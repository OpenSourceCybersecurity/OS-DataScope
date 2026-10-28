using System.Text.RegularExpressions;
using Npgsql;

namespace DataScope.Persistence;

/// <summary>
/// Data Scope uses two PostgreSQL logins. The owner owns the schema and is used only by setup (migrations, backup and
/// restore); its password is kept where only administrators can read it. The services use the runtime login, which can
/// read and write rows but cannot change the schema, and can only add to the audit log and the review
/// history, never change or delete them.
/// </summary>
public static partial class DatabaseRoles
{
    /// <summary>Tables the runtime login may only read and append to.</summary>
    public static readonly IReadOnlyList<string> AppendOnlyTables = ["app_audit_log", "review_events", "activity_gaps"];

    /// <summary>
    /// Evidence tables whose rows the runtime login may add, and later change only in the listed columns, never
    /// delete. Collected file events keep their raw content; only the share and file they belong to can be filled in (D98).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> EvidenceTables = new Dictionary<string, string[]>
    {
        ["file_audit_events"] = ["share_id", "relative_path", "object_kind"],
    };

    /// <summary>Owner-run functions the runtime login may call; each checks its own limits (D106).</summary>
    public static readonly IReadOnlyList<string> RuntimeFunctions =
        ["datascope_purge_activity(timestamp with time zone, integer)"];

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();

    public static bool IsValidName(string name) => Identifier().IsMatch(name);

    /// <summary>
    /// Gives the runtime login exactly what the services need on the current schema. Run as the owner after every
    /// migration, so new tables are covered. Safe to run again.
    /// </summary>
    public static async Task GrantRuntimeAsync(NpgsqlConnection owner, string runtimeRole, CancellationToken ct)
    {
        if (!IsValidName(runtimeRole))
        {
            throw new ArgumentException($"'{runtimeRole}' is not a valid role name.", nameof(runtimeRole));
        }
        var role = $"\"{runtimeRole}\"";
        string[] statements =
        [
            $"GRANT USAGE ON SCHEMA public TO {role}",
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role}",
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {role}",
            $"REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA public FROM {role}",
            .. AppendOnlyTables.Select(t => $"REVOKE UPDATE, DELETE ON \"{t}\" FROM {role}"),
            .. EvidenceTables.SelectMany(t => (string[])[
                $"REVOKE UPDATE, DELETE ON \"{t.Key}\" FROM {role}",
                $"GRANT UPDATE ({string.Join(", ", t.Value.Select(c => $"\"{c}\""))}) ON \"{t.Key}\" TO {role}"]),
            $"REVOKE INSERT, UPDATE, DELETE ON \"{PersistenceSetup.MigrationsTable}\" FROM {role}",
            .. RuntimeFunctions.Select(f => $"GRANT EXECUTE ON FUNCTION {f} TO {role}"),
        ];
        await using var tx = await owner.BeginTransactionAsync(ct);
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, owner, tx);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
