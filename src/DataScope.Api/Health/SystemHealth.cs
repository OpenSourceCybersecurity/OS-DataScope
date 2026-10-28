using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataScope.Api.Endpoints;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Configuration;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Health;

/// <summary>Ok, something to look at soon, something broken now, or not checked on this server.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    Ok,
    Warning,
    Problem,
    Unknown,
}

/// <param name="Area">Which part of Data Scope the check is about, for grouping.</param>
/// <param name="Detail">What was found, in one or two plain sentences.</param>
/// <param name="Fix">What to do about it, when there is something to do.</param>
/// <param name="Route">The screen that shows more, as the web UI's route name.</param>
/// <param name="Items">One line per server, source or drive, worst first.</param>
public sealed record HealthCheck(string Id, string Area, string Title, HealthStatus Status, string Detail, string? Fix = null, string? Route = null,
    IReadOnlyList<HealthItem>? Items = null);

public sealed record HealthItem(string Name, HealthStatus Status, string Detail);

public sealed record HealthReport(HealthStatus Overall, DateTimeOffset CheckedAt, IReadOnlyList<HealthCheck> Checks);

/// <summary>
/// System health (07-DEPLOYMENT: the daily health view): whether the services run, scans finish, the directory sync is
/// current, emails go out, backups were taken, disks have room and certificates are in date. Read-only:
/// everything comes from what the services already record, plus free disk space and certificate dates on this server.
/// Nothing is contacted.
/// </summary>
public sealed class SystemHealth(DataScopeDbContext db, DirectoryStore directory, IConfiguration config, TimeProvider clock)
{
    /// <summary>The scanner checks in every minute; three missed means it has stopped or can't reach the database.</summary>
    public static readonly TimeSpan ServiceLate = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan BackupWarning = TimeSpan.FromHours(26);
    public static readonly TimeSpan BackupProblem = TimeSpan.FromDays(3);
    public const long DiskWarningBytes = 10L << 30;
    public const long DiskProblemBytes = 2L << 30;

    /// <summary>Drives to check, as paths on them. Tests replace it; on a server it is the install and data folders.</summary>
    public Func<IEnumerable<string>> Folders { get; init; } = DefaultFolders;

