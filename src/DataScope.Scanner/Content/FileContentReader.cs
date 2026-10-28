using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataScope.Core.Classification;
using DataScope.Scanner.Content.Legacy;
using DataScope.Scanner.Content.Ocr;
using DataScope.Scanner.Content.Outlook;
using Microsoft.Win32.SafeHandles;

namespace DataScope.Scanner.Content;

/// <summary>
/// Opens one file read-only and hands it to the right reader. Unsupported types are never opened. Offline files and
/// cloud placeholders are never opened either, so a scan cannot trigger a download or a tape recall. Files are opened
/// with full sharing, so users editing them are not blocked. Windows may still update a file's last-access time.
/// </summary>
public sealed partial class FileContentReader(ContentLimits limits, Func<string, Stream>? open = null) : IContentReader
{
    internal enum Kind
    {
        Text,
        Word,
        Excel,
        PowerPoint,
        Pdf,

        /// <summary>Word, Excel and PowerPoint 97-2003, in the OLE compound file format (D132).</summary>
        LegacyOffice,
        Email,

        /// <summary>An Outlook message saved as a .msg file: a compound file, read like an email (D132).</summary>
        OutlookMessage,

        /// <summary>An Outlook data file (.pst) or offline mailbox (.ost): read message by message (D134).</summary>
        OutlookDataFile,
        Archive,

        /// <summary>
        /// Read by what the contents turn out to be: backup, old and temporary copies, and every file whose name says
        /// nothing Data Scope knows (an unknown extension or none at all).
        /// </summary>
        ByContents,

        /// <summary>Images: opened to check what they really are, and their text read by OCR (D129).</summary>
        Image,

        /// <summary>Programs, disk images, video, audio and fonts: never opened. Their names alone decide.</summary>
        Bulk,

        /// <summary>Only from looking at contents: a binary format Data Scope does not read.</summary>
        Binary,
    }

