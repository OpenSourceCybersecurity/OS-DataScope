using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>
/// Cell values from Excel 97-2003 workbooks (.xls, .xlt) ([MS-XLS], BIFF8), read from the Workbook stream as a run
/// of records. Strings come from the shared string table; numbers are written as stored, so an SSN typed as a number
/// is still found; the last calculated value of a formula is used and the formula itself is never evaluated. As in
/// <see cref="OfficeOpenXmlReader"/>, each row is one line with its cells separated by tabs and placed by column.
/// Macros, charts and embedded objects are never opened.
/// </summary>
internal static class ExcelBinaryReader
{
    private const ushort Bof = 0x0809;
    private const ushort Eof = 0x000A;
    private const ushort FilePass = 0x002F;
    private const ushort Sst = 0x00FC;
    private const ushort Continue = 0x003C;
    private const ushort LabelSst = 0x00FD;
    private const ushort Label = 0x0204;
    private const ushort Number = 0x0203;
    private const ushort Rk = 0x027E;
    private const ushort MulRk = 0x00BD;
    private const ushort Formula = 0x0006;
    private const ushort FormulaString = 0x0207;
    private const ushort Biff8 = 0x0600;

    /// <summary>Empty cells filled in before one cell; wider gaps are shortened.</summary>
    private const int MaxColumnGap = 64;