    public async Task<HealthReport> CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<HealthCheck> checks =
        [
            await ServicesAsync(now, ct),
            await ScansAsync(now, ct),
            await DirectoryAsync(now, ct),
            await EmailAsync(now, ct),
            await BackupsAsync(now, ct),
            Disks(),
            await DatabaseAsync(ct),
            Certificates(now),
            await ActivityAsync(now, ct),
        ];
        var overall = checks.Any(c => c.Status == HealthStatus.Problem) ? HealthStatus.Problem
            : checks.Any(c => c.Status == HealthStatus.Warning) ? HealthStatus.Warning : HealthStatus.Ok;
        return new HealthReport(overall, now, checks);
    }

    private async Task<HealthCheck> ServicesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var beats = await db.ServiceHeartbeats.AsNoTracking().ToDictionaryAsync(b => b.Service, ct);
        var items = new List<HealthItem>();
        foreach (var (service, name, fix) in new[]
                 {
                     (ServiceHeartbeatWriter.Scanner, "Scanner service", "Start \"Data Scope Scanner\" in Services on the Data Scope server, then check Event Viewer, Applications and Services Logs, Data Scope, for why it stopped."),
                     (ServiceHeartbeatWriter.Web, "Web service", ""),
                 })
        {
            if (!beats.TryGetValue(service, out var beat))
            {
                items.Add(new HealthItem(name, HealthStatus.Problem, "Has never checked in. " + fix));
                continue;
            }
            var late = now - beat.SeenAt > ServiceLate;
            items.Add(new HealthItem(name, late ? HealthStatus.Problem : HealthStatus.Ok, late
                ? $"Last checked in {Ago(now, beat.SeenAt)} from {beat.Machine}. {fix}".TrimEnd()
                : $"Running on {beat.Machine}, version {beat.Version}, since {Day(beat.StartedAt)}."));
        }
        var worst = Worst(items);
        return new HealthCheck("services", "Services", "Data Scope services", worst,
            worst == HealthStatus.Ok ? "Both services are running." : "A Data Scope service is not running, so scans, emails or collection have stopped.",
            worst == HealthStatus.Ok ? null : "Start the service named below. It picks up where it left off.", Items: items);
    }

    private async Task<HealthCheck> ScansAsync(DateTimeOffset now, CancellationToken ct)
    {
        var servers = await db.ManagedServers.AsNoTracking().Where(s => s.State != EnrollmentState.Retired).OrderBy(s => s.Hostname).ToListAsync(ct);
        if (servers.Count == 0)
        {
            return new HealthCheck("scans", "Scanning", "Scans", HealthStatus.Warning, "No file server has been added yet.", "Add one on Servers & scans.", "setup");
        }
        var ids = servers.Select(s => s.Id).ToList();
        var jobs = await db.ScanJobs.AsNoTracking().Where(j => ids.Contains(j.ServerId))
            .GroupBy(j => j.ServerId)
            .Select(g => new
            {
                ServerId = g.Key,
                LastCompleted = g.Where(j => j.State == ScanJobState.Completed).Max(j => j.FinishedAt),
                Latest = g.OrderByDescending(j => j.CreatedAt).Select(j => new { j.State, j.LastError, j.FinishedAt }).First(),
            })
            .ToDictionaryAsync(x => x.ServerId, ct);
        var items = new List<HealthItem>();
        foreach (var server in servers)
        {
            var name = server.Hostname;
            jobs.TryGetValue(server.Id, out var job);
            var expected = server.ScanFrequency switch
            {
                ScanFrequency.Hourly => TimeSpan.FromHours(Math.Max(1, server.ScanEveryHours ?? 1) * 3),
                ScanFrequency.Daily => TimeSpan.FromDays(2),
                ScanFrequency.Weekly => TimeSpan.FromDays(9),
                _ => (TimeSpan?)null,
            };
            if (server.State == EnrollmentState.Error || job?.Latest.State == ScanJobState.Failed)
            {
                var error = server.LastError ?? job?.Latest.LastError ?? "The last scan failed.";
                items.Add(new HealthItem(name, HealthStatus.Problem, Shorten(error)));
            }
            else if (job?.LastCompleted is null)
            {
                items.Add(new HealthItem(name, job is null ? HealthStatus.Warning : HealthStatus.Ok,
                    job is null ? "Never scanned. Start a scan on Servers & scans." : "The first scan hasn't finished yet."));
            }
            else if (expected is { } every && now - job.LastCompleted.Value > every)
            {
                items.Add(new HealthItem(name, HealthStatus.Warning,
                    $"Last finished scan {Ago(now, job.LastCompleted.Value)}, although it is scheduled {Frequency(server.ScanFrequency)}. Check the scan window and whether scans are paused."));
            }
            else
            {
                items.Add(new HealthItem(name, HealthStatus.Ok, $"Last finished scan {Ago(now, job.LastCompleted.Value)}{(expected is null ? ", scanned by hand" : "")}."));
            }
        }
        var worst = Worst(items);
        var bad = items.Count(i => i.Status != HealthStatus.Ok);
        return new HealthCheck("scans", "Scanning", "Scans", worst,
            bad == 0 ? $"All {items.Count} sources scanned as scheduled." : $"{bad} of {items.Count} sources need a look.",
            worst == HealthStatus.Ok ? null : "Open Servers & scans for the error and to start a scan again.", "setup", Sorted(items));
    }

    private async Task<HealthCheck> DirectoryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var hours = config.GetValue("Directory:SyncHours", 24);
        var latest = await directory.LatestCompletedAsync(ct);
        var failed = await db.DirectorySnapshots.AsNoTracking().Where(s => s.State == DirectorySyncState.Failed && (latest == null || s.RequestedAt > latest.RequestedAt))
            .OrderByDescending(s => s.RequestedAt).FirstOrDefaultAsync(ct);
        if (latest?.CompletedAt is not { } done)
        {
            return new HealthCheck("directory", "Active Directory", "Directory sync", HealthStatus.Warning,
                failed is null ? "Users and groups have not been read from Active Directory yet." : "Every Active Directory sync so far has failed.",
                "Run a sync from Who has access. Access results stay unknown until one finishes.", "access");
        }
        var stale = now - done > TimeSpan.FromHours(Math.Max(48, hours * 2));
        var status = failed is not null ? (stale ? HealthStatus.Problem : HealthStatus.Warning) : stale ? HealthStatus.Warning : HealthStatus.Ok;
        var detail = $"Last sync finished {Ago(now, done)}" + (latest.Domain is { Length: > 0 } d ? $" ({d})." : ".");
        if (failed is not null)
        {
            detail += " A later sync failed, so who is in which group may be out of date.";
        }
        else if (stale)
        {
            detail += $" It should run every {hours} hours.";
        }
        return new HealthCheck("directory", "Active Directory", "Directory sync", status, detail,
            status == HealthStatus.Ok ? null : "Check the scanner can reach a domain controller, then run a sync from Who has access.", "access");
    }

    private async Task<HealthCheck> EmailAsync(DateTimeOffset now, CancellationToken ct)
    {
        var mailServer = !string.IsNullOrWhiteSpace(config["Notifications:Smtp:Host"]) && !string.IsNullOrWhiteSpace(config["Notifications:Smtp:From"]);
        var week = now.AddDays(-7);
        var failed = await db.Outbox.AsNoTracking().Where(m => m.State == OutboxState.Failed && m.CreatedAt >= week)
            .OrderByDescending(m => m.CreatedAt).Select(m => new { m.LastError }).ToListAsync(ct);
        var stuck = await db.Outbox.AsNoTracking().CountAsync(m => m.State == OutboxState.Pending && m.CreatedAt < now.AddHours(-1), ct);
        if (!mailServer)
        {
            return new HealthCheck("email", "Email", "Email", HealthStatus.Warning,
                "No mail server is set, so review emails, reminders and alerts are not sent.", "Set one on System settings, Email, and send a test email.", "system-settings");
        }
        if (failed.Count > 0)
        {
            return new HealthCheck("email", "Email", "Email", HealthStatus.Warning,
                $"{failed.Count} email{(failed.Count == 1 ? "" : "s")} failed in the last 7 days. Latest error: {Shorten(failed[0].LastError ?? "unknown")}",
                "Check the mail server on System settings, Email, and send a test email.", "system-settings");
        }
        if (stuck > 0)
        {
            return new HealthCheck("email", "Email", "Email", HealthStatus.Warning,
                $"{stuck} email{(stuck == 1 ? " has" : "s have")} waited more than an hour to be sent.",
                "The scanner service sends emails: check it is running and can reach the mail server.", "system-settings");
        }
        return new HealthCheck("email", "Email", "Email", HealthStatus.Ok, "Emails go out through your mail server; none failed in the last 7 days.");
    }

    private async Task<HealthCheck> BackupsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The setup tool records every backup it takes in the audit log (M5b); the backup folder itself is admin-only.
        var last = await db.AppAuditLog.AsNoTracking().Where(a => a.Action == "backup.create").OrderByDescending(a => a.Sequence)
            .Select(a => new { a.At, a.Target }).FirstOrDefaultAsync(ct);
        if (last is null)
        {
            return new HealthCheck("backups", "Backups", "Database backups", HealthStatus.Problem, "No backup of the Data Scope database has been recorded.",
                "Check the \"Data Scope nightly backup\" task in Task Scheduler, or run DataScope.Setup backup as an administrator.");
        }
        var age = now - last.At;
        var status = age > BackupProblem ? HealthStatus.Problem : age > BackupWarning ? HealthStatus.Warning : HealthStatus.Ok;
        return new HealthCheck("backups", "Backups", "Database backups", status, $"Last backup {Ago(now, last.At)} ({last.Target}).",
            status == HealthStatus.Ok ? null : "Check the \"Data Scope nightly backup\" task in Task Scheduler and its last result. Keep the backup key away from this server.");
    }

    private HealthCheck Disks()
    {
        var items = new List<HealthItem>();
        foreach (var root in Folders().Select(f => Path.GetPathRoot(Path.GetFullPath(f))).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var drive = new DriveInfo(root!);
                var free = drive.AvailableFreeSpace;
                var share = drive.TotalSize > 0 ? (double)free / drive.TotalSize : 1;
                var status = free < DiskProblemBytes || share < 0.05 ? HealthStatus.Problem : free < DiskWarningBytes || share < 0.10 ? HealthStatus.Warning : HealthStatus.Ok;
                items.Add(new HealthItem(drive.Name, status, $"{Gb(free)} free of {Gb(drive.TotalSize)} ({share:P0})."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                items.Add(new HealthItem(root!, HealthStatus.Unknown, "Free space could not be read."));
            }
        }
        var worst = Worst(items);
        return new HealthCheck("disk", "Server", "Disk space", worst,
            worst == HealthStatus.Ok ? "The drives Data Scope uses on this server have room." : "A drive Data Scope uses on this server is running out of room.",
            worst is HealthStatus.Warning or HealthStatus.Problem ? "Free space on that drive, or move backups elsewhere with -BackupFolder. Scans and backups fail when it is full." : null,
            Items: Sorted(items));
    }

    private async Task<HealthCheck> DatabaseAsync(CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_database_size(current_database()), current_setting('server_version')";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var size = reader.GetInt64(0);
            var version = reader.GetString(1);
            return new HealthCheck("database", "Server", "Database", HealthStatus.Ok, $"PostgreSQL {version.Split(' ')[0]}, {Gb(size)} used.");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private HealthCheck Certificates(DateTimeOffset now)
    {
        var items = new List<HealthItem>();
        if (config["Https:CertificateThumbprint"] is { Length: > 0 } thumbprint && OperatingSystem.IsWindows())
        {
            items.Add(Expiry("HTTPS certificate (the address people open)", MachineCertificate(thumbprint), now,
                "Install a renewed certificate and run Install-DataScope.ps1 with -CertificateThumbprint, or let the installer make a new one."));
        }
        if (items.Count == 0)
        {
            return new HealthCheck("certificates", "Server", "Certificates", HealthStatus.Unknown, "No certificate to check on this server.");
        }
        var worst = Worst(items);
        return new HealthCheck("certificates", "Server", "Certificates", worst,
            worst == HealthStatus.Ok ? "Certificates are in date." : "A certificate expires soon or has expired.", Items: Sorted(items));
    }

    private static HealthItem Expiry(string name, DateTimeOffset? expires, DateTimeOffset now, string fix) => expires switch
    {
        null => new HealthItem(name, HealthStatus.Unknown, "Its end date could not be read."),
        { } e when e <= now => new HealthItem(name, HealthStatus.Problem, $"Expired on {Day(e)}. {fix}"),
        { } e when e - now < TimeSpan.FromDays(30) => new HealthItem(name, HealthStatus.Warning, $"Expires on {Day(e)}. {fix}"),
        { } e => new HealthItem(name, HealthStatus.Ok, $"Valid until {Day(e)}."),
    };

    private async Task<HealthCheck> ActivityAsync(DateTimeOffset now, CancellationToken ct)
    {
        var sources = await (from a in db.ActivitySources.AsNoTracking()
                             join s in db.ManagedServers.AsNoTracking() on a.ServerId equals s.Id
                             where a.Enabled && s.State != EnrollmentState.Retired
                             orderby s.Hostname
                             select new { s.Hostname, a.State, a.LastError, a.LastReadAt }).ToListAsync(ct);
        if (sources.Count == 0)
        {
            return new HealthCheck("activity", "File activity", "File activity collection", HealthStatus.Unknown,
                "File activity is not collected from any server. That is optional.", Route: "activity");
        }
        var items = sources.Select(a => a.State == ActivitySourceState.Error
                ? new HealthItem(a.Hostname, HealthStatus.Warning, Shorten(a.LastError ?? "The last read failed."))
                : a.LastReadAt is not { } read
                    ? new HealthItem(a.Hostname, HealthStatus.Ok, "Waiting for the first read.")
                    : now - read > TimeSpan.FromHours(2)
                        ? new HealthItem(a.Hostname, HealthStatus.Warning, $"Last read {Ago(now, read)}. Events may be overwritten before they are read.")
                        : new HealthItem(a.Hostname, HealthStatus.Ok, $"Last read {Ago(now, read)}."))
            .ToList();
        var worst = Worst(items);
        return new HealthCheck("activity", "File activity", "File activity collection", worst,
            worst == HealthStatus.Ok ? $"Security logs are being read from {items.Count} server{(items.Count == 1 ? "" : "s")}." : "Security logs are not being read from every server.",
            worst == HealthStatus.Ok ? null : "Open File activity for each server's log health and the error.", "activity", Sorted(items));
    }

    private static IEnumerable<string> DefaultFolders()
    {
        yield return AppContext.BaseDirectory;
        if (DataScopeConfigFile.ResolvePath() is { } path)
        {
            yield return path;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DateTimeOffset? MachineCertificate(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, ServerCertificate.Normalize(thumbprint), validOnly: false);
            try
            {
                return found.Count == 0 ? null : new DateTimeOffset(found[0].NotAfter);
            }
            finally
            {
                foreach (var c in found)
                {
                    c.Dispose();
                }
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static HealthStatus Worst(IEnumerable<HealthItem> items)
    {
        var list = items.ToList();
        return list.Any(i => i.Status == HealthStatus.Problem) ? HealthStatus.Problem
            : list.Any(i => i.Status == HealthStatus.Warning) ? HealthStatus.Warning
            : list.Count > 0 && list.All(i => i.Status == HealthStatus.Unknown) ? HealthStatus.Unknown : HealthStatus.Ok;
    }

    private static List<HealthItem> Sorted(List<HealthItem> items) =>
        [.. items.OrderBy(i => i.Status switch { HealthStatus.Problem => 0, HealthStatus.Warning => 1, HealthStatus.Unknown => 2, _ => 3 })];

    private static string Frequency(ScanFrequency f) => f switch
    {
        ScanFrequency.Hourly => "every few hours",
        ScanFrequency.Daily => "every day",
        ScanFrequency.Weekly => "every week",
        _ => "by hand",
    };

    internal static string Ago(DateTimeOffset now, DateTimeOffset at)
    {
        var span = now - at;
        return span < TimeSpan.FromMinutes(2) ? "just now"
            : span < TimeSpan.FromHours(2) ? $"{(int)span.TotalMinutes} minutes ago"
            : span < TimeSpan.FromDays(2) ? $"{(int)span.TotalHours} hours ago"
            : $"{(int)span.TotalDays} days ago, on {Day(at)}";
    }

    private static string Day(DateTimeOffset at) => at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Gb(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes / (double)(1L << 20):0} MB";

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..297] + "...";
}

/// <summary>Runs this service's heartbeat (System health).</summary>
public sealed class HeartbeatService(ServiceHeartbeatWriter writer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => writer.RunAsync(stoppingToken);
}

internal static class HealthEndpoints
{
    public static void MapHealth(this RouteGroupBuilder api) =>
        api.MapGet("/system-health", async (SystemHealth health, CancellationToken ct) => Results.Ok(await health.CheckAsync(ct)))
            .RequireAuthorization(Policies.ReadAuditLog);
}
