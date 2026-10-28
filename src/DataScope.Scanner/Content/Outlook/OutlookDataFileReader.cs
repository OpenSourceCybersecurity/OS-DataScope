using System.Diagnostics;
using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Outlook;

/// <summary>
/// Reads an Outlook data file (.pst, or an .ost offline mailbox) message by message (D134): each message's subject
/// and body (plain text, else HTML, else rich text), its attachments of the types Data Scope reads with the same
/// readers and limits as files on the share, and messages attached to it. Contacts, appointments and tasks are read
/// the same way, by subject and notes. Sender and recipient addresses are left out, links and images are never
/// fetched, and nothing is written to disk.
/// </summary>
/// <remarks>
/// Messages are found from the file's node tree rather than its folders, so a damaged folder can't hide the messages
/// in it. Reading stops at the mailbox's own character budget (<see cref="ContentLimits.MailboxChars"/>), its unpacked
/// bytes (<see cref="ContentLimits.MaxUncompressedBytes"/>) or two thirds of the file's time limit, whichever comes
/// first, and the note says how many of the messages were read. Outlook's password on a .pst file only stops Outlook
/// opening it; the data is not encrypted by it, so such files are read like any other.
/// </remarks>
internal static class OutlookDataFileReader
{
    private const int NormalMessage = 0x04;
    private const int AttachmentNode = 0x05;

    private const ushort Subject = 0x0037;
    private const ushort Body = 0x1000;
    private const ushort RtfCompressed = 0x1009;
    private const ushort Html = 0x1013;
    private const ushort AttachMethod = 0x3705;
    private const ushort AttachData = 0x3701;
    private const ushort AttachFileName = 0x3704;
    private const ushort AttachLongFileName = 0x3707;
    private const ushort DisplayName = 0x3001;

    private const int ByValue = 1;
    private const int EmbeddedMessage = 5;

