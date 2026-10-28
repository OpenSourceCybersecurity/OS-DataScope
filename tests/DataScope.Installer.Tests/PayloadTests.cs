using System.IO.Compression;
using System.Text;
using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class PayloadTests
{
    [Fact]
    public void Finds_the_zip_appended_to_an_unsigned_program()
    {
        var (setup, zip) = Build(Program());

        var found = Payload.Find(new MemoryStream(setup));

        Assert.NotNull(found);
        Assert.Equal(zip, setup.AsSpan((int)found.Value.Offset, (int)found.Value.Length).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void Finds_the_zip_in_front_of_a_code_signature_after_its_padding(int padding)
    {
        var (unsigned, zip) = Build(Program());
        // What signing does: pad to 8 bytes, add the certificate table at the end, point directory 4 at it.
        var signedLength = unsigned.Length + padding;
        var signed = new byte[signedLength + 64];
        unsigned.CopyTo(signed, 0);
        WriteSecurityDirectory(signed, (uint)signedLength, 64);

        var found = Payload.Find(new MemoryStream(signed));

        Assert.NotNull(found);
        Assert.Equal(zip, signed.AsSpan((int)found.Value.Offset, (int)found.Value.Length).ToArray());
    }

    [Fact]
    public void A_program_without_a_package_has_none()
    {
        Assert.Null(Payload.Find(new MemoryStream(Program())));
        Assert.Null(Payload.Find(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public void A_trailer_claiming_more_bytes_than_the_file_holds_is_ignored()
    {
        var program = Program();
        var file = program.Concat(BitConverter.GetBytes((long)program.Length + 1000)).Concat(Payload.Magic).ToArray();
        Assert.Null(Payload.Find(new MemoryStream(file)));
    }

    [Fact]
    public void The_found_zip_unpacks_into_the_package_folder()
    {
        var (setup, _) = Build(Program());
        var folder = Path.Combine(Path.GetTempPath(), "ds-setup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new MemoryStream(setup);
            var found = Payload.Find(stream)!.Value;
            var package = PackageExtractor.Extract(new SubStream(stream, found.Offset, found.Length), folder);

            Assert.Equal(Path.Combine(Path.GetFullPath(folder), "DataScope-1.0.0"), package);
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(package, "VERSION")));
            Assert.True(File.Exists(Path.Combine(package, "app", "api", "DataScope.Api.exe")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_zip_entry_that_climbs_out_of_the_folder_is_refused()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../evil.ps1").Open());
            writer.Write("Remove-Item C:\\ -Recurse");
        }
        zip.Position = 0;
        var folder = Path.Combine(Path.GetTempPath(), "ds-setup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidDataException>(() => PackageExtractor.Extract(zip, folder));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "evil.ps1")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_zip_without_the_install_script_is_refused()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("readme.txt");
        }
        zip.Position = 0;
        var folder = Path.Combine(Path.GetTempPath(), "ds-setup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidDataException>(() => PackageExtractor.Extract(zip, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static (byte[] Setup, byte[] Zip) Build(byte[] program)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
            Add("DataScope-1.0.0/Install-DataScope.ps1", "# installer");
            Add("DataScope-1.0.0/VERSION", "1.0.0");
            Add("DataScope-1.0.0/app/api/DataScope.Api.exe", "MZ");
        }
        var output = new MemoryStream();
        Payload.Append(new MemoryStream(program), new MemoryStream(zip.ToArray()), output);
        return (output.ToArray(), zip.ToArray());
    }

    /// <summary>The smallest PE32+ header the reader looks at: DOS header, PE signature, optional header with 16 directories.</summary>
    private static byte[] Program()
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x80);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(bytes, 0x80 + 24);
        BitConverter.GetBytes(16u).CopyTo(bytes, 0x80 + 24 + 108);
        return bytes;
    }

    private static void WriteSecurityDirectory(byte[] file, uint offset, uint size)
    {
        BitConverter.GetBytes(offset).CopyTo(file, 0x80 + 24 + 112 + 32);
        BitConverter.GetBytes(size).CopyTo(file, 0x80 + 24 + 112 + 36);
    }
}
