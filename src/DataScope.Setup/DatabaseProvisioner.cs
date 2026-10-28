using System.Security.Cryptography;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Setup;

internal sealed record ProvisionRequest(
    string AdminConnection, string Database, string OwnerUser, string AppUser,
    string? ExistingOwnerConnection, string? ExistingAppConnection, bool RotatePasswords);

internal sealed record ProvisionResult(
    string OwnerConnection, string AppConnection, bool CreatedDatabase, IReadOnlyList<string> CreatedRoles,
    IReadOnlyList<string> PasswordsSet, bool SplitExistingDatabase, IReadOnlyList<string> AppliedMigrations);

/// <summary>
/// Creates the Data Scope database and its two login roles (see <see cref="DatabaseRoles"/>), applies migrations as the
/// owner and grants the runtime login what the services need. The admin connection is used only here and is never
/// saved. Safe to run again: existing objects are kept and only pending migrations are applied. A database from an
/// older version, owned by the runtime login, is handed over to the owner login.
/// </summary>
internal static class DatabaseProvisioner
{
    public static async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct)
    {
        var (database, ownerUser, appUser) = (request.Database, request.OwnerUser, request.AppUser);
        if (!DatabaseRoles.IsValidName(database) || !DatabaseRoles.IsValidName(ownerUser) || !DatabaseRoles.IsValidName(appUser))
        {
            throw new SetupException("Database and user names must be lower-case letters, digits and underscores, starting with a letter.");
        }
        if (ownerUser == appUser)
        {
            throw new SetupException("The owner and app logins must be different roles.");
        }
        var admin = new NpgsqlConnectionStringBuilder(request.AdminConnection) { Pooling = false };

        var createdRoles = new List<string>();
        var passwordsSet = new List<string>();
        bool createdDb, split;
        string ownerPassword, appPassword;
        await using (var conn = await Open(admin.ConnectionString, ct))
        {
            var isSuper = await Scalar<bool>(conn, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user", ct);
            ownerPassword = await EnsureRoleAsync(conn, ownerUser, request.ExistingOwnerConnection, request.RotatePasswords, createdRoles, passwordsSet, ct);
            appPassword = await EnsureRoleAsync(conn, appUser, request.ExistingAppConnection, request.RotatePasswords, createdRoles, passwordsSet, ct);
            if (!isSuper)
            {
                // PostgreSQL 16 lets a CREATEROLE admin give objects to a role, or take them from one, only as a member of it.
                await Exec(conn, $"GRANT \"{ownerUser}\" TO CURRENT_USER", ct);
                await Exec(conn, $"GRANT \"{appUser}\" TO CURRENT_USER", ct);
            }

            var currentOwner = await Scalar<string?>(conn, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = $1", ct, database);
            createdDb = currentOwner is null;
            split = currentOwner == appUser;
            if (createdDb)
            {
                await Exec(conn, $"CREATE DATABASE \"{database}\" OWNER \"{ownerUser}\" ENCODING 'UTF8' TEMPLATE template0", ct);
            }
            else if (split)
            {
                // Installed by a version that used one login for everything.
                await Exec(conn, $"ALTER DATABASE \"{database}\" OWNER TO \"{ownerUser}\"", ct);
            }
            else if (currentOwner != ownerUser)
            {
                throw new SetupException($"Database {database} already exists and belongs to {currentOwner}, not {ownerUser}. Use another --database, or change its owner yourself.");
            }
            await Exec(conn, $"REVOKE ALL ON DATABASE \"{database}\" FROM PUBLIC", ct);
            await Exec(conn, $"GRANT CONNECT ON DATABASE \"{database}\" TO \"{appUser}\"", ct);
        }

        // Inside the database, still as admin: hand everything to the owner and close the public schema.
        await using (var conn = await Open(new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString, ct))
        {
            if (split)
            {
                await Exec(conn, $"REASSIGN OWNED BY \"{appUser}\" TO \"{ownerUser}\"", ct);
            }
            await Exec(conn, $"ALTER SCHEMA public OWNER TO \"{ownerUser}\"", ct);
            await Exec(conn, "REVOKE ALL ON SCHEMA public FROM PUBLIC", ct);
        }

        var ownerConnection = Connection(admin, database, ownerUser, ownerPassword);
        var appConnection = Connection(admin, database, appUser, appPassword);
        var applied = await MigrateAsync(ownerConnection, appUser, ct);
        return new ProvisionResult(ownerConnection, appConnection, createdDb, createdRoles, passwordsSet, split, applied);
    }

    /// <summary>Applies pending migrations as the owner, then refreshes the runtime login's grants. Returns the migrations applied.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(string ownerConnection, string? appUser, CancellationToken ct)
    {
        IReadOnlyList<string> pending;
        await using (var db = CreateContext(ownerConnection))
        {
            pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];
            if (pending.Count > 0)
            {
                await db.Database.MigrateAsync(ct);
            }
        }
        if (appUser is not null)
        {
            await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString);
            await conn.OpenAsync(ct);
            await DatabaseRoles.GrantRuntimeAsync(conn, appUser, ct);
        }
        return pending;
    }

    public static DataScopeDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DataScopeDbContext>();
        PersistenceSetup.Configure(options, connectionString);
        return new DataScopeDbContext(options.Options);
    }

    /// <summary>Creates the login if missing, or sets a new password when the saved one can't be reused. Returns the password.</summary>
    private static async Task<string> EnsureRoleAsync(
        NpgsqlConnection conn, string role, string? existingConnection, bool rotate, List<string> created, List<string> passwordsSet, CancellationToken ct)
    {
        var exists = await Scalar<bool>(conn, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)", ct, role);
        var existing = existingConnection is null ? null : new NpgsqlConnectionStringBuilder(existingConnection);
        var canReuse = exists && !rotate && existing is not null
            && string.Equals(existing.Username, role, StringComparison.Ordinal) && !string.IsNullOrEmpty(existing.Password);
        var password = canReuse ? existing!.Password! : NewPassword();
        if (!exists)
        {
            // The password is generated from a URL-safe alphabet, so it is safe inside a SQL literal (DDL takes no parameters).
            await Exec(conn, $"CREATE ROLE \"{role}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{password}'", ct);
            created.Add(role);
            return password;
        }
        if (await Scalar<bool>(conn, "SELECT rolsuper FROM pg_roles WHERE rolname = $1", ct, role))
        {
            throw new SetupException($"Role {role} is a superuser. Data Scope must not run as a superuser; choose another name.");
        }
        if (!canReuse)
        {
            await Exec(conn, $"ALTER ROLE \"{role}\" WITH LOGIN PASSWORD '{password}'", ct);
            passwordsSet.Add(role);
        }
        return password;
    }

    private static string Connection(NpgsqlConnectionStringBuilder admin, string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = admin.Host,
            Port = admin.Port,
            Database = database,
            Username = user,
            Password = password,
            SslMode = admin.SslMode,
        }.ConnectionString;

    private static string NewPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_');

    private static async Task<NpgsqlConnection> Open(string connectionString, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            await conn.DisposeAsync();
            throw new SetupException($"Could not connect to PostgreSQL as the admin user: {ex.Message}");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }
}
