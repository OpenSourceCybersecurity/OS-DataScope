using System.Buffers.Binary;
using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>
/// Text from PowerPoint 97-2003 presentations (.ppt, .pps, .pot) ([MS-PPT]). The "PowerPoint Document" stream is a tree
/// of records; the text on slides, in notes and in text boxes is held in text atoms, as UTF-16 or as 8-bit
/// characters. The tree is walked without recursion and to a bounded depth, every length checked against its
/// container. Text left in the file by an earlier quick save is read too: it is in the file, so it is scanned.
/// Embedded objects, sounds and pictures are never opened.
/// </summary>
internal static class PowerPointBinaryReader
{
    private const ushort TextCharsAtom = 0x0FA0;
    private const ushort TextBytesAtom = 0x0FA8;
    private const ushort CryptSession = 0x2F14;

    /// <summary>The slide master: its placeholder prompts ("Click to edit the title text format") are not document text.</summary>
    private const ushort MainMaster = 0x03F8;
    private const int MaxDepth = 32;

    public static ContentExtraction Read(CompoundFile file, CompoundEntry document, ContentLimits limits, CancellationToken ct)
    {
        if (file.Find(file.Root, "EncryptedSummary") is not null)
        {
            return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
        }
        var data = file.Read(document, limits.MaxUncompressedBytes);
        var budget = new TextBudget(limits.MaxChars);
        var ends = new Stack<int>();
        ends.Push(data.Length);
        var at = 0;
        var records = 0;
        while (!budget.Full && ends.Count > 0)
        {
            if (++records % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            var end = ends.Peek();
            if (at + 8 > end)
            {
                at = ends.Pop(); // This container is done; carry on after it.
                continue;
            }
            var version = data[at] & 0x0F;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            var body = at + 8;
            if (length > end - body)
            {
                at = ends.Pop(); // A record longer than what holds it: the rest of this container is damaged.
                continue;
            }
            if (type == CryptSession)
            {
                return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
            }
            if (version == 0x0F && type != MainMaster && ends.Count < MaxDepth)
            {
                ends.Push(body + (int)length);
                at = body;
                continue;
            }
            switch (type)
            {
                case TextCharsAtom:
                    Append(budget, Encoding.Unicode.GetString(data, body, (int)length & ~1));
                    break;
                case TextBytesAtom:
                    Append(budget, Encoding.Latin1.GetString(data, body, (int)length));
                    break;
            }
            at = body + (int)length;
        }
        var text = budget.ToString();
        return budget.Full
            ? new ContentExtraction(ContentState.Partial, text, $"Only the first {text.Length:N0} characters were scanned.")
            : new ContentExtraction(ContentState.Scanned, text, null);
    }

    /// <summary>One block of text: its paragraph and line breaks become new lines.</summary>
    private static void Append(TextBudget budget, string text)
    {
        foreach (var c in text)
        {
            budget.Append(c is '\r' or '\v' ? '\n' : c < ' ' && c != '\t' ? ' ' : c);
        }
        budget.Append('\n');
    }
}
