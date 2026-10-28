using Npgsql;

namespace DataScope.Setup.Tests;

/// <summary>
/// Hands out unique database and role names on the test server and drops them
/// afterwards. Needs DATASCOPE_TEST_CONNECTION for a user that may create roles
/// and databases (see docs/DEVELOPMENT.md).
/// </summary>
public sealed class ScratchPostgres : IAsyncLifetime
{
    private readonly List<string> _databases = [];
    private readonly List<string> _roles = [];

    public string AdminConnection { get; } = Environment.GetEnvironmentVariable("DATASCOPE_TEST_CONNECTION")
        ?? throw new InvalidOperationException("DATASCOPE_TEST_CONNECTION is not set. Database tests need PostgreSQL; see docs/DEVELOPMENT.md.");

    public (string Database, string Owner, string App) NewNames()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var names = ($"ds_setup_{suffix}", $"ds_owner_{suffix}", $"ds_app_{suffix}");
        _databases.Add(names.Item1);
        _roles.Add(names.Item2);
        _roles.Add(names.Item3);
        return names;
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        foreach (var db in _databases)
        {
            // A connection closed a moment ago can still be exiting; a CREATEROLE (not superuser) login may not end
            // another role's session itself, so wait for it rather than leave the database behind.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await ExecAsync($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
                    break;
                }
                catch (PostgresException ex) when (attempt < 20 && ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
                {
                    await Task.Delay(250);
                }
            }
        }
        foreach (var role in _roles)
        {
            await ExecAsync($"DROP ROLE IF EXISTS \"{role}\"");
        }
    }
}
