using System.Buffers.Binary;
using System.IO.Compression;

namespace DataScope.Scanner.Content.Outlook;

/// <summary>The three layouts of an Outlook data file ([MS-PST] 2.2.2.6).</summary>
internal enum PstFormat
{
    /// <summary>Outlook 97-2002 (.pst): 32-bit identifiers and offsets, 512-byte pages, 2 GB at most.</summary>
    Ansi,

    /// <summary>Outlook 2003 and later (.pst, and .ost before Outlook 2013): 64-bit identifiers, 512-byte pages.</summary>
    Unicode,

    /// <summary>Outlook 2013 and later (.ost): 4,096-byte pages and blocks that may be compressed with zlib.</summary>
    Unicode4K,
}

/// <summary>A node in the file's node tree: a folder, a message, a table and so on.</summary>
internal readonly record struct PstNodeEntry(uint Nid, ulong DataBid, ulong SubBid);

/// <summary>A node in a node's own subnode tree, such as a message's attachments and long property values.</summary>
internal readonly record struct PstSubnode(ulong DataBid, ulong SubBid);

/// <summary>The file is an Outlook data file Data Scope can't read; <see cref="Exception.Message"/> says why, plainly.</summary>
internal sealed class PstUnsupportedException(string message, bool encrypted = false) : Exception(message)
{
    public bool Encrypted { get; } = encrypted;
}

/// <summary>
/// Reads the node and block layer ([MS-PST] 2.2) of an Outlook data file (.pst or .ost), written for untrusted input.
/// Every page and block is checked against the file's length, its own identifier, its signature and its checksum
/// before it is used; each B-tree is walked with each page visited at most once and its levels counting down, so a
/// loop or a lie about depth ends the walk; data trees and subnode trees are at most two levels deep; and no node's
/// data is gathered past the size the caller allows. Blocks are decoded from Outlook's light obfuscation ("compressible"
/// or "high" encryption, which needs no password) and, in Outlook 2013 .ost files, decompressed into a buffer no larger
/// than the block says it holds. Nothing is ever written.
/// </summary>
internal sealed class PstFile
{
    public static ReadOnlySpan<byte> Magic => "!BDN"u8;

    /// <summary>B-trees deeper than this are not real: a file of terabytes needs fewer levels.</summary>
    private const int MaxTreeLevels = 8;

    /// <summary>Pages of the block tree kept in memory, so looking up a message's blocks rarely reads the file.</summary>
    private const int CachedPages = 512;

    /// <summary>Subnodes one node may have: far more attachments and long values than any message holds.</summary>
    internal const int MaxSubnodes = 100_000;

    private const byte BlockTreePage = 0x80;
    private const byte NodeTreePage = 0x81;

    private readonly Stream stream;
    private readonly long length;
    private readonly long nodeRoot;
    private readonly long blockRoot;
    private readonly byte crypt;
    private readonly Dictionary<long, byte[]> pageCache = [];
    private readonly Queue<long> pageOrder = new();

    private PstFile(Stream stream, PstFormat format, bool isOst, long nodeRoot, long blockRoot, byte crypt)
    {
        this.stream = stream;
        length = stream.Length;
        Format = format;
        IsOst = isOst;
        this.nodeRoot = nodeRoot;
        this.blockRoot = blockRoot;
        this.crypt = crypt;
    }

    public PstFormat Format { get; }

    /// <summary>An offline copy of a mailbox (.ost) rather than a personal folders file (.pst).</summary>
    public bool IsOst { get; }

    private bool Ansi => Format == PstFormat.Ansi;

    private int IdSize => Ansi ? 4 : 8;

    private int PageSize => Format == PstFormat.Unicode4K ? 4096 : 512;

    /// <summary>Where a page's entries end and its counts begin.</summary>
    private int EntriesBytes => Format switch { PstFormat.Ansi => 496, PstFormat.Unicode => 488, _ => 4056 };

    /// <summary>The page trailer: type, type again, signature, checksum and the page's own identifier.</summary>
    private int TrailerOffset => Format switch { PstFormat.Ansi => 500, PstFormat.Unicode => 496, _ => 4072 };