    public static ContentExtraction Read(CompoundFile file, CompoundEntry workbook, ContentLimits limits, CancellationToken ct)
    {
        var data = file.Read(workbook, limits.MaxUncompressedBytes);
        if (Next(data, 0) is not { Type: Bof, Length: >= 2 } first)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        if (U16(data, first.Offset) != Biff8)
        {
            return ContentExtraction.Of(ContentState.Unsupported, "Excel workbooks from Excel 95 and earlier are not read.");
        }

        var budget = new TextBudget(limits.MaxChars);
        var shared = new List<string>();
        var row = -1;
        var column = 0;
        var formulaText = false;

        void Place(int r, int c)
        {
            if (r != row)
            {
                if (row >= 0)
                {
                    budget.Append('\n');
                }
                (row, column) = (r, 0);
            }
            for (var gap = Math.Min(c - column, MaxColumnGap); gap > 0; gap--)
            {
                budget.Append('\t');
            }
            column = Math.Max(column, c);
        }

        void Value(string value)
        {
            budget.Append(value);
            budget.Append('\t');
            column++;
        }

        void Cell(int r, int c, string value)
        {
            Place(r, c);
            Value(value);
        }

        var count = 0;
        var position = 0;
        while (!budget.Full && Next(data, position) is { } record)
        {
            var (type, offset, length) = record;
            if (++count % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            position = offset + length;
            var body = data.AsSpan(offset, length);
            switch (type)
            {
                case FilePass:
                    return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
                case Sst:
                    var table = WithContinues(data, offset, length, out var tableEnd);
                    ReadSharedStrings(table, shared, limits.MaxChars);
                    position = tableEnd;
                    break;
                case Bof or Eof:
                    if (row >= 0)
                    {
                        budget.Append('\n');
                    }
                    (row, column) = (-1, 0);
                    break;
                case LabelSst when length >= 10:
                    var index = BinaryPrimitives.ReadUInt32LittleEndian(body[6..]);
                    Cell(U16(body, 0), U16(body, 2), index < shared.Count ? shared[(int)index] : "");
                    break;
                case Label when length >= 9:
                    Cell(U16(body, 0), U16(body, 2), new Segments(data, [(offset + 6, length - 6)]).ReadString(limits.MaxChars));
                    break;
                case Number when length >= 14:
                    Cell(U16(body, 0), U16(body, 2), Format(BinaryPrimitives.ReadDoubleLittleEndian(body[6..])));
                    break;
                case Rk when length >= 10:
                    Cell(U16(body, 0), U16(body, 2), Format(RkValue(BinaryPrimitives.ReadUInt32LittleEndian(body[6..]))));
                    break;
                case MulRk when length >= 12:
                    // Row, first column, then a format and a value per cell, then the last column.
                    for (var (at, c) = (4, (int)U16(body, 2)); at + 6 <= length - 2; at += 6, c++)
                    {
                        Cell(U16(body, 0), c, Format(RkValue(BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 2)..]))));
                    }
                    break;
                case Formula when length >= 14:
                    // The last calculated value: a number, or (marked by 0xFFFF in its top bytes) a type, where 0 means
                    // the text follows in the next record.
                    formulaText = U16(body, 12) == 0xFFFF && body[6] == 0;
                    if (formulaText)
                    {
                        Place(U16(body, 0), U16(body, 2));
                    }
                    else if (U16(body, 12) != 0xFFFF)
                    {
                        Cell(U16(body, 0), U16(body, 2), Format(BinaryPrimitives.ReadDoubleLittleEndian(body[6..])));
                    }
                    break;
                case FormulaString when formulaText && length >= 3:
                    formulaText = false;
                    Value(WithContinues(data, offset, length, out var stringEnd).ReadString(limits.MaxChars));
                    position = stringEnd;
                    break;
            }
        }
        var text = budget.ToString();
        return budget.Full
            ? new ContentExtraction(ContentState.Partial, text, $"Only the first {text.Length:N0} characters were scanned.")
            : new ContentExtraction(ContentState.Scanned, text, null);
    }

    /// <summary>A record's body followed by those of the CONTINUE records after it, and where the last of them ends.</summary>
    private static Segments WithContinues(byte[] data, int offset, int length, out int end)
    {
        var parts = new List<(int, int)> { (offset, length) };
        end = offset + length;
        while (Next(data, end) is { Type: Continue } more)
        {
            parts.Add((more.Offset, more.Length));
            end = more.Offset + more.Length;
        }
        return new Segments(data, parts);
    }

    /// <summary>
    /// The record at <paramref name="at"/>: a 2-byte type, a 2-byte length, then the body. Null at the end, or when the
    /// record would run past it.
    /// </summary>
    internal static (ushort Type, int Offset, int Length)? Next(byte[] data, int at)
    {
        if (at < 0 || at + 4 > data.Length)
        {
            return null;
        }
        var length = U16(data, at + 2);
        return at + 4 + length > data.Length ? null : (U16(data, at), at + 4, length);
    }

    /// <summary>
    /// The shared string table: a count, then strings. Strings past the character budget could never be emitted, so
    /// they are kept as empty strings, which keeps the indexes right.
    /// </summary>
    private static void ReadSharedStrings(Segments s, List<string> shared, int maxChars)
    {
        if (!s.TrySkip(4))
        {
            return;
        }
        var unique = s.ReadU32();
        var total = 0L;
        for (var i = 0L; i < unique && !s.AtEnd; i++)
        {
            var value = s.ReadString(maxChars);
            total += value.Length;
            shared.Add(total <= maxChars ? value : "");
        }
    }

    /// <summary>An RK number: a 30-bit integer or the top 30 bits of a double, either one possibly divided by 100.</summary>
    internal static double RkValue(uint rk)
    {
        var value = (rk & 0x02) != 0
            ? (double)((int)rk >> 2)
            : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFF_FFFC) << 32);
        return (rk & 0x01) != 0 ? value / 100 : value;
    }

    /// <summary>Whole numbers without a decimal point or exponent, so a card number stored as a number reads as its digits.</summary>
    internal static string Format(double value) =>
        double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) < 1e17
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);

    private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));

    /// <summary>
    /// A record and its CONTINUE records read as one run of bytes. A string's characters may be split across a
    /// boundary; the next part then starts with a byte saying whether its characters are 8 or 16 bits.
    /// </summary>
    private sealed class Segments(byte[] data, List<(int Offset, int Length)> parts)
    {
        private int part;
        private int at;

        public bool AtEnd
        {
            get
            {
                while (part < parts.Count && at >= parts[part].Length)
                {
                    (part, at) = (part + 1, 0);
                }
                return part >= parts.Count;
            }
        }

        private byte Next()
        {
            if (AtEnd)
            {
                throw new InvalidDataException("A string runs past the end of its record.");
            }
            return data[parts[part].Offset + at++];
        }

        public bool TrySkip(int n)
        {
            for (var i = 0; i < n; i++)
            {
                if (AtEnd)
                {
                    return false;
                }
                at++;
            }
            return true;
        }

        public uint ReadU32() => (uint)(Next() | (Next() << 8) | (Next() << 16) | (Next() << 24));

        private ushort ReadU16() => (ushort)(Next() | (Next() << 8));

        /// <summary>
        /// A string with a 16-bit count and a flags byte, then optional counts of formatting runs and extra data, the
        /// characters (8-bit ones are Latin-1), and the formatting itself, which is skipped.
        /// </summary>
        public string ReadString(int maxChars)
        {
            int count = ReadU16();
            var flags = Next();
            var wide = (flags & 0x01) != 0;
            var runs = (flags & 0x08) != 0 ? ReadU16() : 0;
            var extra = (flags & 0x04) != 0 ? (int)ReadU32() : 0;
            var text = new StringBuilder(Math.Min(count, maxChars));
            for (var i = 0; i < count; i++)
            {
                if (at >= parts[part].Length && part + 1 < parts.Count)
                {
                    // The characters continue in the next record, which says again how wide they are.
                    (part, at) = (part + 1, 0);
                    wide = (Next() & 0x01) != 0;
                }
                var c = wide ? (char)ReadU16() : (char)Next();
                if (text.Length < maxChars)
                {
                    text.Append(c);
                }
            }
            // Formatting cut short at the end of the table leaves the characters good.
            _ = TrySkip(runs * 4) && TrySkip(extra);
            return text.ToString();
        }
    }
}
