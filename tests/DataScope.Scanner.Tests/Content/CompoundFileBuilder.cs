using System.Buffers.Binary;
using System.Text;

namespace DataScope.Scanner.Tests.Content;

/// <summary>A stream or a storage to put in a compound file built by <see cref="CompoundFileBuilder"/>.</summary>
public abstract record CfbNode(string Name);

public sealed record CfbData(string Name, byte[] Data) : CfbNode(Name);

public sealed record CfbStorage(string Name, params CfbNode[] Children) : CfbNode(Name);

/// <summary>
/// Writes OLE compound files ([MS-CFB], version 3, 512-byte sectors) for the tests: Outlook messages and other files no
/// free tool saves, and damaged files. Streams under 4,096 bytes go in the mini stream, as Office writes them.
/// </summary>
public static class CompoundFileBuilder
{
    private const uint Free = 0xFFFF_FFFF;
    private const uint EndOfChain = 0xFFFF_FFFE;
    private const uint FatSector = 0xFFFF_FFFD;
    private const int Sector = 512;
    private const int Mini = 64;
    private const int Cutoff = 4096;

    private sealed record Entry(string Name, byte Type, CfbNode Node)
    {
        public uint Left { get; set; } = Free;
        public uint Right { get; set; } = Free;
        public uint Child { get; set; } = Free;
        public uint Start { get; set; } = EndOfChain;
        public long Size { get; set; }
    }

    public static byte[] Build(params CfbNode[] rootChildren)
    {
        var entries = new List<Entry> { new("Root Entry", 5, new CfbStorage("Root Entry", rootChildren)) };
        Add(entries, 0, rootChildren);

        var sectors = new List<byte[]>();
        var fat = new List<uint>();
        uint Allocate(byte[] data)
        {
            if (data.Length == 0)
            {
                return EndOfChain;
            }
            var first = (uint)sectors.Count;
            for (var at = 0; at < data.Length; at += Sector)
            {
                var sector = new byte[Sector];
                Array.Copy(data, at, sector, 0, Math.Min(Sector, data.Length - at));
                sectors.Add(sector);
                fat.Add(at + Sector < data.Length ? (uint)sectors.Count : EndOfChain);
            }
            return first;
        }

        // Small streams in the mini stream, chained by the mini FAT; large ones in sectors of their own.
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        foreach (var e in entries.Where(e => e.Node is CfbData))
        {
            var data = ((CfbData)e.Node).Data;
            e.Size = data.Length;
            if (data.Length >= Cutoff)
            {
                e.Start = Allocate(data);
                continue;
            }
            if (data.Length == 0)
            {
                continue;
            }
            e.Start = (uint)miniFat.Count;
            var count = (data.Length + Mini - 1) / Mini;
            for (var i = 0; i < count; i++)
            {
                miniFat.Add(i + 1 < count ? (uint)miniFat.Count + 1 : EndOfChain);
            }
            mini.Write(data);
            mini.Write(new byte[(count * Mini) - data.Length]);
        }
        entries[0].Start = Allocate(mini.ToArray());
        entries[0].Size = mini.Length;
        var miniFatStart = Allocate(miniFat.SelectMany(BitConverter.GetBytes).ToArray());

        var directory = new byte[((entries.Count + 3) / 4) * Sector];
        for (var i = 0; i < entries.Count; i++)
        {
            WriteEntry(directory.AsSpan(i * 128, 128), entries[i]);
        }
        for (var i = entries.Count; i < directory.Length / 128; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan((i * 128) + 0x44), Free);
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan((i * 128) + 0x48), Free);
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan((i * 128) + 0x4C), Free);
        }
        var directoryStart = Allocate(directory);

        // The FAT goes last, and covers its own sectors.
        var fatSectors = 1;
        while ((sectors.Count + fatSectors) > fatSectors * (Sector / 4))
        {
            fatSectors++;
        }
        var fatStart = (uint)sectors.Count;
        for (var i = 0; i < fatSectors; i++)
        {
            fat.Add(FatSector);
        }
        while (fat.Count < fatSectors * (Sector / 4))
        {
            fat.Add(Free);
        }
        for (var i = 0; i < fatSectors; i++)
        {
            sectors.Add(fat.Skip(i * (Sector / 4)).Take(Sector / 4).SelectMany(BitConverter.GetBytes).ToArray());
        }

        var header = new byte[Sector];
        byte[] magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), Cutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), (uint)((miniFat.Count * 4) + Sector - 1) / Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), EndOfChain);
        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + (i * 4)), i < fatSectors ? fatStart + (uint)i : Free);
        }
        return [.. header, .. sectors.SelectMany(s => s)];
    }

    /// <summary>Adds a storage's children after it; each storage points at its first child, and siblings chain to the right.</summary>
    private static void Add(List<Entry> entries, int parent, CfbNode[] children)
    {
        var previous = -1;
        foreach (var child in children)
        {
            var id = entries.Count;
            entries.Add(new Entry(child.Name, child is CfbStorage ? (byte)1 : (byte)2, child));
            if (previous < 0)
            {
                entries[parent].Child = (uint)id;
            }
            else
            {
                entries[previous].Right = (uint)id;
            }
            previous = id;
            if (child is CfbStorage storage)
            {
                Add(entries, id, storage.Children);
            }
        }
    }

    private static void WriteEntry(Span<byte> span, Entry e)
    {
        var name = Encoding.Unicode.GetBytes(e.Name);
        name.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x40..], (ushort)(name.Length + 2));
        span[0x42] = e.Type;
        span[0x43] = 1; // Black.
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x44..], e.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x48..], e.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x4C..], e.Child);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x74..], e.Start);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x78..], (ulong)e.Size);
    }
}
