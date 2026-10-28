using System.Security.Cryptography;

namespace DataScope.Setup.Tests;

public sealed class BackupFormatTests
{
    private static readonly BackupHeader Header = new("Data Scope", DateTimeOffset.Parse("2026-10-10T02:30:00Z", System.Globalization.CultureInfo.InvariantCulture), "datascope", "20261010010708_ScanHistoryAndRetention", "", "test");

    private static async Task<byte[]> Encrypt(byte[] plain, byte[] key)
    {
        using var output = new MemoryStream();
        await BackupFormat.EncryptAsync(new MemoryStream(plain), output, key, Header with { KeyId = BackupFormat.KeyId(key) }, CancellationToken.None);
        return output.ToArray();
    }

    private static async Task<byte[]> Decrypt(byte[] file, byte[] key)
    {
        using var plain = new MemoryStream();
        await BackupFormat.DecryptAsync(new MemoryStream(file), plain, key, CancellationToken.None);
        return plain.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(BackupFormat.ChunkSize)]
    [InlineData(BackupFormat.ChunkSize * 2 + 12345)]
    public async Task Round_trips_any_size_and_hides_the_contents(int size)
    {
        var key = BackupFormat.NewKey();
        var plain = new byte[size];
        // Recognisable, compressible content: if any of it were stored in the clear, it would show up below.
        for (var i = 0; i < size; i++)
        {
            plain[i] = (byte)"fs-backup-marker "[i % 17];
        }
        var file = await Encrypt(plain, key);

        Assert.Equal(plain, await Decrypt(file, key));
        if (size >= 17)
        {
            Assert.Equal(-1, file.AsSpan().IndexOf("fs-backup-marker"u8));
        }
        var header = await BackupFormat.ReadHeaderAsync(new MemoryStream(file), CancellationToken.None);
        Assert.Equal(("datascope", BackupFormat.KeyId(key)), (header.Database, header.KeyId));
    }

    [Fact]
    public async Task A_changed_byte_anywhere_is_refused()
    {
        var key = BackupFormat.NewKey();
        var file = await Encrypt(RandomNumberGenerator.GetBytes(BackupFormat.ChunkSize + 100), key);
        foreach (var position in new[] { 60, 200, file.Length / 2, file.Length - 1 })
        {
            var copy = (byte[])file.Clone();
            copy[position] ^= 0x01;
            var ex = await Assert.ThrowsAsync<SetupException>(() => Decrypt(copy, key));
            Assert.True(ex.Message.Contains("damaged", StringComparison.Ordinal) || ex.Message.Contains("different backup key", StringComparison.Ordinal), ex.Message);
        }
    }

    [Fact]
    public async Task A_cut_off_or_extended_file_is_refused()
    {
        var key = BackupFormat.NewKey();
        var file = await Encrypt(RandomNumberGenerator.GetBytes(BackupFormat.ChunkSize * 2 + 10), key);
        // Cut exactly after the first full chunk: every remaining chunk verifies, but the last one never came.
        var firstChunkEnd = file.Length - (1 + 4 + BackupFormat.ChunkSize + 16) - (1 + 4 + 10 + 16);
        foreach (var cut in new[] { file[..firstChunkEnd], file[..^1], file[..100] })
        {
            Assert.Contains("damaged or incomplete", (await Assert.ThrowsAsync<SetupException>(() => Decrypt(cut, key))).Message, StringComparison.Ordinal);
        }
        Assert.Contains("damaged", (await Assert.ThrowsAsync<SetupException>(() => Decrypt([.. file, 0], key))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wrong_key_is_named_and_a_foreign_file_is_recognised()
    {
        var file = await Encrypt([1, 2, 3], BackupFormat.NewKey());
        var other = BackupFormat.NewKey();
        var ex = await Assert.ThrowsAsync<SetupException>(() => Decrypt(file, other));
        Assert.Contains("different backup key", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BackupFormat.KeyId(other), ex.Message, StringComparison.Ordinal);

        var notOurs = await Assert.ThrowsAsync<SetupException>(() => Decrypt("PGDMP plain pg_dump output"u8.ToArray(), other));
        Assert.Contains("not a Data Scope backup", notOurs.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_are_written_for_people_and_read_back_forgivingly()
    {
        var key = BackupFormat.NewKey();
        var text = BackupFormat.FormatKey(key);
        Assert.Matches("^([0-9a-f]{8}-){7}[0-9a-f]{8}$", text);
        Assert.Equal(key, BackupFormat.ParseKey(text));
        Assert.Equal(key, BackupFormat.ParseKey(" " + text.ToUpperInvariant().Replace('-', ' ') + "\n"));
        Assert.Throws<SetupException>(() => BackupFormat.ParseKey("too-short"));
        Assert.Equal(16, BackupFormat.KeyId(key).Length);
    }

    [Fact]
    public async Task Verify_reads_the_whole_file_even_when_pg_restore_stops_early()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The stand-in tool is a shell script. Windows CI verifies a real backup with PostgreSQL 17's pg_restore.
        }
        var dir = Directory.CreateTempSubdirectory("ds-pgtools-").FullName;
        try
        {
            // Like pg_restore --list: reads the start of the archive, then exits successfully without reading the rest.
            var tool = Path.Combine(dir, "pg_restore");
            File.WriteAllText(tool, "#!/bin/sh\nhead -c 100 > /dev/null\nexit 0\n");
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var key = BackupFormat.NewKey();
            var file = Path.Combine(dir, "test.dsbackup");
            File.WriteAllBytes(file, await Encrypt(new byte[BackupFormat.ChunkSize * 3 + 5], key));

            var header = await Backups.VerifyAsync(new PgTools(dir), file, key, CancellationToken.None);
            Assert.Equal("datascope", header.Database);

            // Damage near the end, well past what the tool read: still refused.
            var bytes = File.ReadAllBytes(file);
            bytes[^30] ^= 0xFF;
            File.WriteAllBytes(file, bytes);
            var ex = await Assert.ThrowsAsync<SetupException>(() => Backups.VerifyAsync(new PgTools(dir), file, key, CancellationToken.None));
            Assert.Contains("damaged or incomplete", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
