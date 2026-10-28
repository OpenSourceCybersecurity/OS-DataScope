using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DataScope.Scanner.Content.Outlook;

namespace DataScope.Scanner.Tests.Content;

/// <summary>A message to put in an Outlook data file built by <see cref="PstBuilder"/>.</summary>
internal sealed class PstMessage
{
    public string? Subject { get; init; }

    /// <summary>What kind of item it is: IPM.Note for an email, IPM.Contact for a contact, and so on.</summary>
    public string Class { get; init; } = "IPM.Note";

    /// <summary>Store the subject and plain body as 8-bit text (Windows-1252) instead of Unicode.</summary>
    public bool Ansi { get; init; }

    public string? Body { get; init; }

    /// <summary>The HTML body, stored as bytes as Outlook does.</summary>
    public string? Html { get; init; }

    /// <summary>The body as compressed RTF ([MS-OXRTFCP]), already compressed.</summary>
    public byte[]? Rtf { get; init; }

    public List<PstAttachment> Attachments { get; init; } = [];
}

/// <summary>An attachment: a file by value, a message attached to the message, or a link to a file elsewhere.</summary>
internal sealed record PstAttachment(string Name, byte[]? Data = null, PstMessage? Message = null, int Method = 1)
{
    public static PstAttachment File(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    public static PstAttachment Attached(PstMessage message) => new("", Message: message, Method: 5);
}

/// <summary>
/// Writes Outlook data files ([MS-PST]) for the tests, in all three layouts, as no free tool on Linux can: the node
/// and block trees (with as many levels as the entries need), data blocks with their trailers, checksums and signatures,
/// Outlook's encodings, data trees for long values, subnode trees, and property contexts on heaps. Only what a reader
/// of messages needs is written: no folders' tables, allocation maps or search index, which Outlook would rebuild.
/// The files the tests read were checked with libpff, an independent reader, while this was written.
/// </summary>
internal sealed class PstBuilder(PstFormat format = PstFormat.Unicode, byte crypt = 1)
{
    private const int ValueInHeapMax = 3_580;

    private readonly List<(uint Nid, ulong Data, ulong Sub)> nodes = [];
    private readonly List<(ulong Bid, byte[] Stored, int Inflated)> blocks = [];
    private ulong nextBid = 4;
    private uint nextNid = 0x400;

    /// <summary>Outlook 2013 .ost only: store blocks compressed with zlib when that makes them smaller.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Spread long values over an XXBLOCK of XBLOCKs even when one XBLOCK would do.</summary>
    public bool TwoLevelDataTrees { get; init; }

    /// <summary>Put each message's subnodes under an SIBLOCK, as Outlook does for messages with many attachments.</summary>
    public bool TwoLevelSubnodeTrees { get; init; }

    /// <summary>The data bytes in one block: a block is at most 8 KB with its trailer.</summary>
    public int BlockData { get; init; } = format == PstFormat.Ansi ? 8_180 : 8_176;

    /// <summary>Makes the .ost header ("SO") instead of a .pst one ("SM").</summary>
    public bool Ost { get; init; }

    private bool AnsiFormat => format == PstFormat.Ansi;

    private int IdSize => AnsiFormat ? 4 : 8;

    /// <summary>Each message's own data block, in the order added, for tests that damage one.</summary>
    public List<ulong> MessageBlocks { get; } = [];

    /// <summary>Where each block and page was written, once <see cref="Build"/> has run.</summary>
    public Dictionary<ulong, long> Offsets { get; } = [];

    /// <summary>Where the node tree's root page was written, once <see cref="Build"/> has run.</summary>
    public long NodeRootOffset { get; private set; }

    public PstBuilder Add(PstMessage message)
    {
        var (data, sub) = WriteMessage(message);
        nodes.Add((Nid(0x04), data, sub));
        MessageBlocks.Add(data);
        return this;
    }

    /// <summary>A node that is not a message, such as a folder, which a reader of messages passes over.</summary>
    public PstBuilder AddOther(int type)
    {
        nodes.Add((Nid(type), Block([0xEC, 0xEC]), 0));
        return this;
    }

    private uint Nid(int type) => (nextNid++ << 5) | (uint)type;

    private ulong NewBid(bool internalBlock)
    {
        var bid = nextBid;
        nextBid += 4;
        return internalBlock ? bid | 2 : bid;
    }