    private int BlockTrailer => Format switch { PstFormat.Ansi => 12, PstFormat.Unicode => 16, _ => 24 };

    private int BlockAlign => Format == PstFormat.Unicode4K ? 512 : 64;

    private int MaxBlockBytes => Format == PstFormat.Unicode4K ? 65_536 : 8_192;

    /// <summary>Opens the data file in <paramref name="stream"/>, which must be seekable.</summary>
    /// <exception cref="InvalidDataException">Not an Outlook data file, or its header is damaged.</exception>
    /// <exception cref="PstUnsupportedException">An Outlook data file in a form Data Scope does not read.</exception>
    public static PstFile Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[564];
        stream.Position = 0;
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 512 || !header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an Outlook data file.");
        }
        var client = U16(header, 8);
        if (client is not (0x4D53 or 0x4F53))
        {
            throw new InvalidDataException("Not an Outlook data file.");
        }
        var format = U16(header, 10) switch
        {
            14 or 15 => PstFormat.Ansi,
            23 => PstFormat.Unicode,
            36 => PstFormat.Unicode4K,
            _ => throw new PstUnsupportedException("The file is in a version of the Outlook data file format Data Scope does not know."),
        };
        if (format != PstFormat.Ansi && read < header.Length)
        {
            throw new InvalidDataException("The Outlook data file's header is cut short.");
        }
        long nodeRoot, blockRoot;
        byte crypt;
        if (format == PstFormat.Ansi)
        {
            nodeRoot = U32(header, 188);
            blockRoot = U32(header, 196);
            crypt = header[461];
        }
        else
        {
            nodeRoot = checked((long)U64(header, 224));
            blockRoot = checked((long)U64(header, 240));
            crypt = header[513];
        }
        if (crypt == 0x10)
        {
            // Windows Information Protection: the contents are truly encrypted, with a key Data Scope does not have.
            throw new PstUnsupportedException("The Outlook data file is encrypted by Windows Information Protection.", encrypted: true);
        }
        if (crypt > 2)
        {
            throw new PstUnsupportedException("The Outlook data file uses an encoding Data Scope does not know.");
        }
        return new PstFile(stream, format, client == 0x4F53, nodeRoot, blockRoot, crypt);
    }

    /// <summary>The kind of node an identifier names: its low five bits ([MS-PST] 2.2.2.1).</summary>
    public static int NidType(uint nid) => (int)(nid & 0x1F);

    /// <summary>Every node in the node tree, in identifier order. The walk visits each page once.</summary>
    public IEnumerable<PstNodeEntry> Nodes(CancellationToken ct)
    {
        var entrySize = Ansi ? 16 : 32;
        foreach (var (page, count, size) in Leaves(nodeRoot, NodeTreePage, ct))
        {
            if (size < entrySize)
            {
                throw new InvalidDataException("A node tree page lists entries smaller than a node.");
            }
            for (var i = 0; i < count; i++)
            {
                var at = i * size;
                yield return Ansi
                    ? new PstNodeEntry(U32(page, at), U32(page, at + 4), U32(page, at + 8))
                    : new PstNodeEntry((uint)U64(page, at), U64(page, at + 8), U64(page, at + 16));
            }
        }
    }

    /// <summary>The leaf pages of a B-tree, depth first, each with its entry count and entry size.</summary>
    private IEnumerable<(byte[] Page, int Count, int Size)> Leaves(long root, byte type, CancellationToken ct)
    {
        var seen = new HashSet<long>();
        var stack = new Stack<(long Offset, int Level)>();
        stack.Push((root, -1));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (offset, expected) = stack.Pop();
            if (!seen.Add(offset))
            {
                throw new InvalidDataException("A tree in the Outlook data file loops back on itself.");
            }
            var (page, count, size, level) = ReadPage(offset, type);
            if (expected >= 0 && level != expected || level > MaxTreeLevels)
            {
                throw new InvalidDataException("A tree in the Outlook data file has inconsistent levels.");
            }
            if (level == 0)
            {
                yield return (page, count, size);
                continue;
            }
            // Pushed in reverse, so children are visited in key order.
            for (var i = count - 1; i >= 0; i--)
            {
                stack.Push((ChildOffset(page, i, size), level - 1));
            }
        }
    }

    private long ChildOffset(byte[] page, int index, int size)
    {
        if (size < 3 * IdSize)
        {
            throw new InvalidDataException("A tree page lists entries too small to point anywhere.");
        }
        var at = (index * size) + (2 * IdSize);
        return Ansi ? U32(page, at) : checked((long)U64(page, at));
    }

    /// <summary>Reads and checks one B-tree page: where it says it is, of the type expected, with a valid checksum.</summary>
    private (byte[] Page, int Count, int Size, int Level) ReadPage(long offset, byte type)
    {
        if (!pageCache.TryGetValue(offset, out var page))
        {
            page = ReadAt(offset, PageSize);
            var t = TrailerOffset;
            if (page[t] != type || page[t + 1] != type)
            {
                throw new InvalidDataException("A tree page in the Outlook data file is not where it should be.");
            }
            uint stored;
            ulong bid;
            if (Ansi)
            {
                bid = U32(page, t + 4);
                stored = U32(page, t + 8);
            }
            else
            {
                stored = U32(page, t + 4);
                bid = U64(page, t + 8);
            }
            if (U16(page, t + 2) != Signature(offset, bid) || Crc(page.AsSpan(0, t)) != stored)
            {
                throw new InvalidDataException("A tree page in the Outlook data file is damaged.");
            }
            if (type == BlockTreePage)
            {
                Remember(offset, page);
            }
        }
        int count, size, level;
        if (Format == PstFormat.Unicode4K)
        {
            count = U16(page, EntriesBytes);
            size = page[EntriesBytes + 4];
            level = page[EntriesBytes + 5];
        }
        else
        {
            count = page[EntriesBytes];
            size = page[EntriesBytes + 2];
            level = page[EntriesBytes + 3];
        }
        if (size == 0 || count * size > EntriesBytes)
        {
            throw new InvalidDataException("A tree page in the Outlook data file lists more entries than fit.");
        }
        return (page, count, size, level);
    }

    private void Remember(long offset, byte[] page)
    {
        if (pageCache.Count >= CachedPages)
        {
            pageCache.Remove(pageOrder.Dequeue());
        }
        pageCache[offset] = page;
        pageOrder.Enqueue(offset);
    }

    /// <summary>Finds a block in the block tree: its offset in the file and its stored size.</summary>
    private (long Offset, int Size) FindBlock(ulong bid)
    {
        var key = bid & ~1UL; // The lowest bit is reserved and ignored.
        var offset = blockRoot;
        int? expected = null;
        for (var depth = 0; depth <= MaxTreeLevels; depth++)
        {
            var (page, count, size, level) = ReadPage(offset, BlockTreePage);
            if (expected is { } e && level != e || level > MaxTreeLevels)
            {
                throw new InvalidDataException("The block tree has inconsistent levels.");
            }
            if (level == 0)
            {
                var entry = Ansi ? 12 : 24;
                if (size < entry)
                {
                    throw new InvalidDataException("A block tree page lists entries smaller than a block.");
                }
                for (var i = 0; i < count; i++)
                {
                    var at = i * size;
                    var id = (Ansi ? U32(page, at) : U64(page, at)) & ~1UL;
                    if (id == key)
                    {
                        var ib = Ansi ? U32(page, at + 4) : checked((long)U64(page, at + 8));
                        return (ib, U16(page, at + (2 * IdSize)));
                    }
                }
                throw new InvalidDataException("A block the Outlook data file refers to is missing.");
            }
            // The last child whose first key is not above the one sought.
            var child = -1;
            for (var i = 0; i < count; i++)
            {
                var first = (Ansi ? U32(page, i * size) : U64(page, i * size)) & ~1UL;
                if (first > key)
                {
                    break;
                }
                child = i;
            }
            if (child < 0)
            {
                throw new InvalidDataException("A block the Outlook data file refers to is missing.");
            }
            offset = ChildOffset(page, child, size);
            expected = level - 1;
        }
        throw new InvalidDataException("The block tree is deeper than any real file.");
    }

    /// <summary>One block's data, checked, decoded and (in Outlook 2013 .ost files) decompressed.</summary>
    public byte[] ReadBlock(ulong bid)
    {
        var (offset, size) = FindBlock(bid);
        var total = (size + BlockTrailer + BlockAlign - 1) / BlockAlign * BlockAlign;
        if (total > MaxBlockBytes)
        {
            throw new InvalidDataException("A block in the Outlook data file is larger than blocks can be.");
        }
        var raw = ReadAt(offset, total);
        var t = total - BlockTrailer;
        uint stored;
        ulong trailerBid;
        if (Ansi)
        {
            trailerBid = U32(raw, t + 4);
            stored = U32(raw, t + 8);
        }
        else
        {
            stored = U32(raw, t + 4);
            trailerBid = U64(raw, t + 8);
        }
        if (U16(raw, t) != size || (trailerBid & ~1UL) != (bid & ~1UL) || U16(raw, t + 2) != Signature(offset, trailerBid)
            || Crc(raw.AsSpan(0, size)) != stored)
        {
            throw new InvalidDataException("A block in the Outlook data file is damaged.");
        }
        var data = raw.AsSpan(0, size).ToArray();
        if (Format == PstFormat.Unicode4K && U16(raw, t + 18) is var inflated and not 0 && inflated != size)
        {
            data = Inflate(data, inflated);
        }
        if (!IsInternal(bid))
        {
            Decode(data, bid);
        }
        return data;
    }

    /// <summary>Internal blocks (data and subnode trees) are never encoded; data blocks may be.</summary>
    public static bool IsInternal(ulong bid) => (bid & 2) != 0;

    private static byte[] Inflate(byte[] compressed, int size)
    {
        var output = new byte[size];
        using var zlib = new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        try
        {
            zlib.ReadExactly(output);
            // A block that inflates to more than it claims is damaged, not a reason to read on.
            if (zlib.ReadByte() >= 0)
            {
                throw new InvalidDataException("A compressed block in the Outlook data file holds more than it says.");
            }
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("A compressed block in the Outlook data file holds less than it says.");
        }
        return output;
    }

    private void Decode(byte[] data, ulong bid)
    {
        if (crypt == 1)
        {
            PstCrypt.DecodeCompressible(data);
        }
        else if (crypt == 2)
        {
            PstCrypt.DecodeHigh(data, (uint)bid);
        }
    }

    /// <summary>
    /// A node's data as its blocks, in order: one block, or the blocks a data tree lists (an XBLOCK of blocks, or an
    /// XXBLOCK of XBLOCKs). Each block is one page of a heap. Blocks stop being read once <paramref name="maxBytes"/>
    /// are gathered, so the last one may run past it.
    /// </summary>
    /// <param name="total">The size of the whole node's data, as its data tree says.</param>
    /// <param name="truncated">The node holds more than <paramref name="maxBytes"/>.</param>
    public List<byte[]> ReadNode(ulong bid, long maxBytes, out long total, out bool truncated)
    {
        var blocks = new List<byte[]>();
        truncated = false;
        total = 0;
        if (bid == 0)
        {
            return blocks;
        }
        var first = ReadBlock(bid);
        if (!IsInternal(bid))
        {
            total = first.Length;
            truncated = first.Length > maxBytes;
            blocks.Add(first);
            return blocks;
        }
        var (level, ids, size) = DataTree(first);
        total = size;
        truncated = size > maxBytes;
        var gathered = 0L;
        var seen = new HashSet<ulong> { bid & ~1UL };
        foreach (var id in ids)
        {
            var children = level == 2 ? DataTreeChildren(id, seen) : [id];
            foreach (var child in children)
            {
                if (gathered >= maxBytes)
                {
                    return blocks;
                }
                if (!seen.Add(child & ~1UL) || IsInternal(child))
                {
                    throw new InvalidDataException("A data tree in the Outlook data file is damaged.");
                }
                var block = ReadBlock(child);
                gathered += block.Length;
                blocks.Add(block);
            }
        }
        if (gathered != total)
        {
            throw new InvalidDataException("A data tree in the Outlook data file does not add up.");
        }
        return blocks;
    }

    private List<ulong> DataTreeChildren(ulong bid, HashSet<ulong> seen)
    {
        if (!IsInternal(bid) || !seen.Add(bid & ~1UL))
        {
            throw new InvalidDataException("A data tree in the Outlook data file is damaged.");
        }
        var (level, ids, _) = DataTree(ReadBlock(bid));
        if (level != 1)
        {
            throw new InvalidDataException("A data tree in the Outlook data file is deeper than allowed.");
        }
        return ids;
    }

    /// <summary>An XBLOCK or XXBLOCK: type 1, level 1 or 2, a count, the total size, then block identifiers.</summary>
    private (int Level, List<ulong> Ids, long Total) DataTree(byte[] block)
    {
        if (block.Length < 8 || block[0] != 0x01 || block[1] is not (1 or 2))
        {
            throw new InvalidDataException("A data tree in the Outlook data file is damaged.");
        }
        var count = U16(block, 2);
        if (8 + (count * IdSize) > block.Length)
        {
            throw new InvalidDataException("A data tree in the Outlook data file lists more blocks than it holds.");
        }
        var ids = new List<ulong>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(Ansi ? U32(block, 8 + (i * 4)) : U64(block, 8 + (i * 8)));
        }
        return (block[1], ids, U32(block, 4));
    }

    /// <summary>A node's subnodes (SLBLOCK, or an SIBLOCK of SLBLOCKs), by identifier.</summary>
    public Dictionary<uint, PstSubnode> Subnodes(ulong bid)
    {
        var result = new Dictionary<uint, PstSubnode>();
        if (bid == 0)
        {
            return result;
        }
        var seen = new HashSet<ulong>();
        AddSubnodes(bid, expectedLevel: null, seen, result);
        return result;
    }

    private void AddSubnodes(ulong bid, int? expectedLevel, HashSet<ulong> seen, Dictionary<uint, PstSubnode> result)
    {
        if (!IsInternal(bid) || !seen.Add(bid & ~1UL))
        {
            throw new InvalidDataException("A subnode tree in the Outlook data file is damaged.");
        }
        var block = ReadBlock(bid);
        if (block.Length < 4 || block[0] != 0x02 || block[1] > 1 || expectedLevel is { } e && block[1] != e)
        {
            throw new InvalidDataException("A subnode tree in the Outlook data file is damaged.");
        }
        var level = block[1];
        var count = U16(block, 2);
        var start = Ansi ? 4 : 8;
        var entry = (level == 0 ? 3 : 2) * IdSize;
        if (start + (count * entry) > block.Length)
        {
            throw new InvalidDataException("A subnode tree in the Outlook data file lists more entries than it holds.");
        }
        for (var i = 0; i < count; i++)
        {
            var at = start + (i * entry);
            var nid = Ansi ? U32(block, at) : (uint)U64(block, at);
            if (level == 1)
            {
                AddSubnodes(Ansi ? U32(block, at + 4) : U64(block, at + 8), 0, seen, result);
                continue;
            }
            if (result.Count >= MaxSubnodes)
            {
                throw new InvalidDataException("A node in the Outlook data file has more subnodes than any real one.");
            }
            result[nid] = Ansi
                ? new PstSubnode(U32(block, at + 4), U32(block, at + 8))
                : new PstSubnode(U64(block, at + 8), U64(block, at + 16));
        }
    }

    private byte[] ReadAt(long offset, int count)
    {
        if (offset < 0 || offset > length - count)
        {
            throw new InvalidDataException("The Outlook data file points past its end.");
        }
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>[MS-PST] 5.5: the page and block signature, from where it is and what it claims to be.</summary>
    private static ushort Signature(long offset, ulong bid)
    {
        var x = (ulong)offset ^ bid;
        return (ushort)((x >> 16) ^ x);
    }

    /// <summary>[MS-PST] 5.3: CRC-32 with the usual polynomial, started from zero and not inverted at the end.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    internal static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));

    internal static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));

    internal static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
}
