using System.Buffers.Binary;
using System.Text;
using DataScope.Scanner.Content.Legacy;

namespace DataScope.Scanner.Content;

/// <summary>
/// The text of a rich text (RTF) message body, as Outlook stores it: compressed ([MS-OXRTFCP]), then RTF. Used for
/// messages with no plain or HTML body. Both steps run in time proportional to their input and stop at the caller's
/// limit, and pictures, embedded objects, field codes and other hidden parts of the RTF are left out.
/// </summary>
internal static class RtfText
{
    private const uint Compressed = 0x75465A4C; // "LZFu"
    private const uint Uncompressed = 0x414C454D; // "MELA"

    /// <summary>[MS-OXRTFCP] 2.1.2.1: the dictionary every compressed RTF stream starts from.</summary>
    private static ReadOnlySpan<byte> Preload =>
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx"u8;

    private static readonly char[] Cp1252 = LegacyText.Windows1252.GetChars([.. Enumerable.Range(0, 256).Select(b => (byte)b)]);

    /// <summary>Destinations whose contents are not the message's text.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "themedata", "colorschememapping", "datastore",
        "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "mmathPr", "fldinst", "filetbl",
        "revtbl", "pgdsctbl", "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr", "footerf",
    };

    /// <summary>Decompresses Outlook's compressed RTF, up to <paramref name="maxBytes"/> of output.</summary>
    /// <exception cref="InvalidDataException">The data is not compressed RTF, or it is damaged.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data, int maxBytes)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("Compressed RTF is cut short.");
        }
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var limit = (int)Math.Min(raw, (uint)maxBytes);
        var input = data[16..];
        if (type == Uncompressed)
        {
            return input[..Math.Min(input.Length, limit)].ToArray();
        }
        if (type != Compressed)
        {
            throw new InvalidDataException("Not compressed RTF.");
        }
        var dictionary = new byte[4096];
        Preload.CopyTo(dictionary);
        var write = Preload.Length;
        var output = new List<byte>(Math.Min(limit, 1 << 20));
        var at = 0;
        while (at < input.Length && output.Count < limit)
        {
            var control = input[at++];
            for (var bit = 0; bit < 8 && output.Count < limit; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    if (at >= input.Length)
                    {
                        return [.. output];
                    }
                    var literal = input[at++];
                    output.Add(literal);
                    dictionary[write] = literal;
                    write = (write + 1) & 0xFFF;
                    continue;
                }
                if (at + 2 > input.Length)
                {
                    throw new InvalidDataException("Compressed RTF is cut short.");
                }
                var reference = BinaryPrimitives.ReadUInt16BigEndian(input[at..]);
                at += 2;
                var offset = reference >> 4;
                if (offset == write)
                {
                    return [.. output]; // The end marker.
                }
                var length = (reference & 0xF) + 2;
                for (var i = 0; i < length && output.Count < limit; i++)
                {
                    var b = dictionary[(offset + i) & 0xFFF];
                    output.Add(b);
                    dictionary[write] = b;
                    write = (write + 1) & 0xFFF;
                }
            }
        }
        return [.. output];
    }

    /// <summary>The visible text of an RTF document, up to <paramref name="maxChars"/> characters.</summary>
    public static string ToText(ReadOnlySpan<byte> rtf, int maxChars)
    {
        var text = new StringBuilder();
        // For each open group: whether its contents are hidden, and how many characters follow each \u as its fallback.
        var groups = new Stack<(bool Hidden, int Skip)>();
        var hidden = false;
        var skipAfterUnicode = 1;
        var pendingSkip = 0;
        var groupStart = false;
        var i = 0;
        while (i < rtf.Length && text.Length < maxChars)
        {
            var c = rtf[i];
            switch (c)
            {
                case (byte)'{':
                    groups.Push((hidden, skipAfterUnicode));
                    groupStart = true;
                    pendingSkip = 0;
                    i++;
                    continue;
                case (byte)'}':
                    if (groups.Count > 0)
                    {
                        (hidden, skipAfterUnicode) = groups.Pop();
                    }
                    groupStart = false;
                    pendingSkip = 0;
                    i++;
                    continue;
                case (byte)'\r' or (byte)'\n':
                    i++;
                    continue;
                case (byte)'\\':
                    break;
                default:
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                    }
                    else if (!hidden)
                    {
                        text.Append(Cp1252[c]);
                    }
                    groupStart = false;
                    i++;
                    continue;
            }

            // A control word or symbol.
            i++;
            if (i >= rtf.Length)
            {
                break;
            }
            var next = rtf[i];
            if (!IsLetter(next))
            {
                i++;
                var wasStart = groupStart;
                groupStart = false;
                switch (next)
                {
                    case (byte)'*' when wasStart:
                        hidden = true; // An optional destination: never text.
                        break;
                    case (byte)'\'':
                        if (i + 2 <= rtf.Length && TryHex(rtf.Slice(i, 2), out var code))
                        {
                            Emit(text, hidden, ref pendingSkip, Cp1252[code]);
                        }
                        i = Math.Min(i + 2, rtf.Length);
                        break;
                    case (byte)'\\' or (byte)'{' or (byte)'}':
                        Emit(text, hidden, ref pendingSkip, (char)next);
                        break;
                    case (byte)'~':
                        Emit(text, hidden, ref pendingSkip, ' ');
                        break;
                    case (byte)'_':
                        Emit(text, hidden, ref pendingSkip, '-');
                        break;
                }
                continue;
            }
            var start = i;
            while (i < rtf.Length && IsLetter(rtf[i]) && i - start < 32)
            {
                i++;
            }
            var word = Encoding.ASCII.GetString(rtf[start..i]);
            int? parameter = null;
            var paramStart = i;
            if (i < rtf.Length && (rtf[i] == '-' || IsDigit(rtf[i])))
            {
                i++;
                while (i < rtf.Length && IsDigit(rtf[i]) && i - paramStart < 11)
                {
                    i++;
                }
                if (int.TryParse(rtf[paramStart..i], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var p))
                {
                    parameter = p;
                }
            }
            if (i < rtf.Length && rtf[i] == ' ')
            {
                i++;
            }
            var atStart = groupStart;
            groupStart = false;
            if (atStart && Hidden.Contains(word))
            {
                hidden = true;
                continue;
            }
            switch (word)
            {
                case "par" or "line" or "row" or "sect" or "page":
                    Emit(text, hidden, ref pendingSkip, '\n');
                    break;
                case "tab" or "cell":
                    Emit(text, hidden, ref pendingSkip, '\t');
                    break;
                case "uc" when parameter is >= 0 and < 10:
                    skipAfterUnicode = parameter.Value;
                    break;
                case "u" when parameter is { } u:
                    Emit(text, hidden, ref pendingSkip, (char)(ushort)(short)u);
                    pendingSkip = skipAfterUnicode;
                    break;
            }
        }
        return text.ToString();
    }

    private static void Emit(StringBuilder text, bool hidden, ref int pendingSkip, char c)
    {
        if (pendingSkip > 0)
        {
            pendingSkip--;
            return;
        }
        if (!hidden)
        {
            text.Append(c);
        }
    }

    private static bool IsLetter(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool TryHex(ReadOnlySpan<byte> two, out byte value) =>
        byte.TryParse(two, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
}
