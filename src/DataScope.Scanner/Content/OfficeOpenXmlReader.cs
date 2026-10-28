using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content;

public enum OfficeFormat
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>
/// Pulls visible text out of DOCX, XLSX and PPTX files, and the text in pictures pasted into them by OCR
/// (<see cref="DocumentPictures"/>). Only the parts that hold document text and pictures are read; macros, embedded
/// objects and external links are never opened or followed. Archive and XML parsing are bounded: the entry
/// count is checked before the archive is opened, decompressed bytes are capped whatever the headers claim, and DTDs
/// and external entities are refused.
/// </summary>
internal static class OfficeOpenXmlReader
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>OLE compound file. A DOCX/XLSX/PPTX in this wrapper is password-protected.</summary>
    internal static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
        MaxCharactersFromEntities = 1024,
    };

    public static ContentExtraction Read(Stream stream, OfficeFormat format, ContentLimits limits, CancellationToken ct)
    {
        Span<byte> head = stackalloc byte[8];
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (head[..headLength].SequenceEqual(OleMagic))
        {
            return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
        }
        if (!head[..headLength].StartsWith(ZipMagic))
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is not a valid Office document.");
        }

        var declaredEntries = DeclaredEntryCount(stream);
        if (declaredEntries is null)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is not a valid Office document.");
        }
        if (declaredEntries > limits.MaxArchiveEntries)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The document holds more parts than the safety limit allows.");
        }
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return ReadText(zip, format, limits, ct);
    }

    private static ContentExtraction ReadText(ZipArchive zip, OfficeFormat format, ContentLimits limits, CancellationToken ct)
    {
        var budget = new TextBudget(limits.MaxChars);
        var bytesLeft = limits.MaxUncompressedBytes;

        Stream Open(ZipArchiveEntry entry)
        {
            if (entry.Length > bytesLeft)
            {
                throw new ContentLimitException("The document expands beyond the safety limit.");
            }
            var limit = entry.Length;
            return new LimitedStream(entry.Open(), limit, n => bytesLeft -= n);
        }

        switch (format)
        {
            case OfficeFormat.Word:
                foreach (var entry in Parts(zip, "word/document.xml", "word/header", "word/footer", "word/footnotes.xml", "word/endnotes.xml", "word/comments.xml"))
                {
                    using var s = Open(entry);
                    ReadParagraphText(s, budget, paragraph: "p", ct);
                }
                break;
            case OfficeFormat.PowerPoint:
                foreach (var entry in Parts(zip, "ppt/slides/slide", "ppt/notesSlides/notesSlide"))
                {
                    using var s = Open(entry);
                    ReadParagraphText(s, budget, paragraph: "p", ct);
                }
                break;
            case OfficeFormat.Excel:
                var shared = new List<string>();
                var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (sharedEntry is not null)
                {
                    using var s = Open(sharedEntry);
                    ReadSharedStrings(s, shared, limits.MaxChars, ct);
                }
                foreach (var entry in Parts(zip, "xl/worksheets/sheet"))
                {
                    using var s = Open(entry);
                    ReadSheet(s, shared, budget, ct);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        var pictures = new DocumentPictures(limits);
        pictures.Read(zip, DocumentPictures.MediaFolder(format), budget, ref bytesLeft, ct);

        var text = budget.ToString();
        if (budget.Full)
        {
            var cut = $"Only the first {text.Length:N0} characters were scanned.";
            return new ContentExtraction(ContentState.Partial, text, pictures.Note is { } also ? $"{cut} {also}" : cut);
        }
        return new ContentExtraction(pictures.Missed ? ContentState.Partial : ContentState.Scanned, text, pictures.Note);
    }

    /// <summary>
    /// Reads the entry count from the end-of-central-directory record, without building the entry list.
    /// Null when the record is missing or the archive uses ZIP64, which no Office file under the size limit needs.
    /// </summary>
    internal static int? DeclaredEntryCount(Stream stream)
    {
        const int EocdSize = 22;
        var tail = (int)Math.Min(stream.Length, EocdSize + ushort.MaxValue);
        if (tail < EocdSize)
        {
            return null;
        }
        var buffer = new byte[tail];
        stream.Position = stream.Length - tail;
        stream.ReadExactly(buffer);
        for (var i = tail - EocdSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == 0x06054B50)
            {
                var total = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 10));
                return total == ushort.MaxValue ? null : total;
            }
        }
        return null;
    }

    /// <summary>Entries whose names match one of the prefixes, in natural order (slide2 before slide10).</summary>
    private static List<ZipArchiveEntry> Parts(ZipArchive zip, params string[] prefixes) =>
        zip.Entries
            .Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.Contains("/_rels/", StringComparison.Ordinal)
                && prefixes.Any(p => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => Array.FindIndex(prefixes, p => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(e => NaturalNumber(e.FullName))
            .ThenBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

    private static long NaturalNumber(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsAsciiDigit).ToArray());
        return digits.Length is > 0 and < 10 ? long.Parse(digits, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Word and PowerPoint: text runs are &lt;w:t&gt; / &lt;a:t&gt;; paragraphs end with &lt;w:p&gt; / &lt;a:p&gt;.</summary>
    private static void ReadParagraphText(Stream s, TextBudget budget, string paragraph, CancellationToken ct)
    {
        using var reader = XmlReader.Create(s, XmlSettings);
        var nodes = 0;
        while (!budget.Full && !reader.EOF)
        {
            if (++nodes % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        AppendText(reader, budget);
                        continue;
                    case "tab":
                        budget.Append('\t');
                        break;
                    case "br" or "cr":
                        budget.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraph)
            {
                budget.Append('\n');
            }
            reader.Read();
        }
        budget.Append('\n');
    }

    private static void ReadSharedStrings(Stream s, List<string> shared, int maxChars, CancellationToken ct)
    {
        using var reader = XmlReader.Create(s, XmlSettings);
        var item = new TextBudget(maxChars);
        var total = 0;
        var nodes = 0;
        while (!reader.EOF)
        {
            if (++nodes % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                item = new TextBudget(maxChars);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                AppendText(reader, item);
                continue;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                var value = item.ToString();
                total += value.Length;
                // Past the character budget the strings could never be emitted anyway; keep indexes stable with empties.
                shared.Add(total <= maxChars ? value : "");
            }
            reader.Read();
        }
    }

    /// <summary>
    /// One line per row, cells separated by tabs. Shared strings are looked up; numbers are kept as stored. Excel leaves
    /// empty cells out, so each cell is placed by its reference (B7 is the second column) to keep columns lined up
    /// under their headers.
    /// </summary>
    private static void ReadSheet(Stream s, List<string> shared, TextBudget budget, CancellationToken ct)
    {
        using var reader = XmlReader.Create(s, XmlSettings);
        string? cellType = null;
        var column = 0;
        var nodes = 0;
        while (!budget.Full && !reader.EOF)
        {
            if (++nodes % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        column = 0;
                        break;
                    case "c":
                        cellType = reader.GetAttribute("t");
                        var at = ColumnOf(reader.GetAttribute("r"));
                        // Bounded so a reference like XFD1 cannot spend the text budget on separators.
                        for (var gap = Math.Min(at - column, MaxColumnGap); gap > 0; gap--)
                        {
                            budget.Append('\t');
                        }
                        column = Math.Max(column, at);
                        break;
                    case "v":
                        var cell = new TextBudget(MaxCellChars);
                        AppendText(reader, cell);
                        var value = cell.ToString();
                        if (cellType == "s")
                        {
                            var ok = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index);
                            value = ok && index < shared.Count ? shared[index] : "";
                        }
                        budget.Append(value);
                        budget.Append('\t');
                        column++;
                        continue;
                    case "t":
                        AppendText(reader, budget);
                        budget.Append('\t');
                        column++;
                        continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                budget.Append('\n');
            }
            reader.Read();
        }
        budget.Append('\n');
    }

    /// <summary>Excel's own limit for one cell.</summary>
    private const int MaxCellChars = 32_767;

    /// <summary>Empty cells filled in before one cell; wider gaps are shortened.</summary>
    private const int MaxColumnGap = 64;

    /// <summary>Zero-based column of a cell reference such as "B7"; -1 when it is missing or malformed.</summary>
    internal static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return -1;
        }
        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                if (++letters > 3)
                {
                    return -1;
                }
                column = (column * 26) + (c - 'A' + 1);
            }
            else
            {
                break;
            }
        }
        return letters == 0 ? -1 : column - 1;
    }

    /// <summary>
    /// Appends an element's text in chunks and leaves the reader after the element. Never materializes one huge
    /// string, so a single enormous text run cannot exhaust memory.
    /// </summary>
    private static void AppendText(XmlReader reader, TextBudget target)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }
        var depth = reader.Depth;
        reader.Read();
        var chunk = new char[4096];
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                int n;
                while (!target.Full && (n = reader.ReadValueChunk(chunk, 0, chunk.Length)) > 0)
                {
                    target.Append(chunk.AsSpan(0, n));
                }
                if (target.Full)
                {
                    reader.Skip();
                    continue;
                }
            }
            reader.Read();
        }
        reader.Read();
    }
}
