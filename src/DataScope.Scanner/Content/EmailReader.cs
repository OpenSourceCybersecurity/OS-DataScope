using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads a saved email (.eml, RFC 5322 with MIME): the subject, the text and HTML bodies, attached emails, and
/// attachments of the types Data Scope reads, with the same readers as files on the share. Addresses and other headers
/// are left out. Links and images are never fetched, scripts never run, and nothing is written to disk. MIME nesting
/// is capped, and attachments count towards the same nesting limit as zips.
/// </summary>
internal static partial class EmailReader
{
    /// <summary>Multipart sections inside each other, within one email. Real mail rarely goes past five.</summary>
    private const int MaxMimeDepth = 20;

    static EmailReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ContentExtraction Read(Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer); // Within MaxEmailBytes, checked before this is called.
        // Latin-1 maps every byte to one character and back, so the structure can be parsed as text and each part's
        // bytes recovered exactly for decoding in its own character set.
        var raw = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var inner = new InnerFiles(limits, depth);
        ReadEntity(raw, inner, mimeDepth: 0, ct);
        return inner.Result("email");
    }

    private static void ReadEntity(string raw, InnerFiles inner, int mimeDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (inner.Stopped)
        {
            return;
        }
        if (mimeDepth > MaxMimeDepth)
        {
            inner.Stop();
            return;
        }
        var (headers, body) = Split(raw);
        var type = Parameters(headers.GetValueOrDefault("content-type") ?? "text/plain");
        var disposition = Parameters(headers.GetValueOrDefault("content-disposition") ?? "");
        var mediaType = type.Value.ToLowerInvariant();

        // The subject of the message itself and of attached messages. Other headers hold addresses and routing.
        if (headers.GetValueOrDefault("subject") is { } subject && (mimeDepth == 0 || IsMessage(headers)))
        {
            AppendText(inner, DecodeWords(subject));
        }

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            if (type.Get("boundary") is not { Length: > 0 } boundary)
            {
                AppendText(inner, body); // Malformed: read what is there as text.
                return;
            }
            foreach (var part in Parts(body, boundary))
            {
                ReadEntity(part, inner, mimeDepth + 1, ct);
            }
            return;
        }

        var bytes = Decode(headers.GetValueOrDefault("content-transfer-encoding"), body);
        if (bytes is null)
        {
            inner.Failed();
            return;
        }
        if (mediaType == "message/rfc822")
        {
            ReadEntity(Encoding.Latin1.GetString(bytes), inner, mimeDepth + 1, ct);
            return;
        }

        var fileName = (disposition.Get("filename") ?? type.Get("name")) is { } name ? DecodeWords(name) : null;
        var isAttachment = disposition.Value.Equals("attachment", StringComparison.OrdinalIgnoreCase);
        var isText = mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType.Length == 0;
        var isImage = mediaType.StartsWith("image/", StringComparison.Ordinal);
        if (isImage)
        {
            // Attached or shown in the message, such as a pasted screenshot: its text is read by OCR (D129).
            using var picture = new MemoryStream(bytes, writable: false);
            inner.Read(fileName ?? "picture", picture, truncated: false, ct);
        }
        else if (fileName is not null && (isAttachment || !isText))
        {
            using var content = new MemoryStream(bytes, writable: false);
            inner.Read(fileName, content, truncated: false, ct);
        }
        else if (mediaType == "text/html")
        {
            AppendText(inner, HtmlToText(Charset(bytes, type.Get("charset"))));
        }
        else if (isText)
        {
            AppendText(inner, Charset(bytes, type.Get("charset")));
        }
        else
        {
            inner.NotRead();
        }
    }

    private static bool IsMessage(Dictionary<string, string> headers) =>
        headers.ContainsKey("from") || headers.ContainsKey("date") || headers.ContainsKey("message-id");

    private static void AppendText(InnerFiles inner, string text)
    {
        if (text.Length == 0)
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

    /// <summary>Headers (names in lower case, folded lines joined, first occurrence kept) and the body.</summary>
    internal static (Dictionary<string, string> Headers, string Body) Split(string raw)
    {
        const int MaxHeaders = 1000;
        const int MaxHeaderChars = 16_384;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var value = new StringBuilder();

        void Flush()
        {
            if (name is not null && headers.Count < MaxHeaders)
            {
                headers.TryAdd(name, value.ToString().Trim());
            }
            name = null;
            value.Clear();
        }

        var position = 0;
        while (position < raw.Length)
        {
            var next = raw.IndexOf('\n', position);
            var lineEnd = next < 0 ? raw.Length : next;
            var line = raw.AsSpan(position, lineEnd - position).TrimEnd('\r');
            position = next < 0 ? raw.Length : next + 1;
            if (line.IsEmpty)
            {
                Flush();
                return (headers, raw[position..]);
            }
            if (line[0] is ' ' or '\t')
            {
                if (name is not null && value.Length < MaxHeaderChars)
                {
                    value.Append(' ').Append(line.Trim());
                }
                continue;
            }
            Flush();
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                name = line[..colon].Trim().ToString().ToLowerInvariant();
                value.Append(line[(colon + 1)..]);
            }
        }
        Flush();
        return (headers, ""); // Headers only, no body.
    }

    /// <summary>The parts between "--boundary" lines, up to the closing "--boundary--".</summary>
    internal static List<string> Parts(string body, string boundary)
    {
        var delimiter = "--" + boundary;
        var parts = new List<string>();
        var start = -1;
        var position = 0;
        while (position < body.Length)
        {
            var next = body.IndexOf('\n', position);
            var lineEnd = next < 0 ? body.Length : next;
            var line = body.AsSpan(position, lineEnd - position).TrimEnd('\r').TrimEnd(' ');
            if (line.StartsWith(delimiter, StringComparison.Ordinal) && line[delimiter.Length..] is "" or "--")
            {
                if (start >= 0)
                {
                    // The line break before a delimiter belongs to the delimiter, not the part.
                    var end = position > start ? position - 1 : start;
                    if (end > start && body[end - 1] == '\r')
                    {
                        end--;
                    }
                    parts.Add(body[start..end]);
                    start = -1;
                }
                if (line.Length > delimiter.Length)
                {
                    return parts; // The closing "--boundary--".
                }
                start = next < 0 ? body.Length : next + 1;
            }
            position = next < 0 ? body.Length : next + 1;
        }
        if (start >= 0 && start < body.Length)
        {
            parts.Add(body[start..]); // No closing delimiter: the last part runs to the end.
        }
        return parts;
    }

    /// <summary>A header value such as <c>text/plain; charset="utf-8"</c>: the value and its parameters.</summary>
    internal sealed record HeaderValue(string Value, Dictionary<string, string> Parameters)
    {
        public string? Get(string name) => Parameters.GetValueOrDefault(name);
    }

    internal static HeaderValue Parameters(string header)
    {
        var parts = SplitParameters(header);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var name = p[..eq].Trim();
            var value = p[(eq + 1)..].Trim().Trim('"');
            if (name.EndsWith('*'))
            {
                // RFC 2231: filename*=utf-8''payroll%20march.xlsx
                name = name.TrimEnd('*');
                var quote = value.IndexOf("''", StringComparison.Ordinal);
                value = quote >= 0 ? Uri.UnescapeDataString(value[(quote + 2)..]) : value;
            }
            values.TryAdd(name, value);
        }
        return new HeaderValue(parts.Count > 0 ? parts[0].Trim() : "", values);
    }

    private static List<string> SplitParameters(string header)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in header)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            if (c == ';' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The part's bytes after its transfer encoding; null when base64 is too damaged to decode.</summary>
    internal static byte[]? Decode(string? transferEncoding, string body)
    {
        switch (transferEncoding?.Trim().ToLowerInvariant())
        {
            case "base64":
                var clean = new StringBuilder(body.Length);
                foreach (var c in body)
                {
                    if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
                    {
                        clean.Append(c);
                    }
                }
                // Padding is often missing or wrong in saved mail; add what the length needs.
                var length = clean.Length - clean.Length % 4;
                var whole = clean.ToString(0, length);
                var remainder = clean.Length % 4;
                var padded = remainder switch
                {
                    2 => whole + clean.ToString(length, 2) + "==",
                    3 => whole + clean.ToString(length, 3) + "=",
                    _ => whole,
                };
                var bytes = new byte[padded.Length / 4 * 3];
                return Convert.TryFromBase64String(padded, bytes, out var written) ? bytes[..written] : null;
            case "quoted-printable":
                return QuotedPrintable(body, underscoreIsSpace: false);
            default:
                return Encoding.Latin1.GetBytes(body);
        }
    }

    internal static byte[] QuotedPrintable(string text, bool underscoreIsSpace)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\n')
                {
                    i++;
                    continue; // A soft line break.
                }
                if (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')
                {
                    i += 2;
                    continue;
                }
                if (i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
                {
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }
            }
            bytes.Add(underscoreIsSpace && c == '_' ? (byte)' ' : (byte)c);
        }
        return [.. bytes];
    }

    /// <summary>Bytes in the part's declared character set, or as Data Scope reads unlabelled text.</summary>
    internal static string Charset(byte[] bytes, string? charset)
    {
        if (charset is { Length: > 0 })
        {
            try
            {
                var encoding = Encoding.GetEncoding(charset.Trim());
                return encoding.GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // An unknown or unsupported character set: fall through.
            }
        }
        return PlainTextReader.Decode(bytes, more: false) ?? Encoding.Latin1.GetString(bytes);
    }

    /// <summary>RFC 2047 encoded words in headers, such as <c>=?utf-8?B?...?=</c>.</summary>
    internal static string DecodeWords(string value)
    {
        try
        {
            return EncodedWord().Replace(value, m =>
            {
                var data = m.Groups[3].Value;
                byte[]? bytes = m.Groups[2].Value is "B" or "b"
                    ? Decode("base64", data)
                    : QuotedPrintable(data, underscoreIsSpace: true);
                return bytes is null ? m.Value : Charset(bytes, m.Groups[1].Value);
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return value;
        }
    }

    /// <summary>Visible text of an HTML body: scripts, styles and tags removed, entities decoded.</summary>
    internal static string HtmlToText(string html)
    {
        try
        {
            var text = HiddenBlocks().Replace(html, " ");
            text = BlockTags().Replace(text, "\n");
            text = Tags().Replace(text, " ");
            return WebUtility.HtmlDecode(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return WebUtility.HtmlDecode(html); // Pathological markup: scan it with the tags left in.
        }
    }

    [GeneratedRegex(@"=\?([^?\s]{1,64})\?([BbQq])\?([^?\s]{0,4096})\?=", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/table)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]{0,10000}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Tags();
}
