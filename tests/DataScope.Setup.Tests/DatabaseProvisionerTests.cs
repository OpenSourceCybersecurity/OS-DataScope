using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Setup.Tests;

[Trait("Category", "Database")]
public sealed class DatabaseProvisionerTests(ScratchPostgres pg) : IClassFixture<ScratchPostgres>
{
    private static async Task<T> Query<T>(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ProvisionRequest Request(string db, string owner, string app, string? ownerCs = null, string? appCs = null, bool rotate = false) =>
        new(pg.AdminConnection, db, owner, app, ownerCs, appCs, rotate);

    [Fact]
    public async Task Creates_an_owner_and_a_least_privilege_app_login()
    {
        var (db, owner, app) = pg.NewNames();
        var result = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None);

        Assert.True(result.CreatedDatabase);
        Assert.Equal([owner, app], result.CreatedRoles);
        Assert.False(result.SplitExistingDatabase);
        Assert.NotEmpty(result.AppliedMigrations);
        var appCs = new NpgsqlConnectionStringBuilder(result.AppConnection);
        Assert.Equal(app, appCs.Username);
        Assert.Equal(db, appCs.Database);
        Assert.True(appCs.Password!.Length >= 40);
        Assert.NotEqual(appCs.Password, new NpgsqlConnectionStringBuilder(result.OwnerConnection).Password);

        // Neither login has server-wide powers; the owner owns the database and schema.
        foreach (var cs in new[] { result.OwnerConnection, result.AppConnection })
        {
            Assert.False(await Query<bool>(cs, "SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = current_user"));
        }
        Assert.Equal(owner, await Query<string>(result.AppConnection, "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()"));
        Assert.Equal(owner, await Query<string>(result.AppConnection, "SELECT pg_get_userbyid(nspowner)::text FROM pg_namespace WHERE nspname = 'public'"));
        Assert.Equal(0L, await Query<long>(result.AppConnection, $"SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner <> '{owner}'"));
        // Other roles can't connect: PUBLIC lost CONNECT on the database.
        Assert.False(await Query<bool>(pg.AdminConnection, $"SELECT has_database_privilege('public', '{db}', 'CONNECT')"));
        await AssertAppLoginIsLimited(result.AppConnection);
    }

    /// <summary>What the services can and can't do with the app login.</summary>
    private static async Task AssertAppLoginIsLimited(string appConnection)
    {
        // Rows: read and write.
        await Exec(appConnection, "INSERT INTO managed_servers (id, hostname, state, created_at, updated_at, created_by_sid) VALUES (gen_random_uuid(), 'fs-roles', 'Enrolled', now(), now(), 'S-1-5-18')");
        Assert.Equal(1L, await Query<long>(appConnection, "SELECT count(*) FROM managed_servers WHERE hostname = 'fs-roles'"));
        await Exec(appConnection, "UPDATE managed_servers SET updated_at = now() WHERE hostname = 'fs-roles'");
        await Exec(appConnection, "DELETE FROM managed_servers WHERE hostname = 'fs-roles'");

        // Audit log: add and read, never change or delete, even with the trigger out of the way.
        await Exec(appConnection, """
            INSERT INTO app_audit_log (sequence, at, actor_sid, actor_name, action, target, details, previous_hash, hash)
            VALUES (1, now(), 'S-1-5-18', 'test', 'test', 'test', '{}', repeat('0', 64), repeat('0', 64))
            """);
        Assert.Equal(1L, await Query<long>(appConnection, "SELECT count(*) FROM app_audit_log"));
        await AssertDenied(appConnection, "UPDATE app_audit_log SET target = 'forged'");
        await AssertDenied(appConnection, "DELETE FROM app_audit_log");
        await AssertDenied(appConnection, "TRUNCATE app_audit_log");
        await AssertDenied(appConnection, "ALTER TABLE app_audit_log DISABLE TRIGGER ALL");
        foreach (var table in DatabaseRoles.AppendOnlyTables.Skip(1))
        {
            await AssertDenied(appConnection, $"DELETE FROM {table}");
        }

        // Schema: no changes, no new tables, no rewriting the migration history.
        await AssertDenied(appConnection, "CREATE TABLE sneaky (id int)");
        await AssertDenied(appConnection, "DROP TABLE files");
        await AssertDenied(appConnection, "ALTER TABLE files ADD COLUMN extra text");
        await AssertDenied(appConnection, "DELETE FROM schema_migrations");
    }

