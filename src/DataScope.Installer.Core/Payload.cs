using System;
using System.IO;
using System.Text;

namespace DataScope.Installer;

/// <summary>
/// DataScope-Setup-&lt;version&gt;.exe is the setup program with the package zip appended:
/// [program][zip][zip length, 8 bytes little-endian]["DSSETUP1"]. Code signing adds its certificate table after that
/// (Authenticode signs the appended zip too), sometimes after up to 7 zero bytes of padding.
/// scripts/make-setup.mjs and Sign-DataScopePackage.ps1 write the same layout.
/// </summary>
public static class Payload
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSSETUP1");

    /// <summary>Where the appended zip starts and how long it is, or null when this program carries none.</summary>
    public static (long Offset, long Length)? Find(Stream file)
    {
        var end = CertificateTableOffset(file) ?? file.Length;
        if (end > file.Length)
        {
            return null;
        }
        const int trailer = 16;
        for (var padding = 0; padding < 8; padding++)
        {
            var at = end - padding - trailer;
            if (at < 0)
            {
                break;
            }
            var bytes = ReadAt(file, at, trailer + padding);
            if (!Same(bytes, 8, Magic) || !AllZero(bytes, trailer))
            {
                continue;
            }
            var length = BitConverter.ToInt64(bytes, 0);
            var offset = at - length;
            return length > 0 && offset > 0 ? (offset, length) : null;
        }
        return null;
    }

    /// <summary>Writes the setup program: the program, then the zip and the trailer.</summary>
    public static void Append(Stream program, Stream zip, Stream output)
    {
        program.CopyTo(output);
        var length = zip.Length;
        zip.CopyTo(output);
        output.Write(BitConverter.GetBytes(length), 0, 8);
        output.Write(Magic, 0, Magic.Length);
    }

    /// <summary>The file offset of the Authenticode certificate table in a PE file, or null when it has none.</summary>
    internal static long? CertificateTableOffset(Stream file)
    {
        if (file.Length < 0x40)
        {
            return null;
        }
        var dos = ReadAt(file, 0, 0x40);
        if (dos[0] != 'M' || dos[1] != 'Z')
        {
            return null;
        }
        long pe = BitConverter.ToInt32(dos, 0x3C);
        if (pe <= 0 || pe + 24 + 2 > file.Length || !Same(ReadAt(file, pe, 4), 0, [(byte)'P', (byte)'E', 0, 0]))
        {
            return null;
        }
        var optional = pe + 24;
        var magic = BitConverter.ToUInt16(ReadAt(file, optional, 2), 0);
        // The data directory count and the directories follow the optional header's fixed part, which is longer in PE32+.
        var (countAt, directoriesAt) = magic switch
        {
            0x10b => (optional + 92, optional + 96),
            0x20b => (optional + 108, optional + 112),
            _ => (-1L, -1L),
        };
        if (countAt < 0 || directoriesAt + (5 * 8) > file.Length || BitConverter.ToUInt32(ReadAt(file, countAt, 4), 0) < 5)
        {
            return null;
        }
        // Directory 4 is the certificate table; unlike the others its address is a file offset.
        var security = ReadAt(file, directoriesAt + (4 * 8), 8);
        var address = BitConverter.ToUInt32(security, 0);
        var size = BitConverter.ToUInt32(security, 4);
        return address > 0 && size > 0 ? address : null;
    }

    private static byte[] ReadAt(Stream file, long offset, int count)
    {
        var buffer = new byte[count];
        file.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = file.Read(buffer, read, count - read);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }
            read += n;
        }
        return buffer;
    }

    private static bool Same(byte[] bytes, int start, byte[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (bytes[start + i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool AllZero(byte[] bytes, int start)
    {
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }
        return true;
    }
}
