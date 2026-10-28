using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Ocr;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// Text in images and scanned PDF pages is read by OCR (D129). Every image is synthetic: made by
/// scripts/make-ocr-fixtures.py with made-up names and test numbers, or built here. On Windows the images are decoded
/// by Windows itself and on Linux by Leptonica, so these tests cover both decoders.
/// </summary>
public sealed class ImageTextTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ds-ocr-").FullName;
    private readonly ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ContentExtraction Read(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileContentReader(limits).Read(path, CancellationToken.None);
    }

    private ContentExtraction ReadFixture(string fixture, string? name = null) => Read(name ?? fixture, Fixtures.Image(fixture));

    private static List<FindingCategory> Found(ContentExtraction r) =>
        r.Text is null ? [] : TextScanner.Scan(r.Text).Findings.Select(f => f.Category).ToList();

    // ---------- images ----------

    [Theory]
    [InlineData("form-scan.png", "512-48-3307")]
    [InlineData("form-scan.jpg", "512-48-3307")]
    [InlineData("badge.gif", "512-48-3307")]
    [InlineData("screenshot.png", "512-48-3308")]
    public void The_text_in_an_image_is_read_and_its_numbers_are_found(string fixture, string ssn)
    {
        var r = ReadFixture(fixture);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
        // The note says the text was machine-read, with how sure the reader was.
        Assert.StartsWith("Text was read from the image by OCR (average confidence ", r.Note, StringComparison.Ordinal);
        Assert.EndsWith("%), so a character may have been misread.", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screenshot_with_a_transparent_background_reads_as_dark_text_on_white()
    {
        // Transparent pixels are black underneath; laid over black, the dark text would vanish.
        var r = ReadFixture("screenshot.png");
        Assert.Contains("Jordan Sample", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.PaymentCard, Found(r));
    }

    [Fact]
    public void Every_page_of_a_multi_page_tiff_is_read()
    {
        var r = ReadFixture("fax.tif");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("FAX COVER SHEET", r.Text, StringComparison.Ordinal);
        Assert.Contains("512-48-3309", r.Text, StringComparison.Ordinal); // On the second page.
        Assert.StartsWith("Text was read from 2 pages of the image by OCR", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tiff_beyond_the_page_limit_is_partly_read()
    {
        limits.MaxPdfPages = 1;
        var r = ReadFixture("fax.tif");
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Only the first page of the image was read by OCR.", r.Note);
        Assert.DoesNotContain("512-48-3309", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_first_frame_of_an_animated_gif_is_read_and_that_is_the_whole_image()
    {
        var r = ReadFixture("animated.gif");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_named_as_something_else_is_still_read()
    {
        var r = ReadFixture("form-scan.png", "scan-2024.bak");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_with_no_text_is_read_and_holds_none()
    {
        var r = ReadFixture("no-text.png");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.True(string.IsNullOrWhiteSpace(r.Text));
        Assert.Equal("OCR found no text in the image.", r.Note);
    }

    [Fact]
    public void A_tiny_image_is_not_recognised()
    {
        var r = ReadFixture("tiny.png");
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Null(r.Text);
        Assert.Equal("The image is too small to hold readable text (16 × 16 pixels).", r.Note);
    }

    [Fact]
    public void An_image_claiming_too_many_pixels_is_refused_from_its_header()
    {
        // 100,000 × 100,000 pixels would need 10 GB as grayscale; the file is a few hundred bytes.
        var r = Read("bomb.png", Fixtures.PngClaiming(100_000, 100_000));
        Assert.Equal(ContentState.TooLarge, r.State);
        Assert.Equal("The image is 100,000 × 100,000 pixels, more than the 50 million pixel limit for reading text in images.", r.Note);
    }

    [Fact]
    public void The_pixel_limit_can_be_lowered()
    {
        limits.MaxImagePixels = 1_000_000;
        var r = ReadFixture("form-scan.png");
        Assert.Equal(ContentState.TooLarge, r.State);
        Assert.Contains("1,275 × 1,650 pixels, more than the 1 million pixel limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_image_is_unreadable()
    {
        var damaged = Fixtures.Image("form-scan.png");
        new Random(7).NextBytes(damaged.AsSpan(16, 400)); // The header and the start of the data.
        var r = Read("damaged.png", damaged);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("The image is damaged, or uses a variant of its format that can't be decoded.", r.Note);
    }

    [Fact]
    public void Images_over_the_size_limit_are_not_opened()
    {
        limits.MaxImageBytes = 10_000;
        var r = ReadFixture("form-scan.png", "scan.dat"); // Found to be an image only by its contents.
        Assert.Equal(ContentState.TooLarge, r.State);
    }

    [Fact]
    public void Icons_are_not_read_for_text()
    {
        var r = Read("app.ico", [0x00, 0x00, 0x01, 0x00, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0]);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("The contents are an icon, too small to hold text worth reading.", r.Note);
    }

    [Fact]
    public void Turned_off_images_are_listed_as_not_read_and_tried_again_later()
    {
        limits.ReadTextInImages = false;
        var r = ReadFixture("form-scan.png");
        Assert.Equal(ContentState.NotRead, r.State);
        Assert.Null(r.Text);
        Assert.Equal("Reading text in images is turned off in this server's settings (Scanner:Content:ReadTextInImages).", r.Note);
    }

    // ---------- crooked pages (D131) ----------

    [Theory]
    [InlineData("form-sideways.png")] // Fed in sideways, turned clockwise.
    [InlineData("form-sideways-left.png")] // And anticlockwise.
    [InlineData("form-upside-down.png")]
    [InlineData("form-tilted.png")] // Tilted 4° by the feeder.
    [InlineData("form-tilted.jpg")] // Photographed 7° off level by a phone.
    public void A_page_scanned_crooked_or_the_wrong_way_round_is_still_read(string fixture)
    {
        var r = ReadFixture(fixture);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
        Assert.Contains("Avery Example", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
    }

    [Fact]
    public void The_reading_kept_is_the_sure_one_not_scraps_read_the_wrong_way_up()
    {
        var read = ImageText.Read(Fixtures.Image("form-upside-down.png"), 1, limits, CancellationToken.None);
        Assert.Equal(ImageOutcome.Read, read.Outcome);
        Assert.True(read.Confidence >= ImageText.WellReadConfidence, $"confidence {read.Confidence}");
    }

    // ---------- pictures in Office documents (D131) ----------

    [Theory]
    [InlineData("scan-pasted.docx")]
    [InlineData("scan-pasted.xlsx")]
    [InlineData("scan-pasted.pptx")]
    public void A_scan_pasted_into_an_office_document_is_read(string fixture)
    {
        var r = ReadFixture(fixture);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
        Assert.StartsWith("Text in 1 picture in the document was read by OCR (average confidence ", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_documents_own_text_comes_before_the_text_in_its_pictures()
    {
        var r = ReadFixture("scan-pasted.docx");
        var own = r.Text!.IndexOf("Signed form attached below.", StringComparison.Ordinal);
        Assert.InRange(own, 0, r.Text.IndexOf("512-48-3307", StringComparison.Ordinal));
    }

    private static byte[] DocxWithPictures(params (string Name, byte[] Content)[] pictures) =>
        Fixtures.Zip([("word/document.xml", Encoding.UTF8.GetBytes(Fixtures.WordXml("Quarterly notes."))),
            .. pictures.Select(p => ($"word/media/{p.Name}", p.Content))]);

    [Fact]
    public void A_document_whose_pictures_hold_no_text_is_scanned_without_a_note()
    {
        var r = Read("notes.docx", DocxWithPictures(("image1.png", Fixtures.Image("no-text.png")), ("image2.png", Fixtures.Image("tiny.png"))));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Null(r.Note);
        Assert.Contains("Quarterly notes.", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pictures_in_a_document_wait_for_text_recognition_when_it_is_off()
    {
        limits.ReadTextInImages = false;
        var r = ReadFixture("scan-pasted.docx");
        // The document's own text is still scanned; the picture is named as unread, so the file shows as partly read.
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("Signed form attached below.", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("512-48-3307", r.Text, StringComparison.Ordinal);
        Assert.Equal("The picture in the document was not read: Reading text in images is turned off in this server's settings (Scanner:Content:ReadTextInImages).", r.Note);
    }

    [Fact]
    public void Damaged_and_oversized_pictures_in_a_document_are_counted_and_the_rest_read()
    {
        limits.MaxImageBytes = 40_000;
        var r = Read("hr.docx", DocxWithPictures(
            ("image1.png", Fixtures.Image("form-scan.png")),
            ("image2.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8]),
            ("image3.jpg", Fixtures.Image("form-scan.jpg")))); // 52 KB: over the lowered limit.
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
        Assert.EndsWith("Some pictures in the document were not read: 1 over the size limit, 1 damaged or in a format this server can't decode.", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_so_many_pictures_in_a_document_are_read_largest_first()
    {
        var pictures = Enumerable.Range(1, DocumentPictures.MaxPictures)
            .Select(i => ($"image{i}.png", Fixtures.Image("tiny.png")))
            .Append(("scan.png", Fixtures.Image("form-scan.png")))
            .ToArray();
        var r = Read("deck.docx", DocxWithPictures(pictures));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal); // The scan is the largest, so it is read.
        Assert.EndsWith($"Some pictures in the document were not read: 1 beyond the first {DocumentPictures.MaxPictures}.", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_objects_and_vector_drawings_in_a_document_are_not_opened()
    {
        // An embedded workbook and an EMF drawing: neither is a picture Data Scope reads, so neither is counted.
        var r = Read("memo.docx", Fixtures.Zip(
            ("word/document.xml", Encoding.UTF8.GetBytes(Fixtures.WordXml("Memo."))),
            ("word/embeddings/Microsoft_Excel_Worksheet.xlsx", Fixtures.Image("scan-pasted.xlsx")),
            ("word/media/image1.emf", [1, 0, 0, 0, 0x6C, 0, 0, 0])));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Null(r.Note);
        Assert.DoesNotContain("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pictures_in_a_document_share_the_files_time_for_reading_text_in_images()
    {
        limits.OcrTimeout = TimeSpan.FromMilliseconds(1);
        var r = Read("scans.docx", DocxWithPictures(("image1.png", Fixtures.Image("form-scan.png")), ("image2.jpg", Fixtures.Image("form-scan.jpg"))));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("Quarterly notes.", r.Text, StringComparison.Ordinal);
        Assert.Contains("left when the time for reading text in images ran out", r.Note, StringComparison.Ordinal);
    }

    // ---------- time ----------

    [Fact]
    public void Recognition_stops_when_its_time_is_up()
    {
        // No file clock is running here, so the image gets the whole (tiny) allowance and is cut off part way.
        limits.OcrTimeout = TimeSpan.FromMilliseconds(1);
        var read = ImageText.Read(Fixtures.Image("form-scan.png"), 1, limits, CancellationToken.None);
        Assert.Equal(ImageOutcome.Read, read.Outcome);
        Assert.True(read.Stopped);
    }

    [Fact]
    public void A_file_whose_time_ran_out_is_tried_again_rather_than_marked_unreadable()
    {
        limits.OcrTimeout = TimeSpan.FromMilliseconds(1);
        var r = Read("scans.zip", Fixtures.Zip(("one.png", Fixtures.Image("form-scan.png")), ("two.png", Fixtures.Image("form-scan.png"))));
        Assert.True(r.State is ContentState.TimedOut or ContentState.Partial, r.State.ToString());
        Assert.Contains("ran out", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognition_stops_when_the_scan_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageText.Read(Fixtures.Image("form-scan.png"), 1, limits, cancelled.Token));
    }

    // ---------- inside zips and emails ----------

    [Fact]
    public void Images_in_a_zip_are_read()
    {
        var r = Read("scans.zip", Fixtures.Zip(("hr/new-hire.png", Fixtures.Image("form-scan.png")), ("notes.txt", Encoding.UTF8.GetBytes("See scan."))));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_attached_to_an_email_is_read()
    {
        var png = Convert.ToBase64String(Fixtures.Image("badge.gif"), Base64FormattingOptions.InsertLineBreaks);
        var eml = $"Subject: badge photo\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nAttached.\r\n"
            + $"--b\r\nContent-Type: image/gif\r\nContent-Disposition: attachment; filename=badge.gif\r\nContent-Transfer-Encoding: base64\r\n\r\n{png}\r\n--b--\r\n";
        var r = Read("badge.eml", Encoding.UTF8.GetBytes(eml));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
    }

    // ---------- scanned PDFs ----------

    [Theory]
    [InlineData("scanned.pdf", "512-48-3307")] // A JPEG page (DCTDecode).
    [InlineData("scanned-fax.pdf", "512-48-3309")] // A 1-bit fax page (CCITTFaxDecode).
    public void A_scanned_pdf_is_read_by_ocr(string fixture, string ssn)
    {
        var r = ReadFixture(fixture);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
        Assert.StartsWith("Text on 1 page was read from pictures by OCR (average confidence ", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_pages_without_text_are_read_by_ocr()
    {
        var pdf = Fixtures.PdfPages("Quarterly benefits report, prepared by the HR team for review.", Fixtures.Image("form-scan.jpg"));
        var r = Read("report.pdf", pdf);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Quarterly benefits report", r.Text, StringComparison.Ordinal);
        Assert.Contains("512-48-3307", r.Text, StringComparison.Ordinal);
        Assert.StartsWith("Text on 1 page was read from pictures by OCR", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_with_text_on_every_page_is_not_read_by_ocr()
    {
        var r = Read("letter.pdf", Fixtures.Pdf("Dear Avery, your benefits enrolment is confirmed for the coming year."));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Null(r.Note);
    }

    [Fact]
    public void A_scanned_pdf_waits_for_text_recognition_when_it_is_off()
    {
        limits.ReadTextInImages = false;
        var r = ReadFixture("scanned.pdf");
        Assert.Equal(ContentState.NotRead, r.State);
        Assert.Equal("The PDF's pages are pictures. Reading text in images is turned off in this server's settings (Scanner:Content:ReadTextInImages).", r.Note);
    }

    [Fact]
    public void A_pdf_with_text_and_unread_page_pictures_is_partial()
    {
        limits.ReadTextInImages = false;
        var r = Read("report.pdf", Fixtures.PdfPages("Quarterly benefits report, prepared by the HR team for review.", Fixtures.Image("form-scan.jpg")));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.StartsWith("Some page pictures were not read: Reading text in images is turned off", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_page_pictures_over_the_pixel_limit_are_not_decoded()
    {
        limits.MaxImagePixels = 1_000_000;
        var r = ReadFixture("scanned.pdf");
        Assert.Equal(ContentState.TooLarge, r.State);
        Assert.Equal("The PDF's pages are pictures that could not be read: 1 over the 1 million pixel limit.", r.Note);
    }

    // ---------- sizes and limits ----------

    [Theory]
    [InlineData(760, 170, 1520, 340)] // A screenshot: enlarged twice over.
    [InlineData(1275, 1650, 1275, 1650)] // A page at 150 dpi: as it is.
    [InlineData(6000, 8000, 3000, 4000)] // A 48 megapixel photo: shrunk to 12.
    [InlineData(20, 10, 40, 20)]
    public void Pictures_are_recognised_at_a_size_that_reads_well(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ImageText.RecognitionSize(width, height));
    }

    [Fact]
    public void Limits_for_images_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxImagePixels = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxImagePixels = 600_000_000 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { OcrTimeout = TimeSpan.Zero }.Validate());
        // Text recognition always stops short of the file's own limit, so what it read is kept.
        Assert.Equal(TimeSpan.FromSeconds(20), new ContentLimits().OcrAllowed);
        Assert.Equal(TimeSpan.FromSeconds(6), new ContentLimits { PerFileTimeout = TimeSpan.FromSeconds(9) }.OcrAllowed);
    }

    [Fact]
    public void Missing_libraries_are_explained_for_an_administrator()
    {
        var why = OcrLibraries.Unavailable(new DllNotFoundException());
        Assert.StartsWith("Text in images is not read on this ", why, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            // The test run has the libraries next to it, and CI runners have the Visual C++ runtime.
            Assert.Contains("did not load (DllNotFoundException)", why, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("apt install libtesseract5", why, StringComparison.Ordinal);
        }
    }
}
