using System.Text;
using DataScope.Scanner.Content.Legacy;

namespace DataScope.Scanner.Content.Outlook;

/// <summary>
/// A node's properties ([MS-PST] 2.3): a heap on the node's data blocks, a B-tree on that heap from property id to
/// type and value, and values kept inline, in the heap, or in a subnode when large. Every heap reference is checked
/// against the page it names and that page's allocation map, the B-tree is at most <see cref="MaxLevels"/> deep with
/// each record visited once, and a value is read only up to the size the caller allows.
/// </summary>
internal sealed class PstProperties
{
    /// <summary>A property context's own data. Real ones are a few kilobytes; long values live in subnodes.</summary>
    internal const long MaxContextBytes = 4L * 1024 * 1024;

    private const int MaxLevels = 4;
    private const int MaxProperties = 10_000;

    private readonly PstFile file;
    private readonly List<byte[]> pages;
    private readonly Dictionary<uint, PstSubnode> subnodes;
    private readonly Dictionary<ushort, (ushort Type, uint Value)> values;

    private PstProperties(PstFile file, List<byte[]> pages, Dictionary<uint, PstSubnode> subnodes, Dictionary<ushort, (ushort, uint)> values)
    {
        this.file = file;
        this.pages = pages;
        this.subnodes = subnodes;
        this.values = values;
    }

    /// <summary>The node's subnodes: attachments, embedded messages and long values.</summary>
    public IReadOnlyDictionary<uint, PstSubnode> Subnodes => subnodes;

    /// <exception cref="InvalidDataException">The node is not a property context, or it is damaged.</exception>
    public static PstProperties Read(PstFile file, PstSubnode node)
    {
        var pages = file.ReadNode(node.DataBid, MaxContextBytes, out _, out var truncated);
        if (truncated || pages.Count == 0)
        {
            throw new InvalidDataException("A message in the Outlook data file is empty or larger than any real one.");
        }
        var first = pages[0];
        if (first.Length < 12 || first[2] != 0xEC || first[3] != 0xBC)
        {
            throw new InvalidDataException("A message in the Outlook data file is not a property context.");
        }
        var heap = new PstProperties(file, pages, file.Subnodes(node.SubBid), []);
        var header = heap.Allocation(PstFile.U32(first, 4));
        if (header.Length < 8 || header[0] != 0xB5 || header[1] != 2 || header[2] != 6 || header[3] > MaxLevels)
        {
            throw new InvalidDataException("A message's property tree in the Outlook data file is damaged.");
        }
        var root = BitConverter.ToUInt32(header.Slice(4, 4));
        if (root != 0)
        {
            heap.Collect(root, header[3], new HashSet<uint>());
        }
        return heap;
    }

    private void Collect(uint hid, int level, HashSet<uint> seen)
    {
        if (!seen.Add(hid))
        {
            throw new InvalidDataException("A message's property tree in the Outlook data file loops back on itself.");
        }
        var records = Allocation(hid);
        if (level > 0)
        {
            for (var at = 0; at + 6 <= records.Length; at += 6)
            {
                Collect(BitConverter.ToUInt32(records.Slice(at + 2, 4)), level - 1, seen);
            }
            return;
        }
        for (var at = 0; at + 8 <= records.Length; at += 8)
        {
            if (values.Count >= MaxProperties)
            {
                throw new InvalidDataException("A message in the Outlook data file has more properties than any real one.");
            }
            values[BitConverter.ToUInt16(records.Slice(at, 2))] = (BitConverter.ToUInt16(records.Slice(at + 2, 2)), BitConverter.ToUInt32(records.Slice(at + 4, 4)));
        }
    }

