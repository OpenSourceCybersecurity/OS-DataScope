using System.Buffers.Binary;
using System.Text;

namespace DataScope.Scanner.Content.Legacy;

/// <summary>A storage (folder) or stream (file) inside a compound file.</summary>
internal sealed record CompoundEntry(int Id, string Name, bool IsStorage, uint Start, long Size, int Left, int Right, int Child);

/// <summary>
/// Reads the OLE compound file format ([MS-CFB]) that older Office files (.doc, .xls, .ppt) and Outlook messages (.msg)
/// are stored in: a small file system of storages and streams inside one file. Written for untrusted input: every
/// sector number is checked against the file's length, every chain of sectors is followed at most once per sector so a
/// loop ends, the tree of entries is walked with each entry visited once, and a stream is read only up to the limit the
/// caller gives. Nothing is ever written.
/// </summary>
internal sealed class CompoundFile
{
    internal static readonly byte[] Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFF_FFFE;
    private const uint MaxRegularSector = 0xFFFF_FFFA;
    private const int EntrySize = 128;

    /// <summary>More entries than any real document has; a directory claiming more is refused.</summary>
    internal const int MaxEntries = 100_000;

    private readonly Stream stream;
    private readonly int sectorSize;
    private readonly int miniSectorSize;
    private readonly long sectorCount;
    private readonly uint[] fat;
    private readonly uint[] miniFat;
    private readonly uint miniStreamCutoff;
    private readonly List<CompoundEntry> entries;
    private byte[]? miniStream;

    private CompoundFile(Stream stream, int sectorSize, int miniSectorSize, long sectorCount, uint[] fat, uint[] miniFat, uint cutoff, List<CompoundEntry> entries)
    {
        this.stream = stream;
        this.sectorSize = sectorSize;
        this.miniSectorSize = miniSectorSize;
        this.sectorCount = sectorCount;
        this.fat = fat;
        this.miniFat = miniFat;
        miniStreamCutoff = cutoff;
        this.entries = entries;
    }

    public CompoundEntry Root => entries[0];

