using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>
/// Reads an Outlook message saved as a .msg file ([MS-OXMSG]) as <see cref="EmailReader"/> reads a saved .eml: the
/// subject, the plain text body (or the HTML body when there is no plain one), messages attached to it, and its
/// attachments of the types Data Scope reads, with the same readers and limits as files on the share. Sender and
/// recipient addresses are left out. Links and images are never fetched, and nothing is written to disk.
/// </summary>
internal static class OutlookMessageReader
{
    private const string Property = "__substg1.0_";
    private const string AttachmentPrefix = "__attach_version1.0_#";

    /// <summary>A storage holding a message's properties as streams, as a .msg file's root does.</summary>
    public static bool IsMessage(CompoundFile file, CompoundEntry storage) =>
        file.Children(storage).Any(e => e.Name.StartsWith(Property, StringComparison.OrdinalIgnoreCase)
            || e.Name.Equals("__properties_version1.0", StringComparison.OrdinalIgnoreCase));

    public static ContentExtraction Read(CompoundFile file, ContentLimits limits, int depth, CancellationToken ct)
    {
        if (depth >= limits.MaxNestingDepth)
        {
            return ContentExtraction.Of(ContentState.Unsupported, $"Zips and emails inside more than {limits.MaxNestingDepth - 1:N0} others are not opened.");
        }
        var inner = new InnerFiles(limits, depth);
        var bytesLeft = limits.MaxUncompressedBytes;
        ReadMessage(file, file.Root, inner, limits, ref bytesLeft, attached: 0, ct);
        return inner.Result("email");
    }

    private static void ReadMessage(CompoundFile file, CompoundEntry storage, InnerFiles inner, ContentLimits limits, ref long bytesLeft, int attached, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var children = file.Children(storage);
        AppendText(inner, Text(file, children, "0037", limits));
        var body = Text(file, children, "1000", limits);
        if (body.Length == 0 && Stream(children, "10130102") is { } html)
        {
            var bytes = file.Read(html, limits.MaxTextBytes, truncate: true);
            body = EmailReader.HtmlToText(PlainTextReader.Decode(bytes, more: false) ?? Encoding.Latin1.GetString(bytes));
        }
        AppendText(inner, body);

        var attachments = children
            .Where(e => e.IsStorage && e.Name.StartsWith(AttachmentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        foreach (var attachment in attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (inner.Stopped)
            {
                return;
            }
            var parts = file.Children(attachment);
            if (Storage(parts, "3701000D") is { } embedded)
            {
                // A message attached to this one, or an embedded object such as a Word file inserted as an icon.
                if (!IsMessage(file, embedded))
                {
                    inner.NotRead();
                }
                else if (attached + 1 >= limits.MaxNestingDepth)
                {
                    inner.Stop();
                }
                else
                {
                    ReadMessage(file, embedded, inner, limits, ref bytesLeft, attached + 1, ct);
                }
                continue;
            }
            if (Stream(parts, "37010102") is not { } data)
            {
                inner.NotRead(); // A link to a file elsewhere, which is never followed.
                continue;
            }
            var name = Text(file, parts, "3707", limits) is { Length: > 0 } longName ? longName : Text(file, parts, "3704", limits);
            ReadAttachment(file, data, name.Length > 0 ? name : "attachment", inner, limits, ref bytesLeft, ct);
        }
    }

    private static void ReadAttachment(CompoundFile file, CompoundEntry data, string name, InnerFiles inner, ContentLimits limits, ref long bytesLeft, CancellationToken ct)
    {
        var kind = FileContentReader.KindOf(name);
        if (FileContentReader.NeverOpened(kind))
        {
            inner.NotRead();
            return;
        }
        // Text and copies are read from the start only, as in a zip.
        var readOnlyStart = kind is FileContentReader.Kind.Text or FileContentReader.Kind.ByContents;
        if (!readOnlyStart && data.Size > FileContentReader.MaxBytes(kind, limits))
        {
            inner.TooLarge();
            return;
        }
        var take = readOnlyStart ? Math.Min(data.Size, limits.MaxTextBytes) : data.Size;
        if (take > bytesLeft)
        {
            inner.Stop();
            return;
        }
        byte[] bytes;
        try
        {
            bytes = file.Read(data, take, truncate: true);
        }
        catch (InvalidDataException)
        {
            inner.Failed();
            return;
        }
        bytesLeft -= bytes.Length;
        using var content = new MemoryStream(bytes, writable: false);
        inner.Read(name, content, truncated: take < data.Size, ct);
    }

    /// <summary>A string property: UTF-16 ("001F") or 8-bit in the message's code page ("001E", read as Windows-1252).</summary>
    private static string Text(CompoundFile file, List<CompoundEntry> children, string tag, ContentLimits limits)
    {
        if (Stream(children, tag + "001F") is { } unicode)
        {
            var bytes = file.Read(unicode, (long)limits.MaxChars * 2, truncate: true);
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).TrimEnd('\0');
        }
        if (Stream(children, tag + "001E") is { } ansi)
        {
            return LegacyText.Windows1252.GetString(file.Read(ansi, limits.MaxChars, truncate: true)).TrimEnd('\0');
        }
        return "";
    }

    private static CompoundEntry? Stream(List<CompoundEntry> children, string id) =>
        children.FirstOrDefault(e => !e.IsStorage && e.Name.Equals(Property + id, StringComparison.OrdinalIgnoreCase));

    private static CompoundEntry? Storage(List<CompoundEntry> children, string id) =>
        children.FirstOrDefault(e => e.IsStorage && e.Name.Equals(Property + id, StringComparison.OrdinalIgnoreCase));

    private static void AppendText(InnerFiles inner, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (inner.Budget.Length > 0)
        {
            inner.Budget.Append("\n\n");
        }
        inner.Budget.Append(text);
        inner.HasOwnText = true;
    }
}
