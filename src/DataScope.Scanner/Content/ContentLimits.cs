namespace DataScope.Scanner.Content;

/// <summary>
/// Safety limits for reading file contents. Every reader stops at these, so one hostile or huge file cannot
/// exhaust memory, disk or time. Bound from the <c>Scanner:Content</c> configuration section.
/// </summary>
public sealed class ContentLimits
{
    /// <summary>Characters of text passed to the detectors per file. Matches the detectors' own cap.</summary>
    public int MaxChars { get; set; } = 1_000_000;

    /// <summary>
    /// Characters of text kept from one Outlook data file (.pst, .ost), read message by message (D134). A mailbox holds
    /// thousands of documents' worth of text, so it has its own, larger budget, which the detectors scan in pieces.
    /// Never less than <see cref="MaxChars"/> (<see cref="MailboxChars"/>).
    /// </summary>
    public int MaxMailboxChars { get; set; } = 5_000_000;

    /// <summary>The character budget for one Outlook data file: <see cref="MaxMailboxChars"/>, at least <see cref="MaxChars"/>.</summary>
    public int MailboxChars => Math.Max(MaxMailboxChars, MaxChars);

    /// <summary>The most text any one file can give: a mailbox's budget, or another file's.</summary>
    public int MaxCharsPerFile => MailboxChars;

    /// <summary>Office files (DOCX, XLSX, PPTX) larger than this are not opened.</summary>
    public long MaxOfficeBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>PDF files larger than this are not opened.</summary>
    public long MaxPdfBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Pages read from one PDF, and from one multi-page TIFF image.</summary>
    public int MaxPdfPages { get; set; } = 200;

    /// <summary>Images larger than this are not opened. Smaller ones are opened to check what they really are.</summary>
    public long MaxImageBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// Read the text in images and scanned PDF pages with OCR (D129). Off, images are opened only to check what they
    /// really are, and are listed as not read.
    /// </summary>
    public bool ReadTextInImages { get; set; } = true;

    /// <summary>
    /// Pictures with more pixels than this are not decoded: the size is read from the image's header first, so an image
    /// that claims to be enormous can't fill memory. 50 million is a full page scanned at 1200 dpi.
    /// </summary>
    public long MaxImagePixels { get; set; } = 50_000_000;

    /// <summary>
    /// Time for reading the text in one file's pictures, shared by all of them (the pages of a scanned PDF, the images
    /// in a zip). At most two thirds of <see cref="PerFileTimeout"/> is used (<see cref="OcrAllowed"/>), so text read
    /// in time is kept and the file is marked partly read, rather than the whole file timing out.
    /// </summary>
    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan OcrAllowed => OcrTimeout < PerFileTimeout * 2 / 3 ? OcrTimeout : PerFileTimeout * 2 / 3;

    /// <summary>Zip files larger than this are not opened.</summary>
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Email files (.eml) larger than this, attachments included, are not opened.</summary>
    public long MaxEmailBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// How many zips and emails deep a file may sit and still be read: 3 reads a zip inside a zip inside a zip, and
    /// no further.
    /// </summary>
    public int MaxNestingDepth { get; set; } = 3;

    /// <summary>Archives listing more entries than this are refused before any entry is read.</summary>
    public int MaxArchiveEntries { get; set; } = 10_000;

    /// <summary>Total bytes that may be decompressed from one Office file or zip, whatever its entries claim.</summary>
    public long MaxUncompressedBytes { get; set; } = 256L * 1024 * 1024;

    public TimeSpan PerFileTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Memory the separate file reader process may use (D127). A file that needs more stops that process, is reported
    /// as unreadable, and the scan carries on with a new one.
    /// </summary>
    public long ReaderMemoryBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Bytes read from the start of a plain text file. Text beyond this is not scanned.</summary>
    public int MaxTextBytes => checked(MaxChars * 4);

    /// <summary>
    /// The limits a full scan reads with (D130): the same protections with more room per file, so long scanned
    /// documents, large PDFs and big archives are read further. Each is at least as generous as the quick-scan value
    /// it starts from, so raising a quick-scan limit never makes full scans shallower. Settings under
    /// <c>Scanner:Content:Full</c> override these.
    /// </summary>
    public ContentLimits ForFullScan() => new()
    {
        MaxChars = MaxChars,
        MaxMailboxChars = Math.Max(MaxMailboxChars, 20_000_000),
        MaxOfficeBytes = Math.Max(MaxOfficeBytes, 200L * 1024 * 1024),
        MaxPdfBytes = Math.Max(MaxPdfBytes, 100L * 1024 * 1024),
        MaxPdfPages = Math.Max(MaxPdfPages, 1_000),
        MaxImageBytes = MaxImageBytes,
        ReadTextInImages = ReadTextInImages,
        MaxImagePixels = MaxImagePixels,
        OcrTimeout = Longer(OcrTimeout, TimeSpan.FromMinutes(2)),
        MaxArchiveBytes = Math.Max(MaxArchiveBytes, 1024L * 1024 * 1024),
        MaxEmailBytes = Math.Max(MaxEmailBytes, 200L * 1024 * 1024),
        MaxNestingDepth = MaxNestingDepth,
        MaxArchiveEntries = Math.Max(MaxArchiveEntries, 50_000),
        MaxUncompressedBytes = Math.Max(MaxUncompressedBytes, 512L * 1024 * 1024),
        PerFileTimeout = Longer(PerFileTimeout, TimeSpan.FromMinutes(3)),
        ReaderMemoryBytes = Math.Max(ReaderMemoryBytes, 2048L * 1024 * 1024),
    };

    private static TimeSpan Longer(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void Validate()
    {
        if (MaxChars is < 1_000 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChars), "Must be between 1,000 and 10,000,000.");
        }
        if (MaxMailboxChars is < 1_000 or > 50_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMailboxChars), "Must be between 1,000 and 50,000,000.");
        }
        if (MaxOfficeBytes < 1 || MaxPdfBytes < 1 || MaxPdfPages < 1 || MaxArchiveEntries < 1 || MaxUncompressedBytes < 1
            || MaxArchiveBytes < 1 || MaxEmailBytes < 1 || MaxImageBytes < 1 || MaxImagePixels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOfficeBytes), "Content limits must be positive.");
        }
        if (MaxNestingDepth is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxNestingDepth), "Must be between 1 and 10.");
        }
        if (ReaderMemoryBytes is < 64L * 1024 * 1024 or > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ReaderMemoryBytes), "Must be between 64 MB and 16 GB.");
        }
        if (PerFileTimeout <= TimeSpan.Zero || PerFileTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(PerFileTimeout), "Must be between 0 and 10 minutes.");
        }
        if (OcrTimeout <= TimeSpan.Zero || OcrTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(OcrTimeout), "Must be between 0 and 10 minutes.");
        }
        if (MaxImagePixels > 500_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxImagePixels), "Must be at most 500 million.");
        }
    }
}
