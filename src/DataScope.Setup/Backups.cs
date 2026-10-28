using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Setup;

internal sealed record BackupResult(string Path, long Bytes, string Sha256, IReadOnlyList<string> Deleted);

internal sealed record RestoreResult(BackupHeader Header, string SafetyBackup, IReadOnlyList<string> AppliedMigrations, long AuditEntriesOnlyInSafetyBackup);

/// <summary>
/// Encrypted backups of the Data Scope database, and restoring one. Backups hold file and folder names, AD names and the
/// audit log, never file contents; they are encrypted with this installation's backup key, which is kept with the owner
/// login and shown by "DataScope.Setup backup-key" so it can be stored somewhere safe for restoring on another server.
/// </summary>
internal static class Backups
{
    public static readonly string[] KeyName = ["Backup", "Key"];
    public const string FilePrefix = "datascope-";
    public const string KeyVariable = "DATASCOPE_BACKUP_KEY";

    /// <summary>The "backups" folder next to the settings folder: %ProgramData%\DataScope\backups on a server.</summary>
    public static string DefaultFolder(string configPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(configPath))!)!, "backups");

    /// <summary>The installation's backup key, created and saved (encrypted) the first time.</summary>
    public static (byte[] Key, bool Created) EnsureKey(ConfigFile owner)
    {
        if (owner.GetSecret(KeyName) is { } saved)
        {
            return (BackupFormat.ParseKey(saved), false);
        }
        var key = BackupFormat.NewKey();
        owner.SetSecret(BackupFormat.FormatKey(key), KeyName);
        OwnerSettings.Save(owner);
        return (key, true);
    }

    public static async Task<BackupResult> CreateAsync(
        PgTools tools, string ownerConnection, byte[] key, string target, bool targetIsFolder, int? keep, DateTimeOffset now, string kind, CancellationToken ct)
    {
        var folder = targetIsFolder ? target : Path.GetDirectoryName(Path.GetFullPath(target))!;
        AdminOnlyFolder.Ensure(folder);
        var path = targetIsFolder
            ? Path.Combine(folder, $"{FilePrefix}{kind}{now.UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}{BackupFormat.Extension}")
            : Path.GetFullPath(target);
        var cs = new NpgsqlConnectionStringBuilder(ownerConnection);
        var header = new BackupHeader("Data Scope", now, cs.Database!, await LatestMigrationAsync(ownerConnection, ct), BackupFormat.KeyId(key), Version);

        var partial = path + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await tools.DumpAsync(ownerConnection, dump => BackupFormat.EncryptAsync(dump, file, key, header, ct), ct);
            }
            File.Move(partial, path, overwrite: false);
        }
        finally
        {
            File.Delete(partial);
        }

        string sha;
        await using (var check = File.OpenRead(path))
        {
            sha = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
        }
        var deleted = new List<string>();
        if (keep is int n)
        {
            // Only regular backups count and are pruned; the copies taken before a restore are kept until removed by hand.
            foreach (var old in Directory.EnumerateFiles(folder, $"{FilePrefix}2*{BackupFormat.Extension}").Order(StringComparer.Ordinal).SkipLast(n))
            {
                File.Delete(old);
                deleted.Add(Path.GetFileName(old));
            }
        }
        var size = new FileInfo(path).Length;
        await AuditAsync(ownerConnection, "backup.create", Path.GetFileName(path), new { File = path, Bytes = size, Sha256 = sha, header.KeyId, header.LatestMigration }, ct);
        return new BackupResult(path, size, sha, deleted);
    }

    /// <summary>Reads the whole backup, checking every chunk and that pg_restore can read the archive. Changes nothing.</summary>
    public static async Task<BackupHeader> VerifyAsync(PgTools tools, string path, byte[] key, CancellationToken ct)
    {
        BackupHeader? header = null;
        await using var file = File.OpenRead(path);
        // pg_restore --list may stop reading once it has the archive's table of contents. Every chunk is still
        // decrypted and authenticated to the end, so a file damaged past that point is caught too.
        await tools.ListAsync(async input => header = await BackupFormat.DecryptAsync(file, new UntilClosedStream(input), key, ct), ct);
        return header ?? throw new SetupException("The backup could not be read to the end. Nothing was changed.");
    }

    /// <summary>Writes to the inner stream until its reader goes away, then discards the rest.</summary>
    private sealed class UntilClosedStream(Stream inner) : Stream
    {
        private bool _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_closed)
            {
                return;
            }
            try
            {
                await inner.WriteAsync(buffer, cancellationToken);
            }
            catch (IOException)
            {
                _closed = true;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Replaces the database's contents with the backup's: verifies the file first, refuses while anything else is
    /// connected, keeps a backup of the current contents, then restores in one transaction and applies any migrations
    /// the backup predates.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(
        PgTools tools, string ownerConnection, string appUser, string path, byte[] key, byte[] safetyKey, string safetyFolder, DateTimeOffset now, CancellationToken ct)
    {
        var header = await VerifyAsync(tools, path, key, ct);
        // A backup from a newer version has tables this version doesn't know; restoring it here would leave the
        // services running against a schema they can't use. That version's own setup restores it.
        await using (var db = DatabaseProvisioner.CreateContext(ownerConnection))
        {
            if (header.LatestMigration != "none" && !db.Database.GetMigrations().Contains(header.LatestMigration))
            {
                throw new SetupException($"This backup was made by a newer version of Data Scope ({header.ProductVersion}, schema {header.LatestMigration}). " +
                    "Restore it with that version's DataScope.Setup. Nothing was changed.");
            }
        }
        // Idle pooled connections from setup itself would count as "other"; close them so only real users remain.
        NpgsqlConnection.ClearAllPools();
        var others = await OtherConnectionsAsync(ownerConnection, ct);
        if (others > 0)
        {
            throw new SetupException($"{others} other connection(s) to the database are open. Stop the Data Scope services first (Stop-Service DataScopeScanner, DataScopeApi).");
        }
        var safety = await CreateAsync(tools, ownerConnection, safetyKey, safetyFolder, targetIsFolder: true, keep: null, now, "before-restore-", ct);
        // Audit entries written after the backup was taken disappear from the live log with everything else. They are
        // kept in the safety backup, and the restore's own entry says how many there were and where to find them.
        var auditBefore = await ScalarAsync<long>(ownerConnection, "SELECT coalesce(max(sequence), 0) FROM app_audit_log", ct);

        await using (var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public; REVOKE ALL ON SCHEMA public FROM PUBLIC;", conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        try
        {
            await using var file = File.OpenRead(path);
            await tools.RestoreAsync(ownerConnection, async input => await BackupFormat.DecryptAsync(file, input, key, ct), ct);
        }
        catch (SetupException ex)
        {
            throw new SetupException($"{ex.Message} The database is now empty. Restore the copy taken just before: DataScope.Setup restore --from \"{safety.Path}\" --confirm");
        }
        var applied = await DatabaseProvisioner.MigrateAsync(ownerConnection, appUser, ct);
        var auditAfter = await ScalarAsync<long>(ownerConnection, "SELECT coalesce(max(sequence), 0) FROM app_audit_log", ct);
        var discarded = Math.Max(0, auditBefore - auditAfter);
        await AuditAsync(ownerConnection, "database.restore", Path.GetFileName(path),
            new
            {
                File = path,
                BackupCreatedAt = header.CreatedAt,
                header.LatestMigration,
                MigrationsApplied = applied,
                SafetyBackup = safety.Path,
                AuditEntriesOnlyInSafetyBackup = discarded,
                LastAuditEntryBeforeRestore = auditBefore,
            }, ct);
        return new RestoreResult(header, safety.Path, applied, discarded);
    }

    private static string Version =>
        typeof(Backups).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static async Task<string> LatestMigrationAsync(string connectionString, CancellationToken ct) =>
        await ScalarAsync<string?>(connectionString, $"SELECT max(migration_id) FROM {PersistenceSetup.MigrationsTable}", ct) ?? "none";

    /// <summary>
    /// Connections to the database other than this one. The server process of a connection that was just closed (such
    /// as setup's own, above) stays listed for a moment, so a count above zero is checked again for up to two seconds;
    /// a service's connection stays open and is still counted. One connection asks every time, so it never sees itself.
    /// </summary>
    private static async Task<long> OtherConnectionsAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()", conn);
        var others = (long)(await cmd.ExecuteScalarAsync(ct))!;
        for (var attempt = 0; others > 0 && attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            others = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return others;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>Records the operation in the audit log, as the Windows account running setup.</summary>
    private static async Task AuditAsync(string ownerConnection, string action, string target, object details, CancellationToken ct)
    {
        var (sid, name) = OperatingSystem.IsWindows() ? WindowsAccount() : ("setup", Environment.UserName);
        await using var db = DatabaseProvisioner.CreateContext(ownerConnection);
        await new AuditLogWriter(db, TimeProvider.System).AppendAsync(sid, name, action, target, details, ct);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Sid, string Name) WindowsAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return (identity.User?.Value ?? "unknown", identity.Name);
    }
}