    private static async Task AssertDenied(string connectionString, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(connectionString, sql));
        Assert.True(ex.SqlState is PostgresErrorCodes.InsufficientPrivilege, $"{sql}: {ex.SqlState} {ex.MessageText}");
    }

    [Fact]
    public async Task Running_again_keeps_the_passwords_and_applies_nothing()
    {
        var (db, owner, app) = pg.NewNames();
        var first = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None);
        var second = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app, first.OwnerConnection, first.AppConnection), CancellationToken.None);

        Assert.Empty(second.CreatedRoles);
        Assert.Empty(second.PasswordsSet);
        Assert.False(second.CreatedDatabase);
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(first.AppConnection, second.AppConnection);
        Assert.Equal(first.OwnerConnection, second.OwnerConnection);
    }

    [Fact]
    public async Task Rotating_sets_new_passwords_and_the_old_ones_stop_working()
    {
        var (db, owner, app) = pg.NewNames();
        var first = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None);
        var rotated = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app, first.OwnerConnection, first.AppConnection, rotate: true), CancellationToken.None);

        Assert.Equal([owner, app], rotated.PasswordsSet);
        Assert.NotEqual(first.AppConnection, rotated.AppConnection);
        Assert.Equal(app, await Query<string>(rotated.AppConnection, "SELECT current_user::text"));
        NpgsqlConnection.ClearAllPools();
        await Assert.ThrowsAsync<PostgresException>(() => Query<string>(first.AppConnection, "SELECT 1::text"));
        await Assert.ThrowsAsync<PostgresException>(() => Query<string>(first.OwnerConnection, "SELECT 1::text"));
    }

    [Fact]
    public async Task Splits_a_database_installed_with_one_login()
    {
        // The layout earlier versions created: the app login owns the database and every table.
        var (db, owner, app) = pg.NewNames();
        await pg.ExecAsync($"CREATE ROLE \"{app}\" LOGIN PASSWORD 'legacy-test-only'");
        await pg.ExecAsync($"GRANT \"{app}\" TO CURRENT_USER");
        await pg.ExecAsync($"CREATE DATABASE \"{db}\" OWNER \"{app}\" TEMPLATE template0");
        var legacy = new NpgsqlConnectionStringBuilder(pg.AdminConnection) { Database = db, Username = app, Password = "legacy-test-only", Pooling = false }.ConnectionString;
        await DatabaseProvisioner.MigrateAsync(legacy, appUser: null, CancellationToken.None);
        await Exec(legacy, "INSERT INTO managed_servers (id, hostname, state, created_at, updated_at, created_by_sid) VALUES (gen_random_uuid(), 'fs-kept', 'Enrolled', now(), now(), 'S-1-5-18')");

        var result = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app, null, legacy), CancellationToken.None);

        Assert.True(result.SplitExistingDatabase);
        Assert.Equal([owner], result.CreatedRoles);
        Assert.Empty(result.AppliedMigrations);
        // The services keep their login and password.
        var kept = new NpgsqlConnectionStringBuilder(result.AppConnection);
        Assert.Equal((app, "legacy-test-only", db), (kept.Username, kept.Password, kept.Database));
        Assert.Equal(1L, await Query<long>(result.AppConnection, "SELECT count(*) FROM managed_servers WHERE hostname = 'fs-kept'"));
        Assert.Equal(0L, await Query<long>(result.OwnerConnection, $"SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner <> '{owner}'"));
        await AssertAppLoginIsLimited(result.AppConnection);
    }

    [Fact]
    public async Task Migrate_refreshes_the_app_logins_grants()
    {
        var (db, owner, app) = pg.NewNames();
        var result = await DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None);
        // As if a new migration added a table: the app can't use it until migrate grants it.
        await Exec(result.OwnerConnection, "CREATE TABLE added_later (id int)");
        await AssertDenied(result.AppConnection, "SELECT count(*) FROM added_later");

        await DatabaseProvisioner.MigrateAsync(result.OwnerConnection, app, CancellationToken.None);
        Assert.Equal(0L, await Query<long>(result.AppConnection, "SELECT count(*) FROM added_later"));
    }

    [Fact]
    public async Task Refuses_a_database_owned_by_someone_else()
    {
        var (db, owner, app) = pg.NewNames();
        await pg.ExecAsync($"CREATE DATABASE \"{db}\"");
        var ex = await Assert.ThrowsAsync<SetupException>(() => DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None));
        Assert.Contains("already exists and belongs to", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Data Scope", "ds_owner", "ds_app")]
    [InlineData("datascope", "ds_owner", "app\"; DROP ROLE postgres; --")]
    [InlineData("datascope", "ds_owner\"--", "ds_app")]
    [InlineData("", "ds_owner", "ds_app")]
    public async Task Refuses_unsafe_names(string db, string owner, string app)
    {
        var ex = await Assert.ThrowsAsync<SetupException>(() => DatabaseProvisioner.ProvisionAsync(Request(db, owner, app), CancellationToken.None));
        Assert.Contains("lower-case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_one_login_for_both()
    {
        var ex = await Assert.ThrowsAsync<SetupException>(() => DatabaseProvisioner.ProvisionAsync(Request("ds_same", "ds_one", "ds_one"), CancellationToken.None));
        Assert.Contains("must be different", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_admin_password_is_reported_plainly()
    {
        var bad = new NpgsqlConnectionStringBuilder(pg.AdminConnection) { Password = "definitely-wrong" }.ConnectionString;
        var (db, owner, app) = pg.NewNames();
        var ex = await Assert.ThrowsAsync<SetupException>(() =>
            DatabaseProvisioner.ProvisionAsync(new ProvisionRequest(bad, db, owner, app, null, null, false), CancellationToken.None));
        Assert.StartsWith("Could not connect to PostgreSQL as the admin user", ex.Message, StringComparison.Ordinal);
    }
}
