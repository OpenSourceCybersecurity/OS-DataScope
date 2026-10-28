using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// Files are read by what their contents are, not their names (D127): unknown extensions, no extension and documents
/// renamed as images are read; binary formats are named and never scanned as text. Every file is synthetic.
/// </summary>
public sealed class ReadByContentsTests : IDisposable
{
    private const string Ssn = "512-48-3307";
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes($"Name,SSN\nAvery Okafor,{Ssn}\n");
    private readonly string dir = Directory.CreateTempSubdirectory("ds-bycontents-").FullName;
    private readonly ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ContentExtraction Read(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileContentReader(limits).Read(path, CancellationToken.None);
    }

    private static byte[] Bytes(byte[] head, int size = 4096, int seed = 3)
    {
        var bytes = new byte[Math.Max(size, head.Length)];
        new Random(seed).NextBytes(bytes);
        head.CopyTo(bytes, 0);
        return bytes;
    }

    // ---------- names Data Scope doesn't know ----------

    [Theory]
    [InlineData("customers.dat")]
    [InlineData("export.2019")]
    [InlineData("README")]
    [InlineData("payroll.csv_old")]
    [InlineData("hr.txt1")]
    public void Reads_text_whatever_its_name(string name)
    {
        var r = Read(name, Csv);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("q3")]
    [InlineData("statement.dat")]
    public void Reads_documents_whatever_their_name(string name)
    {
        Assert.Contains(Ssn, Read(name, Fixtures.Xlsx([["Name", "SSN"], ["Avery", Ssn]])).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read(name + "-pdf", Fixtures.Pdf($"SSN {Ssn}")).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read(name + "-zip", Fixtures.Zip(("people.csv", Csv))).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_binary_formats_are_named_never_scanned_as_text()
    {
        var r = Read("ledger.qbw", Bytes([0x00, 0x01, 0x02], 64 * 1024));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Null(r.Text);
        Assert.Contains("binary format", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_without_zero_bytes_is_still_not_text()
    {
        // High bytes and control characters decode as Latin-1, but no text looks like this.
        var noise = Enumerable.Range(0, 8192).Select(i => (byte)(1 + (i * 37 % 254))).ToArray();
        var r = Read("cache.blob", noise);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void Text_in_a_legacy_encoding_is_still_text()
    {
        var r = Read("clients.dat", Encoding.Latin1.GetBytes($"Café Müller;{Ssn};Zürich\n"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, "a gzip archive")]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, "a Windows program or library")]
    [InlineData(new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00 }, "a SQLite database")]
    public void Names_the_binary_formats_it_recognises(byte[] magic, string what)
    {
        var r = Read("unknown", Bytes(magic));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal($"The contents are {what}, which Data Scope does not read.", r.Note);
    }

    [Fact]
    public void Recognises_an_access_database()
    {
        var head = new byte[20];
        head[1] = 1;
        Encoding.ASCII.GetBytes("Standard ACE DB").CopyTo(head, 4);
        Assert.Equal("The contents are an Access database, which Data Scope does not read.", Read("customers.accdb", Bytes(head)).Note);
    }

    [Fact]
    public void Older_office_files_and_archives_are_read_as_what_they_are()
    {
        // Real ones are read in LegacyFormatTests (D132); these only start like them, so they are damaged.
        Assert.Equal(ContentState.Unreadable, Read("message.msg", Bytes([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])).State);
        Assert.Equal("The 7-Zip archive is damaged or not in the format its name suggests.", Read("unknown", Bytes([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])).Note);
        Assert.Equal("The RAR archive is damaged or not in the format its name suggests.", Read("unknown", Bytes([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07])).Note);
    }

    // ---------- images ----------

    [Theory]
    [InlineData("scan.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("chart.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("fax.tif", new byte[] { 0x49, 0x49, 0x2A, 0x00 })]
    [InlineData("logo.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    public void Images_with_damaged_contents_are_unreadable(string name, byte[] magic)
    {
        // Real images are read by OCR (ImageTextTests); these start like images and then hold noise.
        var r = Read(name, Bytes(magic));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("The image is damaged, or uses a variant of its format that can't be decoded.", r.Note);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "a JPEG image", "Jpeg")]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, "a TIFF image", "Tiff")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, "an icon", "Icon")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "a WebP image", "WebP")]
    [InlineData(new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "a HEIC or AVIF image", "Heif")]
    [InlineData(new byte[] { 0x42, 0x4D, 1, 2, 3, 4, 0, 0, 0, 0 }, "a bitmap image", "Bmp")]
    public void Recognises_image_formats_by_their_first_bytes(byte[] magic, string what, string format)
    {
        var sniffed = ContentSniffer.Sniff(new MemoryStream(Bytes(magic)), limits);
        Assert.Equal(new Sniffed(FileContentReader.Kind.Image, what, Enum.Parse<ImageFormat>(format)), sniffed);
    }

    [Fact]
    public void Heic_photos_are_named_when_there_is_no_decoder()
    {
        var r = Read("b.heic", Bytes([0, 0, 0, 24, .. "ftypheic"u8]));
        if (r.State == ContentState.Unsupported)
        {
            Assert.Equal("The contents are a HEIC or AVIF image, which this server has no decoder for.", r.Note);
        }
        else
        {
            // A Windows server with the HEIF extension installed decodes HEIC, and finds this one damaged.
            Assert.True(OperatingSystem.IsWindows());
            Assert.Equal(ContentState.Unreadable, r.State);
        }
    }

    [Fact]
    public void A_spreadsheet_renamed_as_a_photo_is_read()
    {
        var r = Read("holiday.jpg", Fixtures.Xlsx([["Name", "SSN"], ["Avery", Ssn]]));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read("notes.png", Csv).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_images_are_not_opened()
    {
        limits.MaxImageBytes = 1_000;
        var opened = false;
        var reader = new FileContentReader(limits, _ => { opened = true; return new MemoryStream(); });
        var skip = reader.Precheck("site-photo.jpg", 5_000, FileAttributes.Archive);
        Assert.Equal(ContentState.TooLarge, skip?.State);
        Assert.False(opened);
    }

    [Fact]
    public void Video_and_audio_found_by_contents_are_named()
    {
        Assert.Contains("video or audio", Read("clip", Bytes([0, 0, 0, 24, .. "ftypisom"u8])).Note, StringComparison.Ordinal);
        Assert.Contains("audio", Read("memo", Bytes([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8])).Note, StringComparison.Ordinal);
    }

    // ---------- inside zips ----------

    [Fact]
    public void Files_in_a_zip_with_unknown_names_are_read_by_contents()
    {
        var r = Read("export.zip", Fixtures.Zip(("people.dat", Csv), ("README", Encoding.UTF8.GetBytes("Nothing here."))));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Programs_and_media_in_a_zip_are_not_opened()
    {
        var r = Read("bundle.zip", Fixtures.Zip(("people.csv", Csv), ("setup.exe", [0x4D, 0x5A]), ("intro.mp4", [0, 1]), ("logo.png", [0x89, 0x50])));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Read 1 of 4 files in the zip: 3 of types not read.", r.Note);
    }

    // ---------- the sniffer on its own ----------

    [Fact]
    public void Looks_binary_only_for_bytes_text_never_holds()
    {
        Assert.False(ContentSniffer.LooksBinary(new MemoryStream(Encoding.UTF8.GetBytes("a,b\r\n1,2\t3\f"))));
        Assert.False(ContentSniffer.LooksBinary(new MemoryStream(Encoding.Unicode.GetBytes("﻿Name,SSN"))));
        Assert.False(ContentSniffer.LooksBinary(new MemoryStream([])));
        Assert.True(ContentSniffer.LooksBinary(new MemoryStream([0x00, 0x00, 0x01, 0x02, 0x00, 0x05])));
    }
}
