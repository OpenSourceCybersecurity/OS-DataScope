using System.Buffers.Binary;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

public sealed class ContentReaderTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ds-content-").FullName;
    private readonly ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Save(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ContentExtraction Read(string name, byte[] bytes) => new FileContentReader(limits).Read(Save(name, bytes), CancellationToken.None);

    private ContentExtraction Read(string name, string text) => Read(name, Encoding.UTF8.GetBytes(text));

    // ---------- plain text ----------

    [Fact]
    public void Reads_utf8_text()
    {
        var r = Read("notes.txt", "Employee SSN: 512-48-3307");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("Employee SSN: 512-48-3307", r.Text);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void Reads_text_with_a_byte_order_mark(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var r = Read("export.csv", [.. encoding.GetPreamble(), .. encoding.GetBytes("Name,SSN\nAvery,512-48-3307")]);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_utf16_without_a_byte_order_mark() =>
        Assert.Contains("SSN", Read("export.csv", Encoding.Unicode.GetBytes("Name,SSN\nAvery,512-48-3307")).Text, StringComparison.Ordinal);

    [Fact]
    public void Falls_back_to_latin1_for_invalid_utf8()
    {
        var r = Read("legacy.txt", Encoding.Latin1.GetBytes("Café SSN 512-48-3307"));
        Assert.Equal("Café SSN 512-48-3307", r.Text);
    }

    [Fact]
    public void Refuses_binary_files_with_a_text_extension()
    {
        var r = Read("data.txt", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0xFF, 0xFF]);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Reads_only_the_start_of_a_large_text_file()
    {
        limits.MaxChars = 1_000;
        var r = Read("big.log", new string('x', 10_000));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal(1_000, r.Text!.Length);
    }

    [Fact]
    public void An_empty_file_is_scanned_with_no_text()
    {
        var r = Read("empty.txt", []);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("", r.Text);
    }

    // ---------- Office ----------

    [Fact]
    public void Reads_word_paragraphs_headers_and_footers()
    {
        var bytes = Fixtures.Zip(
            ("word/document.xml", Fixtures.WordXml("Payroll change form", "SSN: 512-48-3307")),
            ("word/header1.xml", Fixtures.WordXml("CONFIDENTIAL")),
            ("word/footer1.xml", Fixtures.WordXml("Page 1")),
            ("word/media/image1.png", "not text"));
        var r = Read("form.docx", bytes);
        Assert.Contains("Payroll change form\nSSN: 512-48-3307\n", r.Text, StringComparison.Ordinal);
        Assert.Contains("CONFIDENTIAL", r.Text, StringComparison.Ordinal);
        Assert.Contains("Page 1", r.Text, StringComparison.Ordinal);
        // Pictures are read by OCR (D131), never as text: this one is not a real picture, so it is named as unread.
        Assert.DoesNotContain("not text", r.Text, StringComparison.Ordinal);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Some pictures in the document were not read: 1 damaged or in a format this server can't decode.", r.Note);
    }

    [Fact]
    public void Reads_spreadsheet_rows_with_shared_strings_and_numbers()
    {
        var r = Read("staff.xlsx", Fixtures.Xlsx(["Name", "SSN"], ["Avery Okafor", "#512483307"]));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Name\tSSN\t\nAvery Okafor\t512483307\t\n", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spreadsheet_of_itins_under_an_ssn_header_is_found()
    {
        var rows = Enumerable.Range(0, 40).Select(i => new string?[] { $"Person {i}", $"900-77-{2200 + i}" }).ToArray();
        var r = Read("people.xlsx", Fixtures.Xlsx([["Name", "SSN"], .. rows]));
        Assert.Equal(ContentState.Scanned, r.State);
        var f = Assert.Single(TextScanner.Scan(r.Text!).Findings);
        Assert.Equal(("DS-ITIN-01", Confidence.High, 40), (f.RuleId, f.Confidence, f.Count));
    }

    [Fact]
    public void Keeps_spreadsheet_cells_under_their_headers_when_excel_leaves_empty_cells_out()
    {
        var r = Read("sparse.xlsx", Fixtures.Xlsx(["Name", "Dept", "SSN"], ["Avery Okafor", null, "#12483307"], [null, null, "#512483307"]));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Name\tDept\tSSN\t\nAvery Okafor\t\t12483307\t\n\t\t512483307\t\n", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_far_away_cell_reference_adds_only_a_bounded_number_of_empty_columns()
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = $"""<worksheet xmlns="{S}"><sheetData><row r="1"><c r="XFD1"><v>7</v></c><c r="nonsense"><v>8</v></c></row></sheetData></worksheet>""";
        var r = Read("wide.xlsx", Fixtures.Zip(("xl/worksheets/sheet1.xml", sheet)));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal(new string('\t', 64) + "7\t8\t\n", r.Text!.TrimEnd('\n') + "\n");
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B7", 1)]
    [InlineData("Z9", 25)]
    [InlineData("AA1", 26)]
    [InlineData("XFD1048576", 16383)]
    [InlineData("ABCD1", -1)]
    [InlineData("12", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void Reads_the_column_of_a_cell_reference(string? reference, int expected) =>
        Assert.Equal(expected, OfficeOpenXmlReader.ColumnOf(reference));

    [Fact]
    public void Reads_slides_in_natural_order()
    {
        var r = Read("deck.pptx", Fixtures.Pptx((10, "tenth"), (2, "second"), (1, "first")));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("first\n\nsecond\n\ntenth\n\n", r.Text);
    }

    [Fact]
    public void Password_protected_office_files_are_reported_as_encrypted()
    {
        // Office saves them as a compound file holding the encrypted package (D132).
        var locked = CompoundFileBuilder.Build(new CfbData("EncryptionInfo", new byte[200]), new CfbData("EncryptedPackage", new byte[5000]));
        Assert.Equal(ContentState.Encrypted, Read("locked.xlsx", locked).State);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("PK\u0003\u0004 truncated")]
    public void Malformed_office_files_are_unreadable(string content) =>
        Assert.Equal(ContentState.Unreadable, Read("broken.docx", content).State);

    [Fact]
    public void Refuses_xml_with_a_dtd_and_never_resolves_external_entities()
    {
        var secret = Save("secret.txt", Encoding.UTF8.GetBytes("TOP-SECRET-VALUE"));
        var xml = $"""<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file://{secret}">]><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>""";
        var r = Read("xxe.docx", Fixtures.Zip(("word/document.xml", xml)));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Refuses_an_entity_expansion_bomb()
    {
        var xml = """<?xml version="1.0"?><!DOCTYPE l [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]><w:document xmlns:w="w"><w:t>&c;</w:t></w:document>""";
        Assert.Equal(ContentState.Unreadable, Read("lol.docx", Fixtures.Zip(("word/document.xml", xml))).State);
    }

    [Fact]
    public void Refuses_a_decompression_bomb()
    {
        limits.MaxUncompressedBytes = 1024 * 1024;
        var huge = Encoding.UTF8.GetBytes(Fixtures.WordXml(new string(' ', 8 * 1024 * 1024)));
        var bytes = Fixtures.Zip(("word/document.xml", huge));
        Assert.True(bytes.Length < 100_000, "the fixture should be a small file that expands hugely");
        var r = Read("bomb.docx", bytes);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Contains("safety limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_an_entry_that_expands_beyond_its_declared_size()
    {
        var content = Encoding.UTF8.GetBytes(Fixtures.WordXml(new string('a', 200_000)));
        var bytes = Fixtures.Zip(("word/document.xml", content));
        LieAboutUncompressedSize(bytes, 100);
        Assert.Equal(ContentState.Unreadable, Read("liar.docx", bytes).State);
    }

    [Fact]
    public void Refuses_archives_with_too_many_entries_before_reading_them()
    {
        limits.MaxArchiveEntries = 5;
        var entries = Enumerable.Range(0, 10).Select(i => ($"word/header{i}.xml", Fixtures.WordXml("x"))).ToArray();
        var r = Read("many.docx", Fixtures.Zip(entries));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Contains("more parts", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_at_the_character_limit_and_says_so()
    {
        limits.MaxChars = 1_000;
        var r = Read("long.docx", Fixtures.Docx(Enumerable.Repeat(new string('y', 300), 10).ToArray()));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal(1_000, r.Text!.Length);
    }

    // ---------- PDF ----------

    [Fact]
    public void Reads_pdf_text()
    {
        var r = Read("statement.pdf", Fixtures.Pdf("Card 4111 1111 1111 1111 exp 04/28"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("4111 1111 1111 1111", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_only_the_configured_number_of_pages()
    {
        limits.MaxPdfPages = 1;
        var r = Read("two.pdf", Fixtures.Pdf("first page", "second page"));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("1 of 2 pages", r.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("second", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_without_a_text_layer_is_not_reported_as_clean()
    {
        var r = Read("scan.pdf", Fixtures.Pdf(""));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("The PDF has no text and no pictures that could hold text.", r.Note);
    }

    [Theory]
    [InlineData("%PDF-1.7\n garbage that is not a pdf")]
    [InlineData("not a pdf at all")]
    public void Malformed_pdfs_are_unreadable(string content) =>
        Assert.Equal(ContentState.Unreadable, Read("broken.pdf", content).State);

    [Fact]
    public void Refuses_a_pdf_compression_bomb()
    {
        limits.MaxUncompressedBytes = 1024 * 1024;
        var bomb = Fixtures.Zlib(new byte[64 * 1024 * 1024]);
        var r = Read("bomb.pdf", Fixtures.RawPdf(bomb, "FlateDecode"));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Contains("safety limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_compressed_pdf_stream_within_the_limit_is_read()
    {
        var content = Fixtures.Zlib(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (hello) Tj ET"));
        var r = Read("small.pdf", Fixtures.RawPdf(content, "FlateDecode"));
        Assert.NotEqual(ContentState.Unreadable, r.State);
    }

    [Fact]
    public void Run_length_sizes_are_measured_before_decoding()
    {
        // 0x80 marks the end; 0xFF repeats the next byte twice; 0x01 copies two literal bytes.
        Assert.Equal(4, PdfReader.BoundedFilterProvider.RunLengthSize([0xFF, 0x41, 0x01, 0x42, 0x43, 0x80, 0x00]));
    }

    // ---------- skipping and failures ----------

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("library.dll")]
    [InlineData("server-2022.iso")]
    [InlineData("disk.vhdx")]
    [InlineData("all-hands.mp4")]
    [InlineData("voicemail.wav")]
    [InlineData("brand.ttf")]
    public void Never_opens_programs_disk_images_media_or_fonts(string name)
    {
        var opened = false;
        var reader = new FileContentReader(limits, _ => { opened = true; return new MemoryStream(); });
        var r = reader.Read(Save(name, [1, 2, 3]), CancellationToken.None);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.False(opened);
    }

    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x0004_0000)]
    [InlineData((FileAttributes)0x0040_0000)]
    public void Never_opens_offline_or_cloud_placeholder_files(FileAttributes attribute)
    {
        var r = new FileContentReader(limits).Precheck("budget.xlsx", 10, FileAttributes.Archive | attribute);
        Assert.Equal(ContentState.Offline, r?.State);
    }

    [Fact]
    public void Large_office_files_are_skipped_without_opening()
    {
        var r = new FileContentReader(limits).Precheck("huge.docx", limits.MaxOfficeBytes + 1, FileAttributes.Normal);
        Assert.Equal(ContentState.TooLarge, r?.State);
        Assert.Null(new FileContentReader(limits).Precheck("huge.log", long.MaxValue, FileAttributes.Normal));
    }

    [Fact]
    public void Access_denied_is_reported_not_thrown()
    {
        var path = Save("hr.txt", [65]);
        var r = new FileContentReader(limits, _ => throw new UnauthorizedAccessException()).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.AccessDenied, r.State);
    }

    [Fact]
    public void A_file_that_vanished_is_reported_as_not_found() =>
        Assert.Equal(ContentState.NotFound, new FileContentReader(limits).Read(Path.Combine(dir, "gone.txt"), CancellationToken.None).State);

    [Fact]
    public void Io_errors_are_reported_without_the_exception_message()
    {
        var path = Save("locked.txt", [65]);
        var r = new FileContentReader(limits, _ => throw new IOException("secret detail", unchecked((int)0x80070020))).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.IoError, r.State);
        Assert.Contains("0x80070020", r.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slow_file_times_out()
    {
        limits.PerFileTimeout = TimeSpan.FromMilliseconds(200);
        var path = Save("slow.txt", [65]);
        var r = new FileContentReader(limits, _ => new SlowStream()).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.TimedOut, r.State);
    }

    [Fact]
    public void Cancelling_the_scan_is_not_reported_as_a_timeout()
    {
        var path = Save("a.txt", [65]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new FileContentReader(limits, _ => new SlowStream()).Read(path, cts.Token));
    }

    [Fact]
    public void Reading_never_changes_the_file()
    {
        var path = Save("form.docx", Fixtures.Docx("SSN 512-48-3307"));
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        new FileContentReader(limits).Read(path, CancellationToken.None);
        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Limits_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxChars = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { PerFileTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxPdfPages = 0 }.Validate());
        new ContentLimits().Validate();
    }

    /// <summary>Rewrites the uncompressed size in the local and central headers of a one-entry zip.</summary>
    private static void LieAboutUncompressedSize(byte[] zip, uint size)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            var sig = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (sig == 0x04034B50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 22), size);
            }
            else if (sig == 0x02014B50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), size);
            }
        }
    }

    private sealed class SlowStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1_000_000;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(20);
            buffer[offset] = 65;
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
