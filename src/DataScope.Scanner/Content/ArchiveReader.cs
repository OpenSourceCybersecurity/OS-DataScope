using System.IO.Compression;
using DataScope.Core.Classification;
using Kind = DataScope.Scanner.Content.FileContentReader.Kind;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads the files inside a zip with the same readers as files on the share. Nothing is written to disk: each entry
/// is decompressed into memory within its type's size limit. The entry count is checked before the archive is
/// opened, the bytes decompressed from one zip are capped whatever the headers claim, password-protected entries are
/// counted but never opened, and zips inside zips stop at <see cref="ContentLimits.MaxNestingDepth"/>.
/// </summary>
internal static class ArchiveReader
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] EmptyZipMagic = [0x50, 0x4B, 0x05, 0x06];

    public static ContentExtraction Read(Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        Span<byte> head = stackalloc byte[8];
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (PackedArchiveReader.IsPacked(head[..headLength]))
        {
            return PackedArchiveReader.Read(stream, limits, depth, ct);
        }
        head = head[..Math.Min(headLength, 4)];
        headLength = head.Length;
        if (!head[..headLength].SequenceEqual(ZipMagic) && !head[..headLength].SequenceEqual(EmptyZipMagic))
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is not a valid zip archive.");
        }
        var declared = OfficeOpenXmlReader.DeclaredEntryCount(stream);
        if (declared is null)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The zip is damaged, or uses the ZIP64 format for very large archives, which is not read.");
        }
        if (declared > limits.MaxArchiveEntries)
        {
            return ContentExtraction.Of(ContentState.Unreadable, $"The zip holds more than {limits.MaxArchiveEntries:N0} files, the safety limit.");
        }
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var inner = new InnerFiles(limits, depth);
        var bytesLeft = limits.MaxUncompressedBytes;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // A folder.
            }
            if (inner.Stopped)
            {
                break;
            }
            // Images are read by OCR like any other image (D129), within the same decompression budget and time.
            var kind = FileContentReader.KindOf(entry.Name);
            if (FileContentReader.NeverOpened(kind))
            {
                inner.NotRead();
                continue;
            }
            if (entry.IsEncrypted)
            {
                inner.Encrypted();
                continue;
            }

            // Text and copies are read from the start only, so a large log in a zip costs no more than one on the share.
            var readOnlyStart = kind is Kind.Text or Kind.ByContents;
            var take = readOnlyStart ? Math.Min(entry.Length, limits.MaxTextBytes) : entry.Length;
            if (!readOnlyStart && entry.Length > FileContentReader.MaxBytes(kind, limits))
            {
                inner.TooLarge();
                continue;
            }
            if (take > bytesLeft)
            {
                inner.Stop();
                break;
            }

            MemoryStream content;
            try
            {
                content = Extract(entry, take, ref bytesLeft, ct);
            }
            catch (InvalidDataException)
            {
                inner.Failed();
                continue;
            }
            catch (ContentLimitException)
            {
                inner.Failed(); // The entry expanded beyond its declared size: a decompression bomb.
                continue;
            }
            using (content)
            {
                inner.Read(entry.Name, content, truncated: take < entry.Length, ct);
            }
        }
        return inner.Result("zip");
    }

    /// <summary>
    /// The first <paramref name="take"/> bytes of an entry. The entry may not expand beyond its declared size, so a
    /// header that lies about the size cannot get past the limits.
    /// </summary>
    private static MemoryStream Extract(ZipArchiveEntry entry, long take, ref long bytesLeft, CancellationToken ct)
    {
        var content = new MemoryStream((int)Math.Min(take, 1 << 20));
        using var source = new LimitedStream(entry.Open(), entry.Length);
        var buffer = new byte[81920];
        while (content.Length < take)
        {
            ct.ThrowIfCancellationRequested();
            var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, take - content.Length));
            if (n == 0)
            {
                break;
            }
            content.Write(buffer, 0, n);
        }
        bytesLeft -= content.Length;
        content.Position = 0;
        return content;
    }
}