    public static ContentExtraction Read(Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        if (depth > 0)
        {
            // A mailbox inside a zip or an email would need the whole mailbox unpacked into memory first.
            return ContentExtraction.Of(ContentState.Unsupported, "Outlook data files inside zips and emails are not opened.");
        }
        PstFile file;
        try
        {
            file = PstFile.Open(stream);
        }
        catch (PstUnsupportedException ex)
        {
            return ContentExtraction.Of(ex.Encrypted ? ContentState.Encrypted : ContentState.Unsupported, ex.Message);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        var mailbox = new Mailbox(file, limits, depth, ct);
        try
        {
            mailbox.ReadAll();
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            // The node tree itself is damaged: what was read before the damage is kept.
            mailbox.TreeDamaged();
        }
        return mailbox.Result();
    }

    /// <summary>
    /// A structure in the file that is not what it claims: checked explicitly (InvalidDataException), or caught by the
    /// runtime's own bounds checks on a value no check foresaw.
    /// </summary>
    private static bool IsDamage(Exception ex) => ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException;

    private sealed class Mailbox(PstFile file, ContentLimits limits, int depth, CancellationToken ct)
    {
        private readonly InnerFiles attachments = new(limits, depth, limits.MailboxChars);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long bytesLeft = limits.MaxUncompressedBytes;
        private int messages;
        private int read;
        private int damaged;
        private bool outOfTime;
        private bool countIncomplete;
        private bool treeDamaged;

        public void TreeDamaged()
        {
            treeDamaged = true;
            countIncomplete = true;
        }

        /// <summary>When reading stops, leaving the rest of the file's time to finish the message being read.</summary>
        private TimeSpan ReadingTime => limits.PerFileTimeout * 2 / 3;

        /// <summary>Messages are still counted after reading stops, until this point.</summary>
        private TimeSpan CountingTime => limits.PerFileTimeout * 5 / 6;

        private bool Stopped => attachments.Stopped || outOfTime;

        public void ReadAll()
        {
            foreach (var node in file.Nodes(ct))
            {
                if (PstFile.NidType(node.Nid) != NormalMessage)
                {
                    continue;
                }
                if (clock.Elapsed > CountingTime)
                {
                    countIncomplete = true;
                    outOfTime = true;
                    return;
                }
                messages++;
                if (Stopped)
                {
                    continue;
                }
                if (clock.Elapsed > ReadingTime)
                {
                    outOfTime = true;
                    continue;
                }
                try
                {
                    ReadMessage(PstProperties.Read(file, new PstSubnode(node.DataBid, node.SubBid)), attached: 0);
                    read++;
                }
                catch (Exception ex) when (IsDamage(ex))
                {
                    // One damaged message must not stop the rest of the mailbox being read.
                    damaged++;
                }
            }
        }

        private void ReadMessage(PstProperties message, int attached)
        {
            ct.ThrowIfCancellationRequested();
            Append(StripSubjectPrefix(message.Text(Subject, limits.MaxChars)));
            Append(BodyOf(message));

            foreach (var (nid, sub) in message.Subnodes.Where(s => PstFile.NidType(s.Key) == AttachmentNode).OrderBy(s => s.Key))
            {
                ct.ThrowIfCancellationRequested();
                if (Stopped)
                {
                    return;
                }
                if (clock.Elapsed > ReadingTime)
                {
                    outOfTime = true;
                    return;
                }
                PstProperties attachment;
                try
                {
                    attachment = PstProperties.Read(file, sub);
                }
                catch (Exception ex) when (IsDamage(ex))
                {
                    attachments.Failed();
                    continue;
                }
                ReadAttachment(attachment, attached);
            }
        }

        private string BodyOf(PstProperties message)
        {
            var body = message.Text(Body, limits.MaxChars);
            if (body.Length > 0)
            {
                return body;
            }
            if (message.TypeOf(Html) == 0x0102 && message.Binary(Html, limits.MaxTextBytes, out _, out _) is { Length: > 0 } html)
            {
                return EmailReader.HtmlToText(PlainTextReader.Decode(html, more: false) ?? Encoding.Latin1.GetString(html));
            }
            if (message.Text(Html, limits.MaxChars) is { Length: > 0 } htmlText)
            {
                return EmailReader.HtmlToText(htmlText);
            }
            if (message.Binary(RtfCompressed, limits.MaxTextBytes, out _, out _) is { Length: > 0 } rtf)
            {
                try
                {
                    return RtfText.ToText(RtfText.Decompress(rtf, limits.MaxTextBytes), limits.MaxChars);
                }
                catch (Exception ex) when (IsDamage(ex))
                {
                    return "";
                }
            }
            return "";
        }

        private void ReadAttachment(PstProperties attachment, int attached)
        {
            var method = attachment.Int32(AttachMethod) ?? ByValue;
            if (method == EmbeddedMessage)
            {
                if (attachment.ObjectNid(AttachData) is not { } nid || !attachment.Subnodes.TryGetValue(nid, out var embedded))
                {
                    attachments.Failed();
                    return;
                }
                if (attached + 1 >= limits.MaxNestingDepth)
                {
                    attachments.NotRead();
                    return;
                }
                try
                {
                    ReadMessage(PstProperties.Read(file, embedded), attached + 1);
                }
                catch (Exception ex) when (IsDamage(ex))
                {
                    attachments.Failed();
                }
                return;
            }
            if (method != ByValue || attachment.TypeOf(AttachData) != 0x0102)
            {
                // A link to a file elsewhere, which is never followed, or an embedded object such as a drawing.
                attachments.NotRead();
                return;
            }
            var name = attachment.Text(AttachLongFileName, 260) is { Length: > 0 } longName ? longName
                : attachment.Text(AttachFileName, 260) is { Length: > 0 } shortName ? shortName
                : attachment.Text(DisplayName, 260) is { Length: > 0 } display ? display
                : "attachment";
            var kind = FileContentReader.KindOf(name);
            if (FileContentReader.NeverOpened(kind))
            {
                attachments.NotRead();
                return;
            }
            // Text and copies are read from the start only, as in a zip.
            var readOnlyStart = kind is FileContentReader.Kind.Text or FileContentReader.Kind.ByContents;
            var max = readOnlyStart ? limits.MaxTextBytes : FileContentReader.MaxBytes(kind, limits);
            var take = Math.Min(max, bytesLeft + 1);
            byte[]? bytes;
            long size;
            bool truncated;
            try
            {
                bytes = attachment.Binary(AttachData, take, out size, out truncated);
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                attachments.Failed();
                return;
            }
            if (bytes is null)
            {
                attachments.Failed();
                return;
            }
            if (truncated && !readOnlyStart)
            {
                if (size > max)
                {
                    attachments.TooLarge();
                }
                else
                {
                    attachments.Stop();
                }
                return;
            }
            if (bytes.Length > bytesLeft)
            {
                attachments.Stop();
                return;
            }
            bytesLeft -= bytes.Length;
            using var content = new MemoryStream(bytes, writable: false);
            attachments.Read(name, content, truncated, ct);
        }

        private void Append(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            var budget = attachments.Budget;
            if (budget.Length > 0)
            {
                budget.Append("\n\n");
            }
            budget.Append(text);
            attachments.HasOwnText = true;
        }

        public ContentExtraction Result()
        {
            var text = attachments.Budget.ToString();
            if (messages == 0)
            {
                if (outOfTime)
                {
                    return ContentExtraction.Of(ContentState.TimedOut, "The time for reading this file ran out before any message in the mailbox was read.");
                }
                return treeDamaged
                    ? ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.")
                    : new ContentExtraction(ContentState.Scanned, null, "The mailbox holds no messages.");
            }
            if (read == 0 && damaged > 0 && !Stopped)
            {
                return ContentExtraction.Of(ContentState.Unreadable, $"None of the {messages:N0} messages in the mailbox could be read: the file is damaged.");
            }
            var all = read == messages && !countIncomplete;
            var count = countIncomplete ? $"{messages:N0} or more" : $"{messages:N0}";
            var what = all
                ? $"all {count} {(messages == 1 ? "message" : "messages")}"
                : $"{read:N0} of {count} messages";
            var problems = new List<string>();
            if (treeDamaged)
            {
                problems.Add("the rest of the file is damaged");
            }
            if (damaged > 0)
            {
                problems.Add($"{damaged:N0} {(damaged == 1 ? "message" : "messages")} damaged");
            }
            if (outOfTime)
            {
                problems.Add($"stopped at the {FileContentReader.Seconds(ReadingTime)} reading time for this file");
            }
            if (attachments.Total > 0)
            {
                what += $" and {attachments.ReadCount:N0} of {attachments.Total:N0} attachments";
            }
            if (attachments.HasProblems || attachments.Stopped)
            {
                problems.Add(attachments.Details());
            }
            var note = $"Read {what} in the mailbox" + (problems.Count > 0 ? ": " + string.Join(", ", problems) + "." : ".");
            var complete = all && !treeDamaged && damaged == 0 && !outOfTime && !attachments.HasProblems && !attachments.Stopped;
            return new ContentExtraction(complete ? ContentState.Scanned : ContentState.Partial, text.Length > 0 ? text : null, note);
        }

        /// <summary>
        /// [MS-PST] 2.5.3.1.1.1: a subject stored in a .pst file may start with U+0001 and a character giving the length
        /// of its prefix ("RE: "), which is not part of the subject.
        /// </summary>
        private static string StripSubjectPrefix(string subject) =>
            subject.Length >= 2 && subject[0] == '\u0001' ? subject[2..] : subject;
    }
}