    private static readonly FrozenDictionary<string, Kind> Kinds = new (Kind Kind, string[] Extensions)[]
    {
        // Plain text, data exports, configuration and scripts. Scripts are read as text only, never run.
        (Kind.Text, [".txt", ".csv", ".tsv", ".log", ".md", ".json", ".xml", ".yaml", ".yml", ".ini", ".conf", ".cfg", ".config",
            ".properties", ".env", ".sql", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".vbs", ".htm", ".html", ".rtf", ".pem", ".key", ".svg"]),
        (Kind.Word, [".docx", ".docm"]),
        (Kind.Excel, [".xlsx", ".xlsm"]),
        (Kind.PowerPoint, [".pptx", ".pptm"]),
        (Kind.Pdf, [".pdf"]),
        (Kind.LegacyOffice, [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot"]),
        (Kind.Email, [".eml"]),
        (Kind.OutlookMessage, [".msg"]),
        (Kind.OutlookDataFile, [".pst", ".ost"]),
        (Kind.Archive, [".zip", ".7z", ".rar"]),
        (Kind.ByContents, [".bak", ".old", ".tmp", ".orig", ".backup", ".bk"]),
        (Kind.Image, [".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".jfif"]),
        (Kind.Bulk, [
            // Programs and installers
            ".exe", ".dll", ".sys", ".msi", ".msp", ".msu", ".cab", ".ocx", ".drv", ".efi", ".scr", ".cpl", ".so", ".dylib", ".lib", ".obj", ".pdb",
            // Disk, virtual machine and backup images
            ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2", ".wim", ".esd", ".gho", ".tib", ".vbk", ".vib",
            // Video and audio
            ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".flv", ".mpg", ".mpeg", ".vob", ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".mid",
            // Fonts
            ".ttf", ".otf", ".woff", ".woff2", ".eot"]),
    }
    .SelectMany(g => g.Extensions.Select(e => KeyValuePair.Create(e, g.Kind)))
    .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, set on cloud placeholders.</summary>
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    private readonly Func<string, Stream> open = open ?? OpenReadOnly;

    /// <summary>
    /// How a file is read, judged from its name: a known extension, or <see cref="Kind.ByContents"/> for any other
    /// name, so "customers.dat", "export.2019" and a file with no extension are all looked at.
    /// </summary>
    internal static Kind KindOf(string fileName) => Kinds.TryGetValue(Path.GetExtension(fileName), out var kind) ? kind : Kind.ByContents;

    /// <summary>Kinds never opened: their name alone is enough to say they are not read.</summary>
    internal static bool NeverOpened(Kind kind) => kind is Kind.Bulk;

    /// <summary>Skip reasons that need no file access: a type never opened, the offline attribute, or too large.</summary>
    public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes)
    {
        var kind = KindOf(fileName);
        if (NeverOpened(kind))
        {
            return NotOpened(kind, fileName);
        }
        if ((attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
        {
            return ContentExtraction.Of(ContentState.Offline, "The file is offline or stored in the cloud. It was not opened, so nothing was downloaded.");
        }
        return TooLarge(kind, size);
    }

    private static ContentExtraction NotOpened(Kind kind, string fileName) => ContentExtraction.Of(ContentState.Unsupported,
        $"Programs, disk images, video, audio and fonts ({Path.GetExtension(fileName).ToLowerInvariant()}) are not opened: they are not documents.");

    public ContentExtraction Read(string fullPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var kind = KindOf(fullPath);
        if (NeverOpened(kind))
        {
            return NotOpened(kind, fullPath);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limits.PerFileTimeout);
        try
        {
            // Attributes can change between the walk and now, for example when a file is moved to the cloud.
            var attributes = File.GetAttributes(fullPath);
            var skip = Precheck(fullPath, 0, attributes);
            if (skip is not null)
            {
                return skip;
            }

            using var stream = open(fullPath);
            using var ocrClock = ImageText.StartFile(limits);
            return ReadAs(kind, stream, limits, depth: 0, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ContentExtraction.Of(ContentState.TimedOut, $"Reading took longer than {limits.PerFileTimeout.TotalSeconds:N0} seconds.");
        }
        catch (UnauthorizedAccessException)
        {
            return ContentExtraction.Of(ContentState.AccessDenied, "Access denied to the scan identity.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ContentExtraction.Of(ContentState.NotFound, "The file was moved or deleted before it could be read.");
        }
        catch (ContentLimitException ex)
        {
            return ContentExtraction.Of(ContentState.Unreadable, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or EndOfStreamException)
        {
            // Exception messages are not stored: parser messages can quote file contents.
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        catch (PathTooLongException)
        {
            return ContentExtraction.Of(ContentState.IoError, "The path is longer than the system allows.");
        }
        catch (IOException ex)
        {
            return ContentExtraction.Of(ContentState.IoError, $"The file could not be read (error 0x{ex.HResult:X8}). It may be locked or the connection dropped.");
        }
    }

    private ContentExtraction? TooLarge(Kind kind, long size) => TooLarge(kind, size, limits);

    internal static ContentExtraction? TooLarge(Kind kind, long size, ContentLimits limits)
    {
        var max = MaxBytes(kind, limits);
        return size > max
            ? ContentExtraction.Of(ContentState.TooLarge, $"Larger than the {max / (1024 * 1024):N0} MB limit for this file type.")
            : null;
    }

    /// <summary>
    /// The largest file of a kind that is opened. Text files, and files read by their contents until those are known,
    /// are read from the start up to the character limit, so they have none.
    /// </summary>
    internal static long MaxBytes(Kind kind, ContentLimits limits) => kind switch
    {
        Kind.Image => limits.MaxImageBytes,
        Kind.Pdf => limits.MaxPdfBytes,
        Kind.Word or Kind.Excel or Kind.PowerPoint or Kind.LegacyOffice => limits.MaxOfficeBytes,
        Kind.Archive => limits.MaxArchiveBytes,
        Kind.Email or Kind.OutlookMessage => limits.MaxEmailBytes,
        _ => long.MaxValue,
    };

    /// <summary>
    /// Reads an opened file, or a file inside a zip or an email, as <paramref name="kind"/>. <paramref name="depth"/>
    /// counts the zips and emails it sits inside, so nesting cannot recurse without end.
    /// </summary>
    internal static ContentExtraction ReadAs(Kind kind, Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        if (kind is Kind.ByContents or Kind.Image)
        {
            return ReadByContents(kind, Seekable(stream), limits, depth, ct);
        }
        if (kind is Kind.Archive or Kind.Email or Kind.OutlookMessage && depth >= limits.MaxNestingDepth)
        {
            return ContentExtraction.Of(ContentState.Unsupported, $"Zips and emails inside more than {limits.MaxNestingDepth - 1:N0} others are not opened.");
        }
        var tooLarge = TooLarge(kind, stream.Length, limits);
        if (tooLarge is not null)
        {
            return tooLarge;
        }
        if (kind is Kind.Word or Kind.Excel or Kind.PowerPoint && StartsWith(stream = Seekable(stream), CompoundFile.Magic))
        {
            // A password-protected file, or an older Office file given a newer name: both are compound files.
            kind = Kind.LegacyOffice;
        }
        return kind switch
        {
            Kind.Text => PlainTextReader.Read(stream, limits, ct),
            Kind.Word => OfficeOpenXmlReader.Read(Seekable(stream), OfficeFormat.Word, limits, ct),
            Kind.Excel => OfficeOpenXmlReader.Read(Seekable(stream), OfficeFormat.Excel, limits, ct),
            Kind.PowerPoint => OfficeOpenXmlReader.Read(Seekable(stream), OfficeFormat.PowerPoint, limits, ct),
            Kind.LegacyOffice or Kind.OutlookMessage => LegacyOfficeReader.Read(Seekable(stream), limits, depth, ct),
            Kind.Pdf => PdfReader.Read(Seekable(stream), limits, ct),
            Kind.Archive => ArchiveReader.Read(Seekable(stream), limits, depth, ct),
            Kind.Email => EmailReader.Read(stream, limits, depth, ct),
            Kind.OutlookDataFile => depth > 0
                ? ContentExtraction.Of(ContentState.Unsupported, "Outlook data files inside zips and emails are not opened.")
                : OutlookDataFileReader.Read(Seekable(stream), limits, depth, ct),
            _ => throw new InvalidOperationException($"{kind} is never read directly."),
        };
    }

    /// <summary>
    /// A file whose name is not trusted is read as whatever its first bytes show it is: a Word, Excel or PowerPoint
    /// file, a PDF, a zip, an email or text. So "payroll.xlsx.bak", a Word autosave named "~WRL0001.tmp", a CSV saved
    /// as "customers.dat", a file with no extension and a spreadsheet renamed "photo.jpg" are all read. Binary formats
    /// Data Scope does not read are recognised from their first bytes and named in the note, never scanned as text.
    /// </summary>
    private static ContentExtraction ReadByContents(Kind named, Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        var sniffed = ContentSniffer.Sniff(stream, limits);
        stream.Position = 0;
        switch (sniffed.Kind)
        {
            case Kind.Image:
                return ReadImage(sniffed, stream, limits, ct);
            case Kind.Binary:
                return ContentExtraction.Of(ContentState.Unsupported, $"The contents are {sniffed.What}, which Data Scope does not read.");
            case Kind.Text when ContentSniffer.LooksBinary(stream):
                stream.Position = 0;
                return ContentExtraction.Of(ContentState.Unsupported, named == Kind.Image
                    ? "The file is named as an image, but its contents are in a binary format Data Scope does not recognise."
                    : "The contents are in a binary format Data Scope does not read, for example a database or a program's own file.");
        }
        stream.Position = 0;
        var read = ReadAs(sniffed.Kind, stream, limits, depth, ct);
        return sniffed.Kind == Kind.Text && read.State == ContentState.Unsupported
            ? ContentExtraction.Of(ContentState.Unsupported, "The contents are in a binary format Data Scope does not read, for example a database or a program's own file.")
            : read;
    }

    /// <summary>
    /// Reads the text in an image by OCR (D129). The note says the text was machine-read, so a finding in it is
    /// weighed knowing a character may have been misread.
    /// </summary>
    private static ContentExtraction ReadImage(Sniffed sniffed, Stream stream, ContentLimits limits, CancellationToken ct)
    {
        if (sniffed.Image == ImageFormat.Icon)
        {
            return ContentExtraction.Of(ContentState.Unsupported, "The contents are an icon, too small to hold text worth reading.");
        }
        if (TooLarge(Kind.Image, stream.Length, limits) is { } tooLarge)
        {
            return tooLarge;
        }
        if (ImageText.Unavailable(limits) is { } why)
        {
            // Not a verdict on the file: it is read again on every scan, so it is read once text recognition works.
            return ContentExtraction.Of(ContentState.NotRead, why);
        }
        if (sniffed.Image == ImageFormat.Heif && !OperatingSystem.IsWindows())
        {
            return ContentExtraction.Of(ContentState.Unsupported, $"The contents are {sniffed.What}, which this server has no decoder for.");
        }
        var encoded = new byte[stream.Length];
        stream.ReadExactly(encoded);
        var maxPages = sniffed.Image == ImageFormat.Tiff ? limits.MaxPdfPages : 1;
        var read = ImageText.Read(encoded, maxPages, limits, ct);
        var budget = new TextBudget(limits.MaxChars);
        budget.Append(read.Text);
        var text = budget.ToString();
        var pages = read.PagesRead == 1 ? "the image" : $"{read.PagesRead:N0} pages of the image";
        var byOcr = $"Text was read from {pages} by OCR (average confidence {read.Confidence}%), so a character may have been misread.";
        return read.Outcome switch
        {
            ImageOutcome.Read when read.Stopped => new ContentExtraction(ContentState.Partial, text,
                $"Reading the text in the image stopped at the {Seconds(limits.OcrAllowed)} limit, so only part of it was read by OCR."),
            // Only TIFF pages count: the later frames of an animated GIF are not pages.
            ImageOutcome.Read when read.MorePages && sniffed.Image == ImageFormat.Tiff => new ContentExtraction(ContentState.Partial, text, read.PagesRead == 1
                ? "Only the first page of the image was read by OCR." : $"Only the first {read.PagesRead:N0} pages of the image were read by OCR."),
            ImageOutcome.Read when budget.Full => new ContentExtraction(ContentState.Partial, text, $"Only the first {text.Length:N0} characters were scanned."),
            ImageOutcome.Read when read.HasText => new ContentExtraction(ContentState.Scanned, text, byOcr),
            ImageOutcome.Read => new ContentExtraction(ContentState.Scanned, null, "OCR found no text in the image."),
            ImageOutcome.TooSmall => new ContentExtraction(ContentState.Scanned, null,
                $"The image is too small to hold readable text ({read.Width:N0} × {read.Height:N0} pixels)."),
            ImageOutcome.TooManyPixels => ContentExtraction.Of(ContentState.TooLarge,
                $"The image is {read.Width:N0} × {read.Height:N0} pixels, more than the {limits.MaxImagePixels / 1_000_000.0:0.#} million pixel limit for reading text in images."),
            // Windows answers "no decoder" for a damaged file in a format it always decodes.
            ImageOutcome.NoDecoder when sniffed.Image is not (ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.Gif or ImageFormat.Tiff or ImageFormat.Bmp) =>
                ContentExtraction.Of(ContentState.Unsupported, $"The contents are {sniffed.What}, which this server has no decoder for."),
            ImageOutcome.OutOfTime => ContentExtraction.Of(ContentState.TimedOut, "The time for reading text in this file's images ran out before this one."),
            _ => ContentExtraction.Of(ContentState.Unreadable, "The image is damaged, or uses a variant of its format that can't be decoded."),
        };
    }

    private static bool StartsWith(Stream stream, ReadOnlySpan<byte> magic)
    {
        Span<byte> head = stackalloc byte[magic.Length];
        stream.Position = 0;
        var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return head[..n].SequenceEqual(magic);
    }

    internal static string Seconds(TimeSpan time) => time.TotalSeconds >= 1 ? $"{time.TotalSeconds:0.#} second" : $"{time.TotalMilliseconds:0} millisecond";

    /// <summary>Archive and PDF parsing need to seek; a non-seekable stream is copied, within the size limit already checked.</summary>
    private static Stream Seekable(Stream stream)
    {
        if (stream.CanSeek)
        {
            return stream;
        }
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static FileStream OpenReadOnly(string path) =>
        OperatingSystem.IsWindows()
            ? OpenWindows(path)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan,
            });

    private const uint GenericRead = 0x8000_0000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagSequentialScan = 0x0800_0000;

    /// <summary>FILE_FLAG_OPEN_NO_RECALL: never pull data back from remote storage, even if attributes were stale.</summary>
    private const uint FlagOpenNoRecall = 0x0010_0000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [SupportedOSPlatform("windows")]
    private static FileStream OpenWindows(string path)
    {
        var handle = CreateFile(@"\\?\" + ToExtendedPath(path), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FlagSequentialScan | FlagOpenNoRecall, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error switch
            {
                2 => new FileNotFoundException(null, path),
                3 => new DirectoryNotFoundException(),
                5 => new UnauthorizedAccessException(),
                _ => new IOException(null, unchecked((int)(0x8007_0000 | (uint)error))),
            };
        }
        return new FileStream(handle, FileAccess.Read, bufferSize: 0);
    }

    /// <summary>"\\server\share\x" becomes "UNC\server\share\x" so the "\\?\" prefix lifts the 260-character limit.</summary>
    private static string ToExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"UNC\" + path[2..]
        : path;
}
