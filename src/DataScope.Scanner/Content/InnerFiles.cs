using DataScope.Core.Classification;
using Kind = DataScope.Scanner.Content.FileContentReader.Kind;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads the files inside a zip or attached to an email, adds their text to one budget, and counts what could not be
/// read, so the result says plainly how much of the container was scanned.
/// </summary>
/// <param name="budgetChars">Characters of text kept for the container; <see cref="ContentLimits.MaxChars"/> unless given.</param>
internal sealed class InnerFiles(ContentLimits limits, int depth, int? budgetChars = null)
{
    private int read;
    private int partial;
    private int encrypted;
    private int notRead;
    private int tooLarge;
    private int failed;
    private int outOfTime;
    private bool stopped;

    private readonly int keptChars = budgetChars ?? limits.MaxChars;

    public TextBudget Budget { get; } = new(budgetChars ?? limits.MaxChars);

    /// <summary>Text that belongs to the container itself, such as an email's subject and body.</summary>
    public bool HasOwnText { get; set; }

    /// <summary>Inner files met, read or not.</summary>
    public int Total => read + encrypted + notRead + tooLarge + failed + outOfTime;

    /// <summary>Inner files read, in whole or in part.</summary>
    public int ReadCount => read;

    /// <summary>Some inner file was not read, or read only in part.</summary>
    public bool HasProblems => encrypted + notRead + tooLarge + failed + outOfTime + partial > 0;

    public void NotRead() => notRead++;

    public void Encrypted() => encrypted++;

    public void TooLarge() => tooLarge++;

    public void Failed() => failed++;

    /// <summary>A safety limit ended the reading early; files not reached are not counted.</summary>
    public void Stop() => stopped = true;

    public bool Stopped => stopped || Budget.Full;

    /// <summary>Reads one inner file by its name. <paramref name="truncated"/>: only its start was available.</summary>
    public void Read(string name, Stream content, bool truncated, CancellationToken ct)
    {
        var kind = FileContentReader.KindOf(name);
        if (FileContentReader.NeverOpened(kind))
        {
            notRead++;
            return;
        }
        ContentExtraction result;
        try
        {
            result = FileContentReader.ReadAs(kind, content, limits, depth + 1, ct);
        }
        catch (Exception ex) when (ex is ContentLimitException or InvalidDataException or System.Xml.XmlException or EndOfStreamException)
        {
            // One damaged file must not stop the rest of the zip or email from being read.
            failed++;
            return;
        }
        Add(result, truncated);
    }

    private void Add(ContentExtraction r, bool truncated)
    {
        if (r.Text is { } text)
        {
            read++;
            if (r.State == ContentState.Partial || truncated)
            {
                partial++;
            }
            if (Budget.Length > 0)
            {
                Budget.Append("\n\n");
            }
            Budget.Append(text);
            return;
        }
        switch (r.State)
        {
            case ContentState.Scanned or ContentState.Partial:
                read++; // Read, with no text in it: an image without words.
                break;
            case ContentState.TimedOut:
                outOfTime++;
                break;
            case ContentState.Encrypted:
                encrypted++;
                break;
            case ContentState.Unsupported or ContentState.NotRead:
                notRead++;
                break;
            case ContentState.TooLarge:
                tooLarge++;
                break;
            default:
                failed++;
                break;
        }
    }

    /// <param name="container">"zip", "7-Zip archive", "RAR archive" or "email", for the note.</param>
    public ContentExtraction Result(string container)
    {
        var text = Budget.ToString();
        var problems = encrypted + notRead + tooLarge + failed + outOfTime;
        if (read == 0 && !HasOwnText)
        {
            if (Total == 0 && !stopped)
            {
                return new ContentExtraction(ContentState.Scanned, text, null);
            }
            if (encrypted > 0 && encrypted == problems && !stopped)
            {
                return ContentExtraction.Of(ContentState.Encrypted, $"The files in the {container} are password-protected.");
            }
            if (outOfTime > 0 && outOfTime == problems && !stopped)
            {
                return ContentExtraction.Of(ContentState.TimedOut, $"The time for reading text in images ran out before the images in the {container} were read.");
            }
            if (notRead > 0 && notRead == problems && !stopped)
            {
                return ContentExtraction.Of(ContentState.Unsupported, $"The {container} holds no file types Data Scope reads.");
            }
            return ContentExtraction.Of(ContentState.Unreadable, $"No file in the {container} could be read: {Details()}.");
        }
        if (problems == 0 && partial == 0 && !Stopped)
        {
            return new ContentExtraction(ContentState.Scanned, text, null);
        }
        var what = container == "email" ? $"the email and {read:N0} of {Total:N0} attachments" : $"{read:N0} of {Total:N0} files in the {container}";
        return new ContentExtraction(ContentState.Partial, text, $"Read {what}: {Details()}.");
    }

    /// <summary>What was not read, and why, for the note: "2 of types not read, 1 over the size limit".</summary>
    internal string Details()
    {
        var parts = new List<string>();
        if (partial > 0)
        {
            parts.Add($"{partial:N0} only in part");
        }
        if (encrypted > 0)
        {
            parts.Add($"{encrypted:N0} password-protected");
        }
        if (notRead > 0)
        {
            parts.Add($"{notRead:N0} of types not read");
        }
        if (tooLarge > 0)
        {
            parts.Add($"{tooLarge:N0} over the size limit");
        }
        if (failed > 0)
        {
            parts.Add($"{failed:N0} damaged or unreadable");
        }
        if (outOfTime > 0)
        {
            parts.Add($"{outOfTime:N0} images left when the time for reading text in images ran out");
        }
        if (Budget.Full)
        {
            parts.Add($"stopped at the {keptChars:N0} character limit");
        }
        else if (stopped)
        {
            parts.Add("stopped at a safety limit");
        }
        return string.Join(", ", parts);
    }
}
