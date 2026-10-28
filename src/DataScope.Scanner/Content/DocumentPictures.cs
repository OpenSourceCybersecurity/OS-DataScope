using System.IO.Compression;
using DataScope.Scanner.Content.Ocr;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads the text in pictures pasted into Word, Excel and PowerPoint files by OCR (D131): a scanned form dropped into
/// a document, or a screenshot in a slide. Only pictures stored in the document's media folder in a format
/// <see cref="FileContentReader"/> reads as an image are tried; embedded objects are never opened, and vector drawings
/// (EMF, WMF) are left out. The largest pictures go first, at most <see cref="MaxPictures"/> per document, all within
/// the file's time for reading text in images, the document's limit on unpacked bytes and the image size limits.
/// </summary>
internal sealed class DocumentPictures(ContentLimits limits)
{
    /// <summary>Pictures read per document. More is rare outside slide decks of photos, which hold little text.</summary>
    internal const int MaxPictures = 50;

    private string? unavailable;
    private int pictures;
    private int read;
    private int withText;
    private int skipped;
    private int tooLarge;
    private int undecodable;
    private int outOfTime;
    private long confidence;
    private long weight;

    /// <summary>True when pictures were left unread, so the document is only partly scanned.</summary>
    public bool Missed => unavailable is not null || skipped > 0 || tooLarge > 0 || undecodable > 0 || outOfTime > 0;

    /// <summary>The document's media folder for each format.</summary>
    internal static string MediaFolder(OfficeFormat format) => format switch
    {
        OfficeFormat.Word => "word/media/",
        OfficeFormat.Excel => "xl/media/",
        OfficeFormat.PowerPoint => "ppt/media/",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Reads the pictures in <paramref name="folder"/> and adds their text to <paramref name="budget"/>.
    /// <paramref name="bytesLeft"/> is the document's remaining allowance of unpacked bytes, shared with its text.
    /// </summary>
    public void Read(ZipArchive zip, string folder, TextBudget budget, ref long bytesLeft, CancellationToken ct)
    {
        var candidates = zip.Entries
            .Where(e => e.FullName.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && e.FullName.Length > folder.Length
                && FileContentReader.KindOf(e.FullName) == FileContentReader.Kind.Image
                && !e.FullName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .ThenBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        pictures = candidates.Count;
        if (pictures == 0)
        {
            return;
        }
        if ((unavailable = ImageText.Unavailable(limits)) is not null)
        {
            return;
        }
        skipped = Math.Max(0, pictures - MaxPictures);
        foreach (var entry in candidates.Take(MaxPictures))
        {
            ct.ThrowIfCancellationRequested();
            if (budget.Full)
            {
                return; // Nothing more could be kept; the note already says the text was cut short.
            }
            if (outOfTime > 0)
            {
                outOfTime++;
                continue;
            }
            if (entry.Length > limits.MaxImageBytes || entry.Length > bytesLeft)
            {
                tooLarge++;
                continue;
            }
            byte[] encoded;
            try
            {
                encoded = new byte[entry.Length];
                using var s = new LimitedStream(entry.Open(), entry.Length);
                s.ReadExactly(encoded);
                bytesLeft -= entry.Length;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ContentLimitException)
            {
                undecodable++;
                continue;
            }
            Add(ImageText.Read(encoded, 1, limits, ct), budget);
        }
    }

    private void Add(ImageRead result, TextBudget budget)
    {
        switch (result.Outcome)
        {
            case ImageOutcome.Read:
                read++;
                if (result.HasText)
                {
                    withText++;
                    budget.Append('\n');
                    budget.Append(result.Text);
                    confidence += (long)result.Confidence * result.Text.Length;
                    weight += result.Text.Length;
                }
                outOfTime += result.Stopped ? 1 : 0;
                break;
            case ImageOutcome.TooSmall:
                read++;
                break;
            case ImageOutcome.TooManyPixels:
                tooLarge++;
                break;
            case ImageOutcome.OutOfTime:
                outOfTime++;
                break;
            default:
                undecodable++;
                break;
        }
    }

    /// <summary>What to add to the document's note, or null when it had no pictures to read.</summary>
    public string? Note
    {
        get
        {
            var parts = new List<string>();
            if (withText > 0)
            {
                var which = withText == 1 ? "1 picture" : $"{withText:N0} pictures";
                parts.Add($"Text in {which} in the document was read by OCR (average confidence {(weight == 0 ? 0 : confidence / weight)}%), so a character may have been misread.");
            }
            if (unavailable is not null)
            {
                parts.Add(pictures == 1
                    ? $"The picture in the document was not read: {unavailable}"
                    : $"The {pictures:N0} pictures in the document were not read: {unavailable}");
            }
            else if (Missed)
            {
                parts.Add($"Some pictures in the document were not read: {Problems()}.");
            }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    private string Problems()
    {
        var parts = new List<string>();
        if (skipped > 0)
        {
            parts.Add($"{skipped:N0} beyond the first {MaxPictures:N0}");
        }
        if (tooLarge > 0)
        {
            parts.Add($"{tooLarge:N0} over the size limit");
        }
        if (undecodable > 0)
        {
            parts.Add($"{undecodable:N0} damaged or in a format this server can't decode");
        }
        if (outOfTime > 0)
        {
            parts.Add($"{outOfTime:N0} left when the time for reading text in images ran out");
        }
        return string.Join(", ", parts);
    }
}
