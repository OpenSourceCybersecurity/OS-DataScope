using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads the start of a text file and decodes it: byte-order mark if present, then strict UTF-8, then Latin-1.
/// Files that look binary are refused rather than scanned as noise.
/// </summary>
internal static class PlainTextReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ContentExtraction Read(Stream stream, ContentLimits limits, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(limits.MaxTextBytes, Math.Max(0, stream.Length))];
        var filled = 0;
        while (filled < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var n = stream.Read(buffer, filled, Math.Min(1 << 20, buffer.Length - filled));
            if (n == 0)
            {
                break;
            }
            filled += n;
        }
        var more = stream.Length > filled;
        var text = Decode(buffer.AsSpan(0, filled), more);
        if (text is null)
        {
            return ContentExtraction.Of(ContentState.Unsupported, "The file is binary, not text.");
        }
        if (more || text.Length > limits.MaxChars)
        {
            var cut = Math.Min(text.Length, limits.MaxChars);
            return new(ContentState.Partial, text[..cut], $"Only the first {cut:N0} characters were scanned.");
        }
        return new(ContentState.Scanned, text, null);
    }

    /// <summary>Null when the bytes look binary.</summary>
    internal static string? Decode(ReadOnlySpan<byte> bytes, bool more)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return DecodeWith(Encoding.UTF8, bytes[3..], more);
        }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return DecodeWith(Encoding.UTF32, bytes[4..], more);
        }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return DecodeWith(Encoding.Unicode, bytes[2..], more);
        }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return DecodeWith(Encoding.BigEndianUnicode, bytes[2..], more);
        }

        var sample = bytes[..Math.Min(bytes.Length, 8192)];
        var nulls = sample.Count((byte)0);
        if (nulls > 0)
        {
            // UTF-16 without a byte-order mark puts a zero in every other byte of ASCII text.
            var oddNulls = 0;
            for (var i = 1; i < sample.Length; i += 2)
            {
                oddNulls += sample[i] == 0 ? 1 : 0;
            }
            return oddNulls * 10 >= sample.Length / 2 * 9 && nulls - oddNulls < sample.Length / 20
                ? DecodeWith(Encoding.Unicode, bytes, more)
                : null;
        }

        try
        {
            return DecodeWith(StrictUtf8, bytes, more);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string DecodeWith(Encoding encoding, ReadOnlySpan<byte> bytes, bool more)
    {
        // When the file continues past what was read, the last character may be cut in half; leave it out.
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, chars, flush: !more);
        return new string(chars, 0, count);
    }
}