    /// <summary>A data block, encoded as the file's header says.</summary>
    private ulong Block(byte[] data)
    {
        var bid = NewBid(internalBlock: false);
        var stored = (byte[])data.Clone();
        if (crypt == 1)
        {
            Encode(stored, inverseCompressible);
        }
        else if (crypt == 2)
        {
            EncodeHigh(stored, (uint)bid);
        }
        AddStored(bid, stored);
        return bid;
    }

    private ulong InternalBlock(byte[] data)
    {
        var bid = NewBid(internalBlock: true);
        AddStored(bid, data);
        return bid;
    }

    private void AddStored(ulong bid, byte[] data)
    {
        if (format == PstFormat.Unicode4K && Compress)
        {
            var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(data);
            }
            if (packed.Length < data.Length)
            {
                blocks.Add((bid, packed.ToArray(), data.Length));
                return;
            }
        }
        blocks.Add((bid, data, data.Length));
    }

    /// <summary>A long value's data: one block, or an XBLOCK (or XXBLOCK) listing blocks.</summary>
    internal ulong Node(byte[] data)
    {
        if (data.Length <= BlockData && !TwoLevelDataTrees)
        {
            return Block(data);
        }
        var ids = new List<ulong>();
        for (var at = 0; at < data.Length; at += BlockData)
        {
            ids.Add(Block(data[at..Math.Min(data.Length, at + BlockData)]));
        }
        if (!TwoLevelDataTrees)
        {
            return InternalBlock(DataTree(1, ids, data.Length));
        }
        var half = (ids.Count + 1) / 2;
        var firstBytes = Math.Min(data.Length, half * BlockData);
        var first = InternalBlock(DataTree(1, ids[..half], firstBytes));
        if (half == ids.Count)
        {
            return InternalBlock(DataTree(2, [first], data.Length));
        }
        var second = InternalBlock(DataTree(1, ids[half..], data.Length - firstBytes));
        return InternalBlock(DataTree(2, [first, second], data.Length));
    }

