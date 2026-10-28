using System.Text;
using DataScope.Core.Classification;
using static DataScope.Scanner.Content.Legacy.CompoundFile;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>
/// Text from Word 97-2003 documents (.doc, .dot) ([MS-DOC]). The document's characters are listed by its piece table,
/// in the table stream; each piece is a run of either 8-bit (Windows-1252) or UTF-16 characters in the WordDocument
/// stream. Main text, footnotes, headers and footers, comments, endnotes and text boxes are all pieces, so all are
/// read. Field codes (such as the instructions behind a table of contents) are dropped and their results kept.
/// Macros, embedded objects and pictures are never opened.
/// </summary>
internal static class WordBinaryReader
{
    private const ushort WordMagic = 0xA5EC;

    /// <summary>Word 97 and later. Word 95 and older use a different layout.</summary>
    private const ushort Word97 = 0x00C1;

    private const ushort Encrypted = 0x0100;
    private const ushort WhichTable = 0x0200;

    public static ContentExtraction Read(CompoundFile file, CompoundEntry word, ContentLimits limits, CancellationToken ct)
    {
        var document = file.Read(word, limits.MaxUncompressedBytes);
        if (document.Length < 0x22 || U16(document, 0) != WordMagic)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        var nFib = U16(document, 2);
        var flags = U16(document, 0x0A);
        if ((flags & Encrypted) != 0)
        {
            return ContentExtraction.Of(ContentState.Encrypted, "The file is password-protected.");
        }
        if (nFib < Word97)
        {
            return ContentExtraction.Of(ContentState.Unsupported, "Word documents from Word 95 and earlier are not read.");
        }

        // FibBase (32 bytes), then three arrays, each preceded by its count: 16-bit values, 32-bit values, and
        // pairs of offsets and lengths, where the 34th pair locates the piece table (Clx).
        var at = 32;
        var csw = U16(document, at);
        at += 2 + (csw * 2);
        var cslw = U16(document, at);
        at += 2 + (cslw * 4);
        var pairs = U16(document, at);
        at += 2;
        const int ClxPair = 33;
        if (pairs <= ClxPair || at + ((ClxPair + 1) * 8) > document.Length)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        var fcClx = U32(document, at + (ClxPair * 8));
        var lcbClx = U32(document, at + (ClxPair * 8) + 4);

        var tableName = (flags & WhichTable) != 0 ? "1Table" : "0Table";
        if (file.Find(file.Root, tableName) is not { IsStorage: false } tableEntry)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }
        var table = file.Read(tableEntry, limits.MaxUncompressedBytes);
        if (lcbClx == 0 || (long)fcClx + lcbClx > table.Length)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is damaged or not in the format its name suggests.");
        }

        var budget = new TextBudget(limits.MaxChars);
        var text = new FieldFilter(budget);
        foreach (var (offset, count, compressed) in Pieces(table, (int)fcClx, (int)lcbClx))
        {
            ct.ThrowIfCancellationRequested();
            if (budget.Full)
            {
                break;
            }
            var bytes = compressed ? count : count * 2L;
            if (offset < 0 || offset + bytes > document.Length)
            {
                continue; // A piece pointing outside the document: skip it, keep the rest.
            }
            var chars = compressed
                ? LegacyText.Windows1252.GetString(document, (int)offset, count)
                : Encoding.Unicode.GetString(document, (int)offset, count * 2);
            text.Append(chars);
        }
        var result = budget.ToString();
        return budget.Full
            ? new ContentExtraction(ContentState.Partial, result, $"Only the first {result.Length:N0} characters were scanned.")
            : new ContentExtraction(ContentState.Scanned, result, null);
    }

    /// <summary>The pieces of text: where each starts in the WordDocument stream, how many characters, and whether 8-bit.</summary>
    internal static IEnumerable<(long Offset, int Count, bool Compressed)> Pieces(byte[] table, int fcClx, int lcbClx)
    {
        var at = fcClx;
        var end = fcClx + lcbClx;
        // Prc entries (formatting) come first and are skipped; then the Pcdt holds the piece table.
        while (at < end && table[at] == 0x01)
        {
            var cb = at + 3 > end ? -1 : (short)U16(table, at + 1);
            if (cb < 0)
            {
                yield break;
            }
            at += 3 + cb;
        }
        if (at + 5 > end || table[at] != 0x02)
        {
            yield break;
        }
        var lcb = U32(table, at + 1);
        at += 5;
        if (lcb < 4 || at + lcb > end)
        {
            yield break;
        }
        // n + 1 character positions, then n 8-byte piece descriptors.
        var n = (int)((lcb - 4) / 12);
        var descriptors = at + ((n + 1) * 4);
        for (var i = 0; i < n; i++)
        {
            var cpStart = U32(table, at + (i * 4));
            var cpEnd = U32(table, at + ((i + 1) * 4));
            if (cpEnd <= cpStart || cpEnd - cpStart > int.MaxValue / 2)
            {
                continue;
            }
            var fc = U32(table, descriptors + (i * 8) + 2);
            var compressed = (fc & 0x4000_0000) != 0;
            var offset = (long)(fc & 0x3FFF_FFFF);
            yield return (compressed ? offset / 2 : offset, (int)(cpEnd - cpStart), compressed);
        }
    }

    /// <summary>
    /// Turns Word's control characters into plain text: paragraph and cell marks into line breaks and tabs, and field
    /// instructions (between the field-begin and field-separator marks) dropped, keeping what the field displays.
    /// </summary>
    private sealed class FieldFilter(TextBudget budget)
    {
        /// <summary>Open fields, innermost on top: true while its instructions are being read.</summary>
        private readonly Stack<bool> fields = new();
        private int instructionDepth;
        private char last;

        public void Append(string chars)
        {
            foreach (var c in chars)
            {
                var previous = last;
                last = c;
                switch (c)
                {
                    case '\u0013': // Field begins: its instructions follow.
                        fields.Push(true);
                        instructionDepth++;
                        continue;
                    case '\u0014': // Separator: what the field shows follows.
                        if (fields.Count > 0 && fields.Peek())
                        {
                            fields.Pop();
                            fields.Push(false);
                            instructionDepth--;
                        }
                        continue;
                    case '\u0015': // Field ends.
                        if (fields.Count > 0 && fields.Pop())
                        {
                            instructionDepth--;
                        }
                        continue;
                }
                if (instructionDepth > 0)
                {
                    continue;
                }
                switch (c)
                {
                    case '\r' or '\u000B' or '\u000C' or '\u000E':
                        budget.Append('\n');
                        break;
                    case '\u0007': // The end of a cell; a second straight after it ends the row.
                        budget.Append(previous == '\u0007' ? '\n' : '\t');
                        break;
                    case '\t':
                        budget.Append('\t');
                        break;
                    case '\u001E': // Non-breaking hyphen.
                        budget.Append('-');
                        break;
                    case ' ':
                        budget.Append(' ');
                        break;
                    case < ' ':
                        break; // Pictures, drawn objects, footnote marks and other anchors.
                    default:
                        budget.Append(c);
                        break;
                }
            }
        }
    }
}
