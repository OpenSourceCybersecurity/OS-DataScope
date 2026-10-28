using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Api.Tests;

/// <summary>
/// A throwaway PostgreSQL database per test class. Needs DATASCOPE_TEST_CONNECTION
/// pointing at a server where the user may create databases and roles (see docs/DEVELOPMENT.md).
/// The test user owns the schema, like the owner login on a server; the API, the worker and the
/// tests themselves connect as a runtime login with the same limited grants the services get.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public const string EnvVar = "DATASCOPE_TEST_CONNECTION";

    // Shared by every test database on the server; a fixed test-only password, not a credential.
    private const string RuntimeRole = "datascope_test_runtime";
    private const string RuntimePassword = "runtime-test-only";
    private static readonly SemaphoreSlim RoleLock = new(1, 1);

    private readonly string _admin;

    /// <summary>The runtime login: what the API and worker use.</summary>
    public string ConnectionString { get; }

    /// <summary>The schema owner: only for tests that must step outside the services' permissions.</summary>
    public string OwnerConnectionString { get; }

    private readonly string _name = $"datascope_test_{Guid.NewGuid():N}";

    public TestDatabase()
        : this(pooling: false)
    {
    }

    /// <summary>A database whose runtime login pools connections, as the services do; for timing measurements.</summary>
    public static TestDatabase Pooled() => new(pooling: true);

    private TestDatabase(bool pooling)
    {
        _admin = Environment.GetEnvironmentVariable(EnvVar)
            ?? throw new InvalidOperationException($"{EnvVar} is not set. Database tests need PostgreSQL; see docs/DEVELOPMENT.md.");
        OwnerConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _name, Pooling = false }.ConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = RuntimeRole, Password = RuntimePassword, Pooling = pooling }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await EnsureRuntimeRoleAsync();
        await using (var conn = new NpgsqlConnection(_admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var db = CreateOwnerContext())
        {
            await db.Database.MigrateAsync();
        }
        await using var owner = new NpgsqlConnection(OwnerConnectionString);
        await owner.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"REVOKE ALL ON SCHEMA public FROM PUBLIC; GRANT CONNECT ON DATABASE \"{_name}\" TO {RuntimeRole}", owner))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        await DatabaseRoles.GrantRuntimeAsync(owner, RuntimeRole, CancellationToken.None);
    }

    /// <summary>Creates the runtime login once per server. Test processes may race, so a duplicate is fine.</summary>
    private async Task EnsureRuntimeRoleAsync()
    {
        await RoleLock.WaitAsync();
        try
        {
            await using var conn = new NpgsqlConnection(_admin);
            await conn.OpenAsync();
            await using var exists = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RuntimeRole}')", conn);
            if (await exists.ExecuteScalarAsync() is true)
            {
                return;
            }
            try
            {
                await using var create = new NpgsqlCommand(
                    $"CREATE ROLE {RuntimeRole} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{RuntimePassword}'", conn);
                await create.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateObject or PostgresErrorCodes.UniqueViolation)
            {
                // Another test process created it first.
            }
        }
        finally
        {
            RoleLock.Release();
        }
    }

    /// <summary>A context on the runtime login, with the services' permissions.</summary>
    public DataScopeDbContext CreateContext() => Context(ConnectionString);

    /// <summary>A context on the owner login, which can change the schema and step around the audit log's protections.</summary>
    public DataScopeDbContext CreateOwnerContext() => Context(OwnerConnectionString);

    private static DataScopeDbContext Context(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DataScopeDbContext>();
        PersistenceSetup.Configure(options, connectionString);
        return new DataScopeDbContext(options.Options);
    }

    /// <summary>
    /// A database outage for tests (A16): refuses new connections and ends every open one, as when PostgreSQL stops
    /// or the network to it drops. Synchronous so a scan's directory source can call it in the middle of a listing.
    /// </summary>
    public void TakeOffline() => Admin($"ALTER DATABASE \"{_name}\" WITH ALLOW_CONNECTIONS false; " +
        $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_name}' AND pid <> pg_backend_pid()");

    /// <summary>Ends the outage started by <see cref="TakeOffline"/>.</summary>
    public void BringOnline()
    {
        Admin($"ALTER DATABASE \"{_name}\" WITH ALLOW_CONNECTIONS true");
        NpgsqlConnection.ClearAllPools();
    }

    private void Admin(string sql)
    {
        using var conn = new NpgsqlConnection(_admin);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