    /// <summary>One allocation in the heap ([MS-PST] 2.3.1): which page, which slot, checked against the page map.</summary>
    private ReadOnlySpan<byte> Allocation(uint hid)
    {
        if ((hid & 0x1F) != 0)
        {
            throw new InvalidDataException("A heap reference in the Outlook data file is not one.");
        }
        var index = (int)((hid >> 5) & 0x7FF);
        var block = (int)(hid >> 16);
        if (index == 0 || block >= pages.Count)
        {
            throw new InvalidDataException("A heap reference in the Outlook data file points nowhere.");
        }
        var page = pages[block];
        if (page.Length < 2)
        {
            throw new InvalidDataException("A heap page in the Outlook data file is damaged.");
        }
        int map = PstFile.U16(page, 0);
        if (map + 4 > page.Length)
        {
            throw new InvalidDataException("A heap page in the Outlook data file is damaged.");
        }
        int count = PstFile.U16(page, map);
        if (index > count || map + 4 + ((count + 1) * 2) > page.Length)
        {
            throw new InvalidDataException("A heap reference in the Outlook data file points past its page.");
        }
        int start = PstFile.U16(page, map + 4 + ((index - 1) * 2));
        int end = PstFile.U16(page, map + 4 + (index * 2));
        if (start > end || end > map)
        {
            throw new InvalidDataException("A heap page in the Outlook data file is damaged.");
        }
        return page.AsSpan(start, end - start);
    }

    public bool Has(ushort id) => values.ContainsKey(id);

    public int? Int32(ushort id) => values.TryGetValue(id, out var v) && v.Type is 0x0003 or 0x0002 ? (int)v.Value : null;

    /// <summary>A string property, Unicode or 8-bit (read as Windows-1252), up to <paramref name="maxChars"/> characters.</summary>
    public string Text(ushort id, int maxChars)
    {
        if (!values.TryGetValue(id, out var v) || v.Type is not (0x001F or 0x001E))
        {
            return "";
        }
        var unicode = v.Type == 0x001F;
        var bytes = Bytes(v.Value, unicode ? (long)maxChars * 2 : maxChars, out _, out _);
        var text = unicode ? Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1) : LegacyText.Windows1252.GetString(bytes);
        return text.TrimEnd('\0');
    }

    /// <summary>A binary or string property's bytes, up to <paramref name="maxBytes"/>.</summary>
    /// <param name="size">The value's whole size.</param>
    public byte[]? Binary(ushort id, long maxBytes, out long size, out bool truncated)
    {
        size = 0;
        truncated = false;
        if (!values.TryGetValue(id, out var v) || v.Type is not (0x0102 or 0x001F or 0x001E))
        {
            return null;
        }
        return Bytes(v.Value, maxBytes, out size, out truncated);
    }

    /// <summary>The type a property is stored as, or 0 when the node does not have it.</summary>
    public ushort TypeOf(ushort id) => values.TryGetValue(id, out var v) ? v.Type : (ushort)0;

    /// <summary>An object property ([MS-PST] 2.3.3.5): the subnode that holds the object, such as an attached message.</summary>
    public uint? ObjectNid(ushort id)
    {
        if (!values.TryGetValue(id, out var v) || v.Type != 0x000D || v.Value == 0 || (v.Value & 0x1F) != 0)
        {
            return null;
        }
        var reference = Allocation(v.Value);
        return reference.Length >= 4 ? BitConverter.ToUInt32(reference[..4]) : null;
    }

    /// <summary>A value's bytes: from the heap when the reference is a heap id, otherwise from the subnode it names.</summary>
    private byte[] Bytes(uint hnid, long maxBytes, out long size, out bool truncated)
    {
        truncated = false;
        if (hnid == 0)
        {
            size = 0;
            return [];
        }
        if ((hnid & 0x1F) == 0)
        {
            var value = Allocation(hnid);
            size = value.Length;
            truncated = value.Length > maxBytes;
            return value[..(int)Math.Min(value.Length, maxBytes)].ToArray();
        }
        if (!subnodes.TryGetValue(hnid, out var sub))
        {
            throw new InvalidDataException("A value in the Outlook data file refers to a subnode that is missing.");
        }
        var blocks = file.ReadNode(sub.DataBid, maxBytes, out size, out truncated);
        var bytes = new byte[Math.Min(blocks.Sum(b => (long)b.Length), maxBytes)];
        var at = 0;
        foreach (var block in blocks)
        {
            var take = Math.Min(block.Length, bytes.Length - at);
            block.AsSpan(0, take).CopyTo(bytes.AsSpan(at));
            at += take;
        }
        return bytes;
    }
}