    /// <summary>Opens the compound file in <paramref name="stream"/>, which must be seekable.</summary>
    /// <exception cref="InvalidDataException">Not a compound file, or its structure is damaged.</exception>
    public static CompoundFile Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[512];
        stream.Position = 0;
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a compound file.");
        }
        var major = U16(header, 0x1A);
        var sectorShift = U16(header, 0x1E);
        var miniShift = U16(header, 0x20);
        if (U16(header, 0x1C) != 0xFFFE || !(major == 3 && sectorShift == 9 || major == 4 && sectorShift == 12) || miniShift != 6)
        {
            throw new InvalidDataException("Unsupported compound file header.");
        }
        var sectorSize = 1 << sectorShift;
        var sectorCount = (stream.Length - sectorSize + sectorSize - 1) / sectorSize;
        if (sectorCount <= 0)
        {
            throw new InvalidDataException("The compound file has no sectors.");
        }

        // The FAT's own sectors: the first 109 are listed in the header, the rest in a chain of DIFAT sectors.
        var fatSectors = U32(header, 0x2C);
        if (fatSectors > sectorCount)
        {
            throw new InvalidDataException("The compound file claims more FAT sectors than it has.");
        }
        var fatList = new List<uint>((int)fatSectors);
        for (var i = 0; i < 109 && fatList.Count < fatSectors; i++)
        {
            fatList.Add(U32(header, 0x4C + (i * 4)));
        }
        var difat = U32(header, 0x44);
        var perDifat = (sectorSize / 4) - 1;
        var difatSeen = 0L;
        var buffer = new byte[sectorSize];
        while (fatList.Count < fatSectors)
        {
            if (difat > MaxRegularSector || difat >= sectorCount || ++difatSeen > sectorCount)
            {
                throw new InvalidDataException("The compound file's FAT list is damaged.");
            }
            ReadSector(stream, sectorSize, difat, buffer);
            for (var i = 0; i < perDifat && fatList.Count < fatSectors; i++)
            {
                fatList.Add(U32(buffer, i * 4));
            }
            difat = U32(buffer, perDifat * 4);
        }

        var fat = new uint[fatList.Count * (sectorSize / 4)];
        for (var i = 0; i < fatList.Count; i++)
        {
            if (fatList[i] >= sectorCount)
            {
                throw new InvalidDataException("The compound file's FAT points outside the file.");
            }
            ReadSector(stream, sectorSize, fatList[i], buffer);
            for (var j = 0; j < sectorSize / 4; j++)
            {
                fat[(i * (sectorSize / 4)) + j] = U32(buffer, j * 4);
            }
        }

        var file = new CompoundFile(stream, sectorSize, 1 << miniShift, sectorCount, fat, [], U32(header, 0x38), []);
        var directory = file.ReadChain(U32(header, 0x30), (long)MaxEntries * EntrySize);
        for (var offset = 0; offset + EntrySize <= directory.Length && file.entries.Count < MaxEntries; offset += EntrySize)
        {
            file.entries.Add(Entry(directory, offset, file.entries.Count, major));
        }
        if (file.entries.Count == 0 || directory[0x42] != 5)
        {
            throw new InvalidDataException("The compound file has no root entry.");
        }

        var miniFatBytes = U32(header, 0x40) == 0 ? [] : file.ReadChain(U32(header, 0x3C), sectorCount * sectorSize);
        var miniFat = new uint[miniFatBytes.Length / 4];
        for (var i = 0; i < miniFat.Length; i++)
        {
            miniFat[i] = U32(miniFatBytes, i * 4);
        }
        return new CompoundFile(stream, sectorSize, 1 << miniShift, sectorCount, fat, miniFat, file.miniStreamCutoff, file.entries);
    }

    private static CompoundEntry Entry(byte[] directory, int offset, int id, int major)
    {
        var nameBytes = Math.Clamp((int)U16(directory, offset + 0x40), 2, 64) - 2;
        var name = Encoding.Unicode.GetString(directory, offset, nameBytes & ~1);
        var type = directory[offset + 0x42];
        var size = major == 3 ? U32(directory, offset + 0x78) : (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(offset + 0x78)), long.MaxValue);
        return new CompoundEntry(id, name, type is 1 or 5, U32(directory, offset + 0x74), size,
            (int)U32(directory, offset + 0x44), (int)U32(directory, offset + 0x48), (int)U32(directory, offset + 0x4C));
    }

    /// <summary>
    /// The storages and streams directly inside <paramref name="storage"/>. The entries form a tree; each is visited at
    /// most once, so a damaged tree that points back at itself ends.
    /// </summary>
    public List<CompoundEntry> Children(CompoundEntry storage)
    {
        var children = new List<CompoundEntry>();
        if (!storage.IsStorage)
        {
            return children;
        }
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(storage.Child);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id < 0 || id >= entries.Count || id == storage.Id || !seen.Add(id))
            {
                continue;
            }
            var entry = entries[id];
            children.Add(entry);
            pending.Push(entry.Left);
            pending.Push(entry.Right);
        }
        return children;
    }

    /// <summary>The child named <paramref name="name"/> (names compare without case, as in the format), or null.</summary>
    public CompoundEntry? Find(CompoundEntry storage, string name) =>
        Children(storage).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A stream's bytes, or with <paramref name="truncate"/> its first <paramref name="max"/> bytes.</summary>
    /// <exception cref="ContentLimitException">The stream is larger than <paramref name="max"/> and not to be truncated.</exception>
    /// <exception cref="InvalidDataException">Its sectors are damaged.</exception>
    public byte[] Read(CompoundEntry entry, long max, bool truncate = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsStorage)
        {
            throw new InvalidDataException("A storage is not a stream.");
        }
        if (entry.Size > max && !truncate)
        {
            throw new ContentLimitException("A part of the file is larger than the safety limit.");
        }
        var size = Math.Min(entry.Size, max);
        if (entry.Size < miniStreamCutoff)
        {
            return ReadMini(entry.Start, (int)size);
        }
        var bytes = ReadChain(entry.Start, size);
        if (bytes.Length < size)
        {
            throw new InvalidDataException("A stream is shorter than its entry says.");
        }
        return bytes;
    }

    /// <summary>Follows a chain of sectors from <paramref name="start"/>, reading at most <paramref name="max"/> bytes.</summary>
    private byte[] ReadChain(uint start, long max)
    {
        var output = new MemoryStream();
        var buffer = new byte[sectorSize];
        var sector = start;
        long steps = 0;
        while (sector != EndOfChain && output.Length < max)
        {
            if (sector > MaxRegularSector || sector >= sectorCount || sector >= fat.Length || ++steps > sectorCount)
            {
                throw new InvalidDataException("A chain of sectors in the compound file is damaged.");
            }
            ReadSector(stream, sectorSize, sector, buffer);
            output.Write(buffer, 0, (int)Math.Min(sectorSize, max - output.Length));
            sector = fat[sector];
        }
        return output.ToArray();
    }

    private byte[] ReadMini(uint start, int size)
    {
        if (size == 0)
        {
            return [];
        }
        miniStream ??= ReadChain(Root.Start, Math.Min(Root.Size, sectorCount * sectorSize));
        var output = new byte[size];
        var written = 0;
        var sector = start;
        var steps = 0;
        while (written < size)
        {
            var offset = (long)sector * miniSectorSize;
            if (sector > MaxRegularSector || sector >= miniFat.Length || ++steps > miniFat.Length || offset + miniSectorSize > miniStream.Length)
            {
                throw new InvalidDataException("A chain of mini sectors in the compound file is damaged.");
            }
            var n = Math.Min(miniSectorSize, size - written);
            Array.Copy(miniStream, offset, output, written, n);
            written += n;
            sector = miniFat[sector];
        }
        return output;
    }

    private static void ReadSector(Stream stream, int sectorSize, uint sector, byte[] buffer)
    {
        stream.Position = ((long)sector + 1) * sectorSize;
        var n = stream.ReadAtLeast(buffer, sectorSize, throwOnEndOfStream: false);
        if (n < sectorSize)
        {
            Array.Clear(buffer, n, sectorSize - n); // The last sector may be cut short; the rest reads as zeros.
        }
    }

    internal static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));

    internal static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
}
