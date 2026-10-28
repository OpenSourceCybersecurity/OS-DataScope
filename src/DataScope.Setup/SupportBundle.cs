using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataScope.Persistence;
using DataScope.Persistence.Configuration;
using Npgsql;

namespace DataScope.Setup;

/// <summary>
/// A zip of diagnostics for support, written locally and never sent anywhere. It holds versions, the configuration
/// check, settings with every password and key removed, and database counts by state; no share, folder, file, user or
/// group names, no matched text and no audit entries. Error messages are filtered for paths, accounts, addresses and
/// long numbers. The person running it reads the files before deciding to send them.
/// </summary>
internal static partial class SupportBundle
{
    public const string Removed = "(removed)";
    private const int MaxEvents = 500;

    // Read by people in a text editor, never put in a web page: keep <path> and accents readable rather than \u-escaped.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<string> CreateAsync(ConfigFile config, ConfigFile owner, Func<string, System.Security.Cryptography.X509Certificates.X509Certificate2>? loadCertificate,
        string target, bool includeEventLog, DateTimeOffset now, CancellationToken ct)
    {
        var path = Directory.Exists(target)
            ? Path.Combine(target, $"datascope-support-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip")
            : target;
        var temp = path + ".partial";
        try
        {
            await using (var file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, "README.txt", Readme(includeEventLog));
                Add(zip, "system.json", JsonSerializer.Serialize(SystemInfo(now), Json));

                var check = new StringBuilder();
                foreach (var r in await Checks.RunAsync(config, owner, loadCertificate, now, ct))
                {
                    check.AppendLine(CultureInfo.InvariantCulture, $"[{r.Level}] {r.Name}: {Redact(r.Detail)}");
                }
                Add(zip, "check.txt", check.ToString());

                Add(zip, "settings.json", config.Exists ? RedactSettings(await File.ReadAllTextAsync(config.Path, ct)) : "(no settings file)");
                Add(zip, "database.json", await DatabaseStatsAsync(config, ct));
                if (includeEventLog)
                {
                    Add(zip, "eventlog.txt", OperatingSystem.IsWindows() ? EventLogText(now) : "(the Windows event log exists only on Windows)");
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return path;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Readme(bool includeEventLog) =>
        $"""
        Data Scope support bundle
        =========================

        This file was written on this server and has not been sent anywhere. Read the files
        below before you send it to anyone; delete anything you are not comfortable sharing.

        system.json     Data Scope, Windows and .NET versions, processor count, time zone.
        check.txt       The output of 'DataScope.Setup check'.
        settings.json   datascope.json with every password, key and secret replaced by "{Removed}",
                        and database connections reduced to server, port, database and login name.
        database.json   Counts by state (servers, shares, scans, files, gaps, reviews, email), the
                        schema version, table sizes, recent scan and directory sync timings, and
                        whether the audit log chain verifies. No names of shares, folders, files,
                        users or groups, no matched text and no audit log entries.
        {(includeEventLog ? "eventlog.txt    The last 7 days of Data Scope's Windows event log entries.\n" : "")}
        Error messages in check.txt, database.json{(includeEventLog ? " and eventlog.txt" : "")} are filtered: a path and the rest of its
        line, DOMAIN\name accounts, email addresses and long numbers are replaced with <path>,
        <account>, <email> and <number>. Filtering can miss things, which is why you should read them first.
        """;

    private static object SystemInfo(DateTimeOffset now) => new
    {
        Product = "Data Scope",
        Version = typeof(SupportBundle).Assembly.GetName().Version?.ToString(),
        Os = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.OSArchitecture.ToString(),
        Runtime = RuntimeInformation.FrameworkDescription,
        Processors = Environment.ProcessorCount,
        TimeZone = TimeZoneInfo.Local.Id,
        CreatedAt = now,
    };

    /// <summary>Settings with every secret replaced and connection strings reduced to where they point.</summary>
    public static string RedactSettings(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return "(datascope.json is not valid JSON, so it was left out)";
        }
        RedactNode(root, []);
        return root?.ToJsonString(Json) ?? "{}";
    }

    private static void RedactNode(JsonNode? node, List<string> path)
    {
        if (node is not JsonObject obj)
        {
            return;
        }
        foreach (var (key, value) in obj.ToList())
        {
            path.Add(key);
            if (value is JsonObject)
            {
                RedactNode(value, path);
            }
            else if (path.Count == 2 && string.Equals(path[0], "ConnectionStrings", StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = ConnectionSummary(value?.GetValue<string>());
            }
            else if (IsSecretKey(key) || SettingSecrets.Keys.Any(k => k.SequenceEqual(path, StringComparer.OrdinalIgnoreCase)))
            {
                obj[key] = Removed;
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    private static bool IsSecretKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.EndsWith("key", StringComparison.OrdinalIgnoreCase);

    private static string ConnectionSummary(string? value)
    {
        if (value is null || SettingSecrets.IsProtected(value))
        {
            return value is null ? "" : "(encrypted; " + Removed + ")";
        }
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(value);
            return $"Host={cs.Host};Port={cs.Port};Database={cs.Database};Username={cs.Username};Password={Removed}";
        }
        catch (ArgumentException)
        {
            return Removed;
        }
    }

    /// <summary>Replaces paths, DOMAIN\name accounts, email addresses and long numbers in a message.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        text = Path_().Replace(text, "<path>");
        text = Email().Replace(text, "<email>");
        text = Account().Replace(text, "<account>");
        text = Ssn().Replace(text, "<number>");
        text = LongNumber().Replace(text, "<number>");
        return text;
    }

    // Paths can contain spaces, so a path and the rest of its line (up to a quote) are replaced: it is not possible to
    // tell where a file name ends.
    [GeneratedRegex(@"(?:\\\\|\b[A-Za-z]:[\\/])[^""'<>|\r\n]*")]
    private static partial Regex Path_();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Za-z0-9][A-Za-z0-9._-]*\\[A-Za-z0-9._$-]+")]
    private static partial Regex Account();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b(?:\d[ -]?){8,}\d\b")]
    private static partial Regex LongNumber();

    private static async Task<string> DatabaseStatsAsync(ConfigFile config, CancellationToken ct)
    {
        string? cs;
        try
        {
            cs = config.GetSecret("ConnectionStrings", "DataScope");
        }
        catch (SetupException ex)
        {
            return JsonSerializer.Serialize(new { Error = Redact(ex.Message) }, Json);
        }
        if (cs is null)
        {
            return JsonSerializer.Serialize(new { Error = "No database connection is configured." }, Json);
        }
        var stats = new JsonObject();
        try
        {
            await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false, Timeout = 15 }.ConnectionString);
            await conn.OpenAsync(ct);
            stats["Server"] = conn.PostgreSqlVersion.ToString();
            stats["Migrations"] = await ListAsync(conn, $"SELECT migration_id FROM {PersistenceSetup.MigrationsTable} ORDER BY migration_id", ct);
            stats["Servers"] = await CountsAsync(conn, "SELECT state, count(*) FROM managed_servers GROUP BY state", ct);
            stats["Shares"] = await CountsAsync(conn, "SELECT kind || ' / ' || state, count(*) FROM shares GROUP BY 1", ct);
            stats["ScanJobs"] = await CountsAsync(conn, "SELECT state, count(*) FROM scan_jobs GROUP BY state", ct);
            stats["Files"] = await CountsAsync(conn, "SELECT coalesce(override_sensitivity, sensitivity) || ' / ' || content_state, count(*) FROM files WHERE removed_at IS NULL GROUP BY 1", ct);
            stats["ScanGaps"] = await CountsAsync(conn, "SELECT kind, count(*) FROM scan_gaps GROUP BY kind", ct);
            stats["Reviews"] = await CountsAsync(conn, "SELECT state, count(*) FROM access_reviews GROUP BY state", ct);
            stats["Email"] = await CountsAsync(conn, "SELECT state, count(*) FROM notification_outbox GROUP BY state", ct);
            stats["RecentScans"] = await RowsAsync(conn,
                "SELECT state, attempts, created_at, started_at, finished_at, items_done, last_error FROM scan_jobs ORDER BY created_at DESC LIMIT 20", ct);
            stats["RecentDirectorySyncs"] = await RowsAsync(conn,
                "SELECT state, attempts, requested_at, started_at, completed_at, users, groups, memberships, last_error FROM directory_snapshots ORDER BY requested_at DESC LIMIT 10", ct);
            stats["TableSizes"] = await CountsAsync(conn,
                "SELECT relname, pg_total_relation_size(relid) FROM pg_catalog.pg_statio_user_tables", ct);
            stats["AuditEntries"] = await ScalarAsync(conn, "SELECT count(*) FROM app_audit_log", ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            stats["Error"] = Redact(ex.Message);
        }
        try
        {
            await using var db = DatabaseProvisioner.CreateContext(cs);
            var verify = await new AuditLogWriter(db, TimeProvider.System).VerifyAsync(ct);
            stats["AuditChainValid"] = verify.Valid;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            stats["AuditChainValid"] = Redact(ex.Message);
        }
        return stats.ToJsonString(Json);
    }

    private static async Task<JsonObject> CountsAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var result = new JsonObject();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.IsDBNull(0) ? "(none)" : reader.GetString(0)] = reader.GetInt64(1);
        }
        return result;
    }

    private static async Task<JsonArray> ListAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var result = new JsonArray();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>Rows as objects; text columns are filtered, since error messages can name paths and accounts.</summary>
    private static async Task<JsonArray> RowsAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var result = new JsonArray();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    string s => Redact(s),
                    DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToString("o", CultureInfo.InvariantCulture),
                    long n => n,
                    int n => n,
                    var v => Convert.ToString(v, CultureInfo.InvariantCulture),
                };
            }
            result.Add(row);
        }
        return result;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [SupportedOSPlatform("windows")]
    private static string EventLogText(DateTimeOffset now)
    {
        var text = new StringBuilder();
        var since = now.AddDays(-7).UtcDateTime;
        using var log = new EventLog("Application");
        var entries = log.Entries.Cast<EventLogEntry>()
            .Where(e => e.Source.StartsWith("Data Scope", StringComparison.Ordinal) && e.TimeGenerated.ToUniversalTime() >= since)
            .OrderByDescending(e => e.TimeGenerated)
            .Take(MaxEvents);
        foreach (var e in entries)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{e.TimeGenerated.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z {e.EntryType} {e.Source} {e.InstanceId}");
            text.AppendLine(Redact(e.Message));
            text.AppendLine();
        }
        return text.Length == 0 ? "(no Data Scope entries in the last 7 days)" : text.ToString();
    }
}
