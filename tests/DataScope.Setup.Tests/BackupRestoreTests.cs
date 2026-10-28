using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Setup.Tests;

/// <summary>
/// Acceptance test A17: back up, change things, restore, and the services' view is exactly as it was, with the
/// limited login still limited and the audit chain intact. Uses the real pg_dump and pg_restore.
/// </summary>
[Trait("Category", "Database")]
[Collection(nameof(EnvironmentVariableTests))]
public sealed class BackupRestoreTests(ScratchPostgres pg) : IClassFixture<ScratchPostgres>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-backup-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config", "datascope.json");
    private string BackupFolder => Path.Combine(_dir, "backups");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, null);
        Environment.SetEnvironmentVariable(Backups.KeyVariable, null);
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SetupProgram.RunAsync([.. args, "--config", ConfigPath], output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<string> Install()
    {
        var (db, owner, app) = pg.NewNames();
        var admin = new NpgsqlConnectionStringBuilder(pg.AdminConnection);
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, admin.Password);
        var result = await Run("database", "--host", admin.Host!, "--port", $"{admin.Port}", "--admin-user", admin.Username!,
            "--database", db, "--owner-user", owner, "--app-user", app);
        Assert.True(result.Code == 0, result.Err);
        return ConfigFile.Load(ConfigPath).GetSecret("ConnectionStrings", "DataScope")!;
    }

    private string OwnerConnection() => ConfigFile.Load(OwnerSettings.PathFor(ConfigPath)).GetSecret(OwnerSettings.Key)!;

    private static async Task Exec(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> Servers(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT hostname FROM managed_servers ORDER BY hostname", conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    private static Task AddServer(string cs, string host) => Exec(cs,
        $"INSERT INTO managed_servers (id, hostname, state, created_at, updated_at, created_by_sid) VALUES (gen_random_uuid(), '{host}', 'Enrolled', now(), now(), 'S-1-5-18')");

    private static async Task Audit(string cs, string action)
    {
        await using var db = DatabaseProvisioner.CreateContext(cs);
        await new AuditLogWriter(db, TimeProvider.System).AppendAsync("S-1-5-21-1-2-3-500", "NW\\admin", action, "test");
    }

    private string[] RegularBackups() => [.. Directory.GetFiles(BackupFolder, "datascope-2*.dsbackup").Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Backup_change_restore_brings_everything_back()
    {
        var app = await Install();
        await AddServer(app, "fs-backup-marker");
        await Audit(app, "server.enroll");

        var backup = await Run("backup", "--keep", "2");
        Assert.True(backup.Code == 0, backup.Err);
        Assert.Contains("Created this server's backup key", backup.Out, StringComparison.Ordinal);
        var file = Assert.Single(RegularBackups());
        Assert.Equal(-1, File.ReadAllBytes(file).AsSpan().IndexOf("fs-backup-marker"u8));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(BackupFolder));
        }
        var key = await Run("backup-key");
        Assert.Matches("([0-9a-f]{8}-){7}[0-9a-f]{8}", key.Out);
        var verify = await Run("verify-backup", "--from", file);
        Assert.True(verify.Code == 0, verify.Err);
        Assert.Contains("complete and readable", verify.Out, StringComparison.Ordinal);

        // Life goes on after the backup.
        await Exec(app, "DELETE FROM managed_servers WHERE hostname = 'fs-backup-marker'");
        await AddServer(app, "fs-after-backup");
        await Audit(app, "server.retire");

        Assert.Equal(2, (await Run("restore", "--from", file)).Code);
        await using (var open = new NpgsqlConnection(app))
        {
            // A running service holds a connection: refused, nothing changed.
            await open.OpenAsync();
            var busy = await Run("restore", "--from", file, "--confirm");
            Assert.Equal(1, busy.Code);
            Assert.Contains("Stop the Data Scope services first", busy.Err, StringComparison.Ordinal);
        }
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(["fs-after-backup"], await Servers(app));

        var restore = await Run("restore", "--from", file, "--confirm");
        Assert.True(restore.Code == 0, restore.Err);
        Assert.Contains("The schema is current", restore.Out, StringComparison.Ordinal);
        // backup.create and server.retire came after the backup, then the safety backup's own entry.
        Assert.Contains("3 audit log entries written after the backup are no longer in the live log", restore.Out, StringComparison.Ordinal);
        Assert.Equal(["fs-backup-marker"], await Servers(app));
        Assert.Single(Directory.GetFiles(BackupFolder, "datascope-before-restore-*.dsbackup"));

        // The services' login works and is still limited; the audit chain holds and records the restore.
        await AddServer(app, "fs-after-restore");
        await using (var conn = new NpgsqlConnection(app))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE TABLE sneaky (id int)", conn);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync())).SqlState);
            await using var update = new NpgsqlCommand("UPDATE app_audit_log SET target = 'x'", conn);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var db = DatabaseProvisioner.CreateContext(app))
        {
            var verified = await new AuditLogWriter(db, TimeProvider.System).VerifyAsync(CancellationToken.None);
            Assert.True(verified.Valid);
            var actions = await db.AppAuditLog.OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync();
            // The live log is the backup's, plus the restore; what came after the backup is in the safety copy.
            Assert.Equal(["server.enroll", "database.restore"], actions);
            var details = await db.AppAuditLog.Where(a => a.Action == "database.restore").Select(a => a.Details).SingleAsync();
            Assert.Contains("\"AuditEntriesOnlyInSafetyBackup\":3", details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Keeps_the_newest_backups_only()
    {
        await Install();
        var upgrade = await Run("backup", "--before-upgrade");
        Assert.True(upgrade.Code == 0, upgrade.Err);
        for (var i = 0; i < 3; i++)
        {
            var result = await Run("backup", "--keep", "2");
            Assert.True(result.Code == 0, result.Err);
        }
        Assert.Equal(2, RegularBackups().Length);
        // The copy taken before an upgrade is not one of the nightly set and is kept.
        Assert.Single(Directory.GetFiles(BackupFolder, "datascope-before-upgrade-*.dsbackup"));
    }

    [Fact]
    public async Task A_new_server_restores_with_the_saved_key()
    {
        var app = await Install();
        await AddServer(app, "fs-old-server");
        Assert.Equal(0, (await Run("backup")).Code);
        var file = Assert.Single(RegularBackups());
        var keyText = (await Run("backup-key")).Out.Split('\n').Select(l => l.Trim()).Single(l => l.Length == 71);

        // The new server has its own settings: no backup key yet.
        var owner = ConfigFile.Load(OwnerSettings.PathFor(ConfigPath));
        owner.Set(null, Backups.KeyName);
        owner.Save();
        await Exec(app, "DELETE FROM managed_servers");

        Environment.SetEnvironmentVariable(Backups.KeyVariable, "0000000000000000000000000000000000000000000000000000000000000000");
        var wrong = await Run("restore", "--from", file, "--confirm");
        Assert.Equal(1, wrong.Code);
        Assert.Contains("not the key this backup was made with", wrong.Err, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(Backups.KeyVariable, keyText);
        var restore = await Run("restore", "--from", file, "--confirm");
        Assert.True(restore.Code == 0, restore.Err);
        Assert.Contains("now uses the one you supplied", restore.Out, StringComparison.Ordinal);
        Assert.Equal(["fs-old-server"], await Servers(app));
        Assert.Contains(keyText, (await Run("backup-key")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_is_refused_before_anything_changes()
    {
        var app = await Install();
        var owner = OwnerConnection();
        await AddServer(app, "fs-untouched");
        // What a later version's schema looks like to this one: a migration it has never heard of.
        await Exec(owner, $"INSERT INTO {PersistenceSetup.MigrationsTable} (migration_id, product_version) VALUES ('99991231000000_FromTheFuture', '99.0')");
        Assert.Equal(0, (await Run("backup")).Code);
        var file = Assert.Single(RegularBackups());
        await Exec(app, "DELETE FROM managed_servers");

        var restore = await Run("restore", "--from", file, "--confirm");
        Assert.Equal(1, restore.Code);
        Assert.Contains("made by a newer version of Data Scope", restore.Err, StringComparison.Ordinal);
        Assert.Empty(await Servers(app));
        Assert.Empty(Directory.GetFiles(BackupFolder, "datascope-before-restore-*"));
    }

    [Fact]
    public async Task A_damaged_backup_is_refused_before_anything_changes()
    {
        var app = await Install();
        await AddServer(app, "fs-untouched");
        Assert.Equal(0, (await Run("backup")).Code);
        var file = Assert.Single(RegularBackups());
        var bytes = File.ReadAllBytes(file);
        bytes[^20] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        await Exec(app, "DELETE FROM managed_servers");
        await AddServer(app, "fs-current");

        var restore = await Run("restore", "--from", file, "--confirm");
        Assert.Equal(1, restore.Code);
        Assert.Contains("damaged or incomplete", restore.Err, StringComparison.Ordinal);
        Assert.Equal(["fs-current"], await Servers(app));
        Assert.Empty(Directory.GetFiles(BackupFolder, "datascope-before-restore-*"));
        Assert.Equal(1, (await Run("verify-backup", "--from", file)).Code);
    }

    [Fact]
    public async Task Backup_explains_a_missing_owner_login_or_pg_dump()
    {
        var noOwner = await Run("backup");
        Assert.Equal(1, noOwner.Code);
        Assert.Contains("has no owner login", noOwner.Err, StringComparison.Ordinal);

        await Install();
        var noTools = await Run("backup", "--pg-bin", Path.Combine(_dir, "nowhere"));
        Assert.Equal(1, noTools.Code);
        Assert.Contains("pg_dump was not found", noTools.Err, StringComparison.Ordinal);
        Assert.Equal(2, (await Run("backup", "--out", Path.Combine(_dir, "x.dsbackup"), "--keep", "3")).Code);
    }

    [Fact]
    public async Task The_tools_folder_is_checked_when_saved_and_used_by_later_backups()
    {
        await Install();
        var wrong = await Run("configure", "--pg-bin", Path.Combine(_dir, "nowhere"));
        Assert.Equal(1, wrong.Code);
        Assert.Contains("pg_dump was not found", wrong.Err, StringComparison.Ordinal);
        Assert.Null(ConfigFile.Load(ConfigPath).GetString("Backup", "PgBinPath"));

        var real = PgTools.Locate(null, null).BinDirectory;
        Assert.Equal(0, (await Run("configure", "--pg-bin", real)).Code);
        Assert.Equal(real, ConfigFile.Load(ConfigPath).GetString("Backup", "PgBinPath"));
        var backup = await Run("backup");
        Assert.True(backup.Code == 0, backup.Err);
    }
}
