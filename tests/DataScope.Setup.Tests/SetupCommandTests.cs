using Npgsql;

namespace DataScope.Setup.Tests;

/// <summary>The commands the installer runs, end to end. Not parallel: they set an environment variable.</summary>
[Trait("Category", "Database")]
[Collection(nameof(EnvironmentVariableTests))]
public sealed class SetupCommandTests(ScratchPostgres pg) : IClassFixture<ScratchPostgres>, IDisposable
{
    private const string AdminSid = "S-1-5-21-111-222-333-1105";
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-setup-").FullName;
    private string ConfigPath => Path.Combine(_dir, "datascope.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, null);
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SetupProgram.RunAsync([.. args, "--config", ConfigPath], output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task<(int Code, string Out, string Err)> RunDatabase(string db, string owner, string role, params string[] extra)
    {
        var admin = new NpgsqlConnectionStringBuilder(pg.AdminConnection);
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, admin.Password);
        return await Run(["database", "--host", admin.Host!, "--port", admin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--admin-user", admin.Username!, "--database", db, "--owner-user", owner, "--app-user", role, .. extra]);
    }

    private string OwnerConnection() => ConfigFile.Load(OwnerSettings.PathFor(ConfigPath)).GetSecret(OwnerSettings.Key)!;

    [Fact]
    public async Task Install_flow_database_configure_check_then_upgrade()
    {
        var (db, owner, role) = pg.NewNames();
        var created = await RunDatabase(db, owner, role);
        Assert.True(created.Code == 0, created.Err);
        Assert.Contains($"Created database {db}.", created.Out, StringComparison.Ordinal);
        // The password lives in the settings file only, never on screen.
        var password = new NpgsqlConnectionStringBuilder(ConfigFile.Load(ConfigPath).GetSecret("ConnectionStrings", "DataScope")).Password!;
        var ownerPassword = new NpgsqlConnectionStringBuilder(OwnerConnection()).Password!;
        Assert.DoesNotContain(password, created.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerPassword, created.Out, StringComparison.Ordinal);
        // The services' settings file never holds the owner's password.
        Assert.DoesNotContain(ownerPassword, File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Null(ConfigFile.Load(ConfigPath).GetString(OwnerSettings.Key));

        // Without an admin group the check fails: nobody could manage the product.
        var noAdmins = await Run("check");
        Assert.Equal(1, noAdmins.Code);
        Assert.Contains("[FAIL] Administrators", noAdmins.Out, StringComparison.Ordinal);

        Assert.Equal(0, (await Run("configure", "--role", $"PlatformAdmin={AdminSid}")).Code);
        var check = await Run("check");
        Assert.True(check.Code == 0, check.Out);
        Assert.Contains("[ OK ] Schema: Up to date", check.Out, StringComparison.Ordinal);
        Assert.Contains("[ OK ] Database logins", check.Out, StringComparison.Ordinal);
        Assert.Contains("[WARN] HTTPS", check.Out, StringComparison.Ordinal);

        // Upgrade path: migrate is a no-op, and running database again (with only the admin login) keeps everything.
        var migrate = await Run("migrate");
        Assert.Contains("already up to date", migrate.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("one database login", migrate.Out, StringComparison.Ordinal);
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, new NpgsqlConnectionStringBuilder(pg.AdminConnection).Password);
        var again = await Run("database", "--admin-user", new NpgsqlConnectionStringBuilder(pg.AdminConnection).Username!);
        Assert.True(again.Code == 0, again.Err);
        Assert.Contains("already exists; kept it", again.Out, StringComparison.Ordinal);
        Assert.Equal(password, new NpgsqlConnectionStringBuilder(ConfigFile.Load(ConfigPath).GetSecret("ConnectionStrings", "DataScope")).Password);
        Assert.Equal(ownerPassword, new NpgsqlConnectionStringBuilder(OwnerConnection()).Password);
        Assert.Equal([AdminSid], ConfigFile.Load(ConfigPath).GetStrings("Auth", "RoleGroupSids", "PlatformAdmin"));
    }

    [Fact]
    public async Task Check_flags_unapplied_migrations_development_sign_in_and_superusers()
    {
        var (db, owner, role) = pg.NewNames();
        Assert.Equal(0, (await RunDatabase(db, owner, role)).Code);
        await Run("configure", "--role", $"PlatformAdmin={AdminSid}");
        var config = ConfigFile.Load(ConfigPath);

        await using (var conn = new NpgsqlConnection(OwnerConnection()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM schema_migrations WHERE migration_id = (SELECT max(migration_id) FROM schema_migrations)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        config.Set("Development", "Auth", "Mode");
        config.Save();

        var check = await Run("check");
        Assert.Equal(1, check.Code);
        Assert.Contains("[FAIL] Schema: 1 migration(s) not applied", check.Out, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Sign-in", check.Out, StringComparison.Ordinal);

        // Pointing the settings at a superuser is refused.
        config.Set(pg.AdminConnection, "ConnectionStrings", "DataScope");
        config.Save();
        if (await IsSuperuser(pg.AdminConnection))
        {
            Assert.Contains("[FAIL] Database role", (await Run("check")).Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Database_without_a_password_and_no_console_explains_how_to_supply_it()
    {
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, null);
        if (!Console.IsInputRedirected)
        {
            return; // An interactive run would prompt instead.
        }
        var (code, _, err) = await Run("database", "--database", "unused_db", "--app-user", "unused_role");
        Assert.Equal(2, code);
        Assert.Contains(SetupProgram.AdminPasswordVariable, err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_on_a_one_login_server_says_how_to_split_it()
    {
        var (db, _, app) = pg.NewNames();
        await pg.ExecAsync($"CREATE ROLE \"{app}\" LOGIN PASSWORD 'legacy-test-only'");
        await pg.ExecAsync($"GRANT \"{app}\" TO CURRENT_USER");
        await pg.ExecAsync($"CREATE DATABASE \"{db}\" OWNER \"{app}\" TEMPLATE template0");
        var legacy = new NpgsqlConnectionStringBuilder(pg.AdminConnection) { Database = db, Username = app, Password = "legacy-test-only" }.ConnectionString;
        var config = ConfigFile.Load(ConfigPath);
        config.Set(legacy, "ConnectionStrings", "DataScope");
        config.Save();

        var (code, output, err) = await Run("migrate");
        Assert.True(code == 0, err);
        Assert.Contains("one database login for everything", output, StringComparison.Ordinal);
        Assert.Contains("[WARN] Database logins", (await Run("check")).Out, StringComparison.Ordinal);
    }

    private static async Task<bool> IsSuperuser(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", conn);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}

[CollectionDefinition(nameof(EnvironmentVariableTests), DisableParallelization = true)]
public sealed class EnvironmentVariableTests;