    private byte[] DataTree(int level, List<ulong> ids, int total)
    {
        var b = new byte[8 + (ids.Count * IdSize)];
        b[0] = 0x01;
        b[1] = (byte)level;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)ids.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)total);
        for (var i = 0; i < ids.Count; i++)
        {
            WriteId(b, 8 + (i * IdSize), ids[i]);
        }
        return b;
    }

    private (ulong Data, ulong Sub) WriteMessage(PstMessage m)
    {
        var props = new List<(ushort Id, ushort Type, byte[]? Bytes, uint Inline)>();
        var subnodes = new List<(uint Nid, ulong Data, ulong Sub)>();
        void Text(ushort id, string? value, bool ansi)
        {
            if (value is not null)
            {
                props.Add(ansi ? (id, (ushort)0x001E, Encoding.Latin1.GetBytes(value), 0u) : (id, (ushort)0x001F, Encoding.Unicode.GetBytes(value), 0u));
            }
        }
        Text(0x001A, m.Class, ansi: false);
        Text(0x0037, m.Subject, m.Ansi);
        Text(0x1000, m.Body, m.Ansi);
        if (m.Html is not null)
        {
            props.Add((0x1013, 0x0102, Encoding.UTF8.GetBytes(m.Html), 0));
        }
        if (m.Rtf is not null)
        {
            props.Add((0x1009, 0x0102, m.Rtf, 0));
        }
        props.Add((0x0E07, 0x0003, null, 1)); // PidTagMessageFlags: read.
        foreach (var a in m.Attachments)
        {
            var (data, sub) = WriteAttachment(a);
            subnodes.Add((Nid(0x05), data, sub));
        }
        return Context(props, subnodes);
    }

    private (ulong Data, ulong Sub) WriteAttachment(PstAttachment a)
    {
        var props = new List<(ushort Id, ushort Type, byte[]? Bytes, uint Inline)>
        {
            (0x3705, 0x0003, null, (uint)a.Method),
        };
        var subnodes = new List<(uint Nid, ulong Data, ulong Sub)>();
        if (a.Name.Length > 0)
        {
            props.Add((0x3707, 0x001F, Encoding.Unicode.GetBytes(a.Name), 0));
        }
        if (a.Message is { } embedded)
        {
            var nid = Nid(0x04);
            var (data, sub) = WriteMessage(embedded);
            subnodes.Add((nid, data, sub));
            var reference = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(reference, nid);
            BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(4), 100);
            props.Add((0x3701, 0x000D, reference, 0));
        }
        else if (a.Data is { } bytes)
        {
            props.Add((0x3701, 0x0102, bytes, 0));
        }
        return Context(props, subnodes);
    }

    /// <summary>A property context: a heap holding a B-tree of the properties, and its subnodes.</summary>
    private (ulong Data, ulong Sub) Context(List<(ushort Id, ushort Type, byte[]? Bytes, uint Inline)> props,
        List<(uint Nid, ulong Data, ulong Sub)> subnodes)
    {
        var allocations = new List<byte[]> { new byte[8], Array.Empty<byte>() }; // The B-tree's header and its records, filled in below.
        var records = new List<byte[]>();
        var heapBytes = 0;
        foreach (var (id, type, bytes, inline) in props.OrderBy(p => p.Id))
        {
            uint value;
            if (bytes is null)
            {
                value = inline;
            }
            else if (bytes.Length <= ValueInHeapMax && heapBytes + bytes.Length < 6_000 || type == 0x000D)
            {
                allocations.Add(bytes);
                heapBytes += bytes.Length;
                value = (uint)allocations.Count << 5; // A heap id in page 0.
            }
            else
            {
                var nid = Nid(0x1F);
                subnodes.Add((nid, Node(bytes), 0));
                value = nid;
            }
            var record = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(record, id);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), value);
            records.Add(record);
        }
        allocations[0] = [0xB5, 2, 6, 0, 0x40, 0, 0, 0]; // Keys of 2 bytes, values of 6, one level, records at heap id 0x40.
        allocations[1] = [.. records.SelectMany(r => r)];

        var heap = new MemoryStream();
        heap.Write(new byte[12]);
        var offsets = new List<int> { 12 };
        foreach (var a in allocations)
        {
            heap.Write(a);
            offsets.Add((int)heap.Length);
        }
        var map = (int)heap.Length;
        var page = heap.ToArray();
        var bytesOut = new byte[map + 4 + (offsets.Count * 2)];
        page.CopyTo(bytesOut, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytesOut, (ushort)map);
        bytesOut[2] = 0xEC;
        bytesOut[3] = 0xBC;
        BinaryPrimitives.WriteUInt32LittleEndian(bytesOut.AsSpan(4), 0x20); // The B-tree header is allocation 1.
        BinaryPrimitives.WriteUInt16LittleEndian(bytesOut.AsSpan(map), (ushort)allocations.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytesOut.AsSpan(map + 4 + (i * 2)), (ushort)offsets[i]);
        }
        // The heap is written as one page, so it is one block.
        return (Block(bytesOut), Subnodes(subnodes));
    }

    private ulong Subnodes(List<(uint Nid, ulong Data, ulong Sub)> subnodes)
    {
        if (subnodes.Count == 0)
        {
            return 0;
        }
        var sorted = subnodes.OrderBy(s => s.Nid).ToList();
        if (!TwoLevelSubnodeTrees)
        {
            return InternalBlock(SubnodeLeaf(sorted));
        }
        // One SLBLOCK per subnode under an SIBLOCK.
        var leaves = sorted.Select(s => (s.Nid, Bid: InternalBlock(SubnodeLeaf([s])))).ToList();
        var start = AnsiFormat ? 4 : 8;
        var b = new byte[start + (leaves.Count * 2 * IdSize)];
        b[0] = 0x02;
        b[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)leaves.Count);
        for (var i = 0; i < leaves.Count; i++)
        {
            WriteId(b, start + (i * 2 * IdSize), leaves[i].Nid);
            WriteId(b, start + (i * 2 * IdSize) + IdSize, leaves[i].Bid);
        }
        return InternalBlock(b);
    }

    private byte[] SubnodeLeaf(List<(uint Nid, ulong Data, ulong Sub)> entries)
    {
        var start = AnsiFormat ? 4 : 8;
        var b = new byte[start + (entries.Count * 3 * IdSize)];
        b[0] = 0x02;
        b[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var at = start + (i * 3 * IdSize);
            WriteId(b, at, entries[i].Nid);
            WriteId(b, at + IdSize, entries[i].Data);
            WriteId(b, at + (2 * IdSize), entries[i].Sub);
        }
        return b;
    }

    // ---------- Layout ----------

    private int PageSize => format == PstFormat.Unicode4K ? 4096 : 512;

    private int EntriesBytes => format switch { PstFormat.Ansi => 496, PstFormat.Unicode => 488, _ => 4056 };

    private int TrailerOffset => format switch { PstFormat.Ansi => 500, PstFormat.Unicode => 496, _ => 4072 };

    private int BlockTrailer => format switch { PstFormat.Ansi => 12, PstFormat.Unicode => 16, _ => 24 };

    private int BlockAlign => format == PstFormat.Unicode4K ? 512 : 64;

    public byte[] Build()
    {
        var file = new MemoryStream();
        file.Write(new byte[0x4400]); // Where Outlook puts the first allocation map; the header comes first.
        var placed = new List<(ulong Bid, long Offset, int Size)>();
        foreach (var (bid, stored, inflated) in blocks.OrderBy(b => b.Bid))
        {
            var total = (stored.Length + BlockTrailer + BlockAlign - 1) / BlockAlign * BlockAlign;
            var offset = file.Length;
            var buffer = new byte[total];
            stored.CopyTo(buffer, 0);
            var t = total - BlockTrailer;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(t), (ushort)stored.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(t + 2), Signature(offset, bid));
            var crc = PstFile.Crc(stored);
            if (AnsiFormat)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(t + 4), (uint)bid);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(t + 8), crc);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(t + 4), crc);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(t + 8), bid);
                if (format == PstFormat.Unicode4K)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(t + 18), (ushort)inflated);
                }
            }
            file.Write(buffer);
            placed.Add((bid, offset, stored.Length));
            Offsets[bid] = offset;
        }
        Pad(file, PageSize);

        var blockEntry = AnsiFormat ? 12 : 24;
        var blockRoot = Tree(file, 0x80, blockEntry, placed.Select(p =>
        {
            var e = new byte[blockEntry];
            WriteId(e, 0, p.Bid);
            WriteId(e, IdSize, (ulong)p.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2 * IdSize), (ushort)p.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan((2 * IdSize) + 2), 2);
            return (p.Bid, e);
        }).ToList());
        var nodeEntry = AnsiFormat ? 16 : 32;
        var nodeRoot = Tree(file, 0x81, nodeEntry, nodes.OrderBy(n => n.Nid).Select(n =>
        {
            var e = new byte[nodeEntry];
            WriteId(e, 0, n.Nid);
            WriteId(e, IdSize, n.Data);
            WriteId(e, 2 * IdSize, n.Sub);
            // Every message's folder: one the file does not have, so other readers list the messages as orphans.
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(3 * IdSize), 0x8082);
            return ((ulong)n.Nid, e);
        }).ToList());

        NodeRootOffset = nodeRoot.Offset;
        var bytes = file.ToArray();
        WriteHeader(bytes, nodeRoot, blockRoot);
        return bytes;
    }

    /// <summary>Writes a B-tree's pages, leaves first, and returns the root page's identifier and offset.</summary>
    private (ulong Bid, long Offset) Tree(MemoryStream file, byte type, int entrySize, List<(ulong Key, byte[] Entry)> entries)
    {
        var level = 0;
        var perPage = EntriesBytes / entrySize;
        var current = entries;
        while (true)
        {
            var pages = new List<(ulong Key, ulong Bid, long Offset)>();
            var chunks = current.Count == 0 ? [[]] : current.Chunk(perPage).Select(c => c.ToList()).ToList();
            foreach (var chunk in chunks)
            {
                var bid = NewBid(internalBlock: false);
                var offset = file.Length;
                var page = new byte[PageSize];
                for (var i = 0; i < chunk.Count; i++)
                {
                    chunk[i].Entry.CopyTo(page, i * entrySize);
                }
                if (format == PstFormat.Unicode4K)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(EntriesBytes), (ushort)chunk.Count);
                    BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(EntriesBytes + 2), (ushort)perPage);
                    page[EntriesBytes + 4] = (byte)entrySize;
                    page[EntriesBytes + 5] = (byte)level;
                }
                else
                {
                    page[EntriesBytes] = (byte)chunk.Count;
                    page[EntriesBytes + 1] = (byte)perPage;
                    page[EntriesBytes + 2] = (byte)entrySize;
                    page[EntriesBytes + 3] = (byte)level;
                }
                var t = TrailerOffset;
                page[t] = type;
                page[t + 1] = type;
                BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(t + 2), Signature(offset, bid));
                var crc = PstFile.Crc(page.AsSpan(0, t));
                if (AnsiFormat)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 4), (uint)bid);
                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 8), crc);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(t + 4), crc);
                    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(t + 8), bid);
                }
                file.Write(page);
                Offsets[bid] = offset;
                pages.Add((chunk.Count > 0 ? chunk[0].Key : 0, bid, offset));
            }
            if (pages.Count == 1)
            {
                return (pages[0].Bid, pages[0].Offset);
            }
            // The next level up: one entry (first key, page id, page offset) per page below.
            level++;
            entrySize = 3 * IdSize;
            perPage = EntriesBytes / entrySize;
            current = pages.Select(p =>
            {
                var e = new byte[3 * IdSize];
                WriteId(e, 0, p.Key);
                WriteId(e, IdSize, p.Bid);
                WriteId(e, 2 * IdSize, (ulong)p.Offset);
                return (p.Key, e);
            }).ToList();
        }
    }

    private void WriteHeader(byte[] b, (ulong Bid, long Offset) nodeRoot, (ulong Bid, long Offset) blockRoot)
    {
        "!BDN"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)(Ost ? 0x4F53 : 0x4D53));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), format switch { PstFormat.Ansi => 14, PstFormat.Unicode => 23, _ => 36 });
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 19);
        b[14] = 1;
        b[15] = 1;
        if (AnsiFormat)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)nextBid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(168), (uint)b.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(184), (uint)nodeRoot.Bid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(188), (uint)nodeRoot.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(192), (uint)blockRoot.Bid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(196), (uint)blockRoot.Offset);
            b[460] = 0x80;
            b[461] = crypt;
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(184), (ulong)b.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(216), nodeRoot.Bid);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(224), (ulong)nodeRoot.Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(232), blockRoot.Bid);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(240), (ulong)blockRoot.Offset);
            b[512] = 0x80;
            b[513] = crypt;
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(516), nextBid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(524), PstFile.Crc(b.AsSpan(8, 516)));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), PstFile.Crc(b.AsSpan(8, 471)));
    }

    private void WriteId(byte[] b, int at, ulong id)
    {
        if (AnsiFormat)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)id);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), id);
        }
    }

    private static void Pad(MemoryStream file, int to)
    {
        var extra = (int)((to - (file.Length % to)) % to);
        file.Write(new byte[extra]);
    }

    private static ushort Signature(long offset, ulong bid)
    {
        var x = (ulong)offset ^ bid;
        return (ushort)((x >> 16) ^ x);
    }

    // ---------- Encoding: the inverse of what readers undo ----------

    private static readonly byte[] compressible = DecodeTable(b => { var x = new[] { b }; PstCrypt.DecodeCompressible(x); return x[0]; });
    private static readonly byte[] inverseCompressible = Inverse(compressible);

    private static byte[] DecodeTable(Func<byte, byte> decode) => [.. Enumerable.Range(0, 256).Select(i => decode((byte)i))];

    private static byte[] Inverse(byte[] table)
    {
        var inverse = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            inverse[table[i]] = (byte)i;
        }
        return inverse;
    }

    private static void Encode(byte[] data, byte[] table)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = table[data[i]];
        }
    }

    /// <summary>
    /// "High" encoding: for each byte, the value whose decoding gives it. Decoding one byte at a position depends only on
    /// that byte and the salt, so each is found by trying all 256.
    /// </summary>
    private static void EncodeHigh(byte[] data, uint key)
    {
        var salt = (ushort)((key >> 16) ^ key);
        var probe = new byte[1];
        for (var i = 0; i < data.Length; i++)
        {
            // A key with nothing in its top half salts its first byte with itself: this byte's salt.
            var keyAt = (ushort)(salt + i);
            for (var c = 0; c < 256; c++)
            {
                probe[0] = (byte)c;
                PstCrypt.DecodeHigh(probe, keyAt);
                if (probe[0] == data[i])
                {
                    data[i] = (byte)c;
                    break;
                }
            }
        }
    }
}
