using System.Buffers.Binary;
using System.Text;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

/// <summary>Outlook's compressed rich text bodies ([MS-OXRTFCP]) and the text in RTF (D134). All synthetic.</summary>
public sealed class RtfTextTests
{
    /// <summary>RTF stored as Outlook stores it uncompressed: a 16-byte header with "MELA", then the RTF.</summary>
    internal static byte[] Uncompressed(string rtf) => Stored(Encoding.Latin1.GetBytes(rtf), 0x414C454D, Encoding.Latin1.GetByteCount(rtf));

    private static byte[] Stored(byte[] payload, uint type, int rawSize)
    {
        var b = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(payload.Length + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), type);
        payload.CopyTo(b, 16);
        return b;
    }

    /// <summary>
    /// Compressed RTF written by hand: each item is a literal string or a reference into the dictionary (offset,
    /// length), grouped eight to a control byte, as [MS-OXRTFCP] 2.2 describes.
    /// </summary>
    private static byte[] Compressed(int rawSize, params object[] items)
    {
        var payload = new List<byte>();
        var tokens = new List<(bool Reference, byte[] Bytes)>();
        foreach (var item in items)
        {
            if (item is string s)
            {
                tokens.AddRange(Encoding.Latin1.GetBytes(s).Select(c => (false, new[] { c })));
            }
            else
            {
                var (offset, length) = ((int, int))item;
                var reference = (ushort)((offset << 4) | (length - 2));
                tokens.Add((true, [(byte)(reference >> 8), (byte)reference]));
            }
        }
        foreach (var group in tokens.Chunk(8))
        {
            var control = 0;
            for (var i = 0; i < group.Length; i++)
            {
                control |= group[i].Reference ? 1 << i : 0;
            }
            payload.Add((byte)control);
            payload.AddRange(group.SelectMany(t => t.Bytes));
        }
        return Stored([.. payload], 0x75465A4C, rawSize);
    }

    /// <summary>Where the dictionary is first written: just after the 207 bytes it starts with.</summary>
    private const int Start = 207;

    [Fact]
    public void Uncompressed_rtf_is_returned_as_stored()
    {
        Assert.Equal("{\\rtf1 Hello}"u8.ToArray(), RtfText.Decompress(Uncompressed("{\\rtf1 Hello}"), 1_000));
    }

    [Fact]
    public void Literals_are_copied_until_the_end_marker()
    {
        var data = Compressed(100, "{\\rtf1 Hi}", (Start + 10, 2));
        Assert.Equal("{\\rtf1 Hi}", Encoding.Latin1.GetString(RtfText.Decompress(data, 1_000)));
    }

    [Fact]
    public void References_copy_from_the_dictionary_it_starts_with()
    {
        // Offset 0 of the dictionary is "{\rtf1\ansi\mac".
        var data = Compressed(100, (0, 11), " Hi}", (Start + 15, 2));
        Assert.Equal("{\\rtf1\\ansi Hi}", Encoding.Latin1.GetString(RtfText.Decompress(data, 1_000)));
    }

    [Fact]
    public void References_copy_from_what_was_just_written()
    {
        // "ab" then a reference of 6 bytes to it: the copy overlaps itself, repeating the pair.
        var data = Compressed(100, "ab", (Start, 6), (Start + 8, 2));
        Assert.Equal("abababab", Encoding.Latin1.GetString(RtfText.Decompress(data, 1_000)));
    }

    [Fact]
    public void Output_stops_at_the_limit()
    {
        var data = Compressed(1_000_000, "ab", (Start, 17), (Start + 2, 17), (Start + 4, 17));
        Assert.Equal(10, RtfText.Decompress(data, 10).Length);
    }

    [Fact]
    public void Output_stops_at_the_size_the_header_gives()
    {
        var data = Compressed(4, "abcdefgh");
        Assert.Equal("abcd", Encoding.Latin1.GetString(RtfText.Decompress(data, 1_000)));
    }

    [Fact]
    public void Damaged_compressed_rtf_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RtfText.Decompress(new byte[10], 1_000));
        Assert.Throws<InvalidDataException>(() => RtfText.Decompress(Stored([1, 2, 3], 0x12345678, 3), 1_000));
        // A reference whose second byte is missing.
        Assert.Throws<InvalidDataException>(() => RtfText.Decompress(Stored([0x01, 0x00], 0x75465A4C, 100), 1_000));
    }

    [Fact]
    public void Visible_text_is_kept_and_hidden_parts_left_out()
    {
        const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red255\green0\blue0;}{\*\generator Riched20;}"
            + @"\pard Caf\'e9 SSN 512-48-3307\par Price \u8364? 10\tab net\line"
            + @"{\pict\wmetafile8 0123456789abcdef}{\field{\*\fldinst HYPERLINK ""http://example.test""}{\fldrslt link}} end\{x\}}";
        Assert.Equal("Café SSN 512-48-3307\nPrice € 10\tnet\nlink end{x}", RtfText.ToText(Encoding.Latin1.GetBytes(rtf), 1_000));
    }

    [Fact]
    public void Unicode_fallbacks_are_skipped_as_the_document_says()
    {
        const string rtf = @"{\rtf1\uc2 \u21512 ab\u21517 cd!}";
        Assert.Equal("\u5408\u540D!", RtfText.ToText(Encoding.Latin1.GetBytes(rtf), 1_000));
    }

    [Fact]
    public void Text_stops_at_the_limit()
    {
        Assert.Equal("abcde", RtfText.ToText("{\\rtf1 abcdefghij}"u8, 5));
    }

    [Fact]
    public void Unbalanced_and_cut_rtf_is_read_as_far_as_it_goes()
    {
        Assert.Equal("ab", RtfText.ToText("}}{\\rtf1 a}b{"u8, 100));
        Assert.Equal("abc", RtfText.ToText("{\\rtf1 abc\\"u8, 100));
        Assert.Equal("x", RtfText.ToText("{\\rtf1 x\\'4"u8, 100));
    }
}
