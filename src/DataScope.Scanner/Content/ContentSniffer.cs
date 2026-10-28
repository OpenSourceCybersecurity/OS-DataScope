using System.IO.Compression;
using System.Text;
using DataScope.Scanner.Content.Legacy;
using Kind = DataScope.Scanner.Content.FileContentReader.Kind;

namespace DataScope.Scanner.Content;

/// <summary>
/// What the first bytes of a file show it is. <see cref="What"/> names a binary format for notes, such as "a 7-Zip
/// archive"; <see cref="Image"/> says which image format, for images.
/// </summary>
internal readonly record struct Sniffed(Kind Kind, string? What = null, ImageFormat Image = ImageFormat.None);

internal enum ImageFormat
{
    None,
    Jpeg,
    Png,
    Gif,
    Tiff,
    Bmp,
    Icon,
    WebP,
    Heif,
}

/// <summary>
/// Tells what a file really is from its first bytes, for files whose name says nothing or can't be trusted (.bak,
/// .tmp, no extension, an image). Only the start of the file and, for a zip, its list of entries are read.
/// </summary>
internal static class ContentSniffer
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] EmptyZipMagic = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    /// <summary>Formats recognised by the bytes at the very start. Checked in order; the first match wins.</summary>
    private static readonly (byte[] Magic, Sniffed Sniffed)[] Signatures =
    [
        ([0xFF, 0xD8, 0xFF], new(Kind.Image, "a JPEG image", ImageFormat.Jpeg)),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], new(Kind.Image, "a PNG image", ImageFormat.Png)),
        ("GIF87a"u8.ToArray(), new(Kind.Image, "a GIF image", ImageFormat.Gif)),
        ("GIF89a"u8.ToArray(), new(Kind.Image, "a GIF image", ImageFormat.Gif)),
        ([0x49, 0x49, 0x2A, 0x00], new(Kind.Image, "a TIFF image", ImageFormat.Tiff)),
        ([0x4D, 0x4D, 0x00, 0x2A], new(Kind.Image, "a TIFF image", ImageFormat.Tiff)),
        ([0x00, 0x00, 0x01, 0x00], new(Kind.Image, "an icon", ImageFormat.Icon)),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], new(Kind.Archive, "a 7-Zip archive")),
        ("Rar!\x1A\x07"u8.ToArray(), new(Kind.Archive, "a RAR archive")),
        ([0x1F, 0x8B], new(Kind.Binary, "a gzip archive")),
        ([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], new(Kind.Binary, "an xz archive")),
        ("MSCF"u8.ToArray(), new(Kind.Binary, "a cabinet (.cab) archive")),
        ("!BDN"u8.ToArray(), new(Kind.OutlookDataFile, "an Outlook data file (.pst or .ost)")),
        ("SQLite format 3\0"u8.ToArray(), new(Kind.Binary, "a SQLite database")),
        ("MZ"u8.ToArray(), new(Kind.Binary, "a Windows program or library")),
        ([0x7F, 0x45, 0x4C, 0x46], new(Kind.Binary, "a program or library")),
        ("vhdxfile"u8.ToArray(), new(Kind.Binary, "a virtual disk")),
        ("conectix"u8.ToArray(), new(Kind.Binary, "a virtual disk")),
        ("KDMV"u8.ToArray(), new(Kind.Binary, "a virtual disk")),
        ("MSWIM\0\0\0"u8.ToArray(), new(Kind.Binary, "a Windows disk image")),
        ([0x1A, 0x45, 0xDF, 0xA3], new(Kind.Binary, "a video")),
        ("ID3"u8.ToArray(), new(Kind.Binary, "an audio file")),
        ("fLaC"u8.ToArray(), new(Kind.Binary, "an audio file")),
        ("OggS"u8.ToArray(), new(Kind.Binary, "an audio or video file")),
        ([0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11], new(Kind.Binary, "a Windows Media file")),
    ];

    /// <summary>Header fields that start a saved email. Two of them in the first lines mean it is one.</summary>
    private static readonly string[] EmailHeaders =
        ["from:", "to:", "subject:", "date:", "received:", "return-path:", "message-id:", "mime-version:", "content-type:", "x-"];

    /// <summary>The kind to read the stream as. Text, which may still turn out binary, when nothing else matches.</summary>
    public static Sniffed Sniff(Stream stream, ContentLimits limits)
    {
        Span<byte> head = stackalloc byte[4096];
        stream.Position = 0;
        var length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..length];
        stream.Position = 0;

        if (head.StartsWith(CompoundFile.Magic))
        {
            return new(Kind.LegacyOffice);
        }
        if (head.StartsWith(PdfMagic))
        {
            return new(Kind.Pdf);
        }
        if (head.StartsWith(ZipMagic) || head.StartsWith(EmptyZipMagic))
        {
            return new(ZipKind(stream, limits));
        }
        foreach (var (magic, sniffed) in Signatures)
        {
            if (head.StartsWith(magic))
            {
                return sniffed;
            }
        }
        if (Container(head) is { } container)
        {
            return container;
        }
        if (head.Length >= 54 && head.StartsWith("BM"u8) && head[6] == 0 && head[7] == 0 && head[8] == 0 && head[9] == 0)
        {
            return new(Kind.Image, "a bitmap image", ImageFormat.Bmp); // "BM", then four reserved bytes that are always zero.
        }
        if (head.Length >= 20 && head[..4].SequenceEqual((ReadOnlySpan<byte>)[0, 1, 0, 0])
            && (head[4..19].SequenceEqual("Standard Jet DB"u8) || head[4..19].SequenceEqual("Standard ACE DB"u8)))
        {
            return new(Kind.Binary, "an Access database");
        }
        return LooksLikeEmail(head) ? new(Kind.Email) : new(Kind.Text);
    }

    /// <summary>RIFF and ISO base media containers: the format is named a few bytes in.</summary>
    private static Sniffed? Container(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            var form = head[8..12];
            return form.SequenceEqual("WEBP"u8) ? new(Kind.Image, "a WebP image", ImageFormat.WebP)
                : form.SequenceEqual("WAVE"u8) ? new(Kind.Binary, "an audio file")
                : new(Kind.Binary, "a video or audio file");
        }
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = head[8..12];
            return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("avif"u8)
                ? new(Kind.Image, "a HEIC or AVIF image", ImageFormat.Heif)
                : new(Kind.Binary, "a video or audio file");
        }
        return null;
    }

    /// <summary>
    /// True when the start of the stream is not text: it can't be decoded, or more than one byte in ten is a control
    /// character that text never holds. Used for files whose name says nothing, so a binary format Data Scope doesn't
    /// know is never scanned as noise.
    /// </summary>
    public static bool LooksBinary(Stream stream)
    {
        Span<byte> head = stackalloc byte[8192];
        stream.Position = 0;
        var length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        head = head[..length];
        if (length == 0)
        {
            return false;
        }
        if (PlainTextReader.Decode(head, more: stream.Length > length) is not { } text)
        {
            return true;
        }
        var control = 0;
        foreach (var c in text)
        {
            if ((c < 0x20 && c is not ('\t' or '\n' or '\r' or '\f')) || c is >= '\u007F' and <= '\u009F')
            {
                control++;
            }
        }
        return control * 10 > text.Length;
    }

    /// <summary>Word, Excel and PowerPoint files are zips with their own folder of parts; anything else is a plain zip.</summary>
    private static Kind ZipKind(Stream stream, ContentLimits limits)
    {
        try
        {
            if (stream.Length > limits.MaxArchiveBytes || OfficeOpenXmlReader.DeclaredEntryCount(stream) is not { } count || count > limits.MaxArchiveEntries)
            {
                return Kind.Archive; // The archive reader refuses it and says why.
            }
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
                {
                    return Kind.Word;
                }
                if (entry.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    return Kind.Excel;
                }
                if (entry.FullName.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
                {
                    return Kind.PowerPoint;
                }
            }
            return Kind.Archive;
        }
        catch (InvalidDataException)
        {
            return Kind.Archive;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static bool LooksLikeEmail(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head);
        var found = 0;
        foreach (var line in text.Split('\n').Take(20))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                break; // The headers end at the first blank line.
            }
            if (trimmed[0] is ' ' or '\t')
            {
                continue; // A header folded onto the next line.
            }
            if (EmailHeaders.Any(h => trimmed.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            {
                found++;
            }
            else if (!trimmed.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }
        return found >= 2;
    }
}
