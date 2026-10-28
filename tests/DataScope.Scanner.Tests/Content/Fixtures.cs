using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DataScope.Scanner.Tests.Content;

/// <summary>Builds small synthetic Office and PDF files in memory. No real documents are used.</summary>
internal static class Fixtures
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static byte[] Zip(params (string Name, string Content)[] entries) =>
        Zip(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Esc(string s) => SecurityElement.Escape(s);

    public static string WordXml(params string[] paragraphs) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{W}"><w:body>"""
        + string.Concat(paragraphs.Select(p => $"""<w:p><w:r><w:t xml:space="preserve">{Esc(p)}</w:t></w:r></w:p>"""))
        + "</w:body></w:document>";

    public static byte[] Docx(params string[] paragraphs) =>
        Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", WordXml(paragraphs)));

    public static byte[] Pptx(params (int Number, string Text)[] slides) =>
        Zip(slides.Select(s => ($"ppt/slides/slide{s.Number}.xml",
            $"""<p:sld xmlns:p="p" xmlns:a="{A}"><a:p><a:r><a:t>{Esc(s.Text)}</a:t></a:r></a:p></p:sld>""")).ToArray());

    /// <summary>
    /// Rows of cells. Strings go to the shared string table; values starting with '#' are stored as numbers. A null
    /// cell is left out of the file, as Excel does with empty cells, so the next cell's reference skips a column.
    /// </summary>
    public static byte[] Xlsx(params string?[][] rows)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder($"""<worksheet xmlns="{S}"><sheetData>""");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = rows[r][c];
                var reference = $"{(char)('A' + c)}{r + 1}";
                if (cell is null)
                {
                    continue;
                }
                if (cell.StartsWith('#'))
                {
                    sheet.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}"><v>{cell[1..]}</v></c>""");
                }
                else
                {
                    shared.Add(cell);
                    sheet.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" t="s"><v>{shared.Count - 1}</v></c>""");
                }
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        var sst = $"""<sst xmlns="{S}">""" + string.Concat(shared.Select(s => $"<si><t>{Esc(s)}</t></si>")) + "</sst>";
        return Zip(("xl/sharedStrings.xml", sst), ("xl/worksheets/sheet1.xml", sheet.ToString()));
    }

    public static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(612, 792);
            if (text.Length > 0)
            {
                page.AddText(text, 12, new PdfPoint(72, 700), font);
            }
        }
        return builder.Build();
    }

    /// <summary>
    /// A PDF of text pages and picture pages: a page given as bytes is a JPEG filling the page, as a scanner makes it.
    /// </summary>
    public static byte[] PdfPages(params object[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var content in pages)
        {
            var page = builder.AddPage(612, 792);
            switch (content)
            {
                case string text:
                    page.AddText(text, 12, new PdfPoint(72, 700), font);
                    break;
                case byte[] jpeg:
                    page.AddJpeg(jpeg, new PdfRectangle(0, 0, 612, 792));
                    break;
            }
        }
        return builder.Build();
    }

    /// <summary>The synthetic images made by scripts/make-ocr-fixtures.py.</summary>
    public static byte[] Image(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content", "Fixtures", "ocr", name));

    /// <summary>
    /// A PNG whose header claims <paramref name="width"/> by <paramref name="height"/> pixels, with almost no data:
    /// what a decompression bomb looks like before it is decoded.
    /// </summary>
    public static byte[] PngClaiming(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // 8 bits, grayscale
        Chunk(ms, "IHDR", header);
        Chunk(ms, "IDAT", Zlib(new byte[1024]));
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        s.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(typed));
        s.Write(length);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }
        return ~crc;
    }

    /// <summary>A hand-written PDF whose single page content stream is <paramref name="streamBytes"/> with the given filter.</summary>
    public static byte[] RawPdf(byte[] streamBytes, string filter)
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.7\n");
        offsets.Add(ms.Position);
        Write("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        offsets.Add(ms.Position);
        Write("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        offsets.Add(ms.Position);
        Write("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n");
        offsets.Add(ms.Position);
        Write($"4 0 obj << /Length {streamBytes.Length} /Filter /{filter} >>\nstream\n");
        ms.Write(streamBytes);
        Write("\nendstream\nendobj\n");
        var xref = ms.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            Write($"{o:0000000000} 00000 n \n");
        }
        Write($"trailer << /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    public static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data);
        }
        return ms.ToArray();
    }
}
