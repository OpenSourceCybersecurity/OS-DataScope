using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Api.Cleanup;
using DataScope.Api.Compliance;
using DataScope.Api.Progress;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>What a report covers: everything, one file server, or one share.</summary>
public sealed record ReportScope(Guid? ServerId, Guid? ShareId, string Description);

/// <summary>Everything a report needs to run: the database, the access engine, the caller, the scope and the clock.</summary>
internal sealed class ReportContext(DataScopeDbContext db, AccessService access, Actor actor, ReportScope scope, DateTimeOffset now, int rowLimit, int days = ReportContext.DefaultDays)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 3660;

    public DataScopeDbContext Db { get; } = db;
    public AccessService Access { get; } = access;
    public Actor Actor { get; } = actor;
    public ReportScope Scope { get; } = scope;
    public DateTimeOffset Now { get; } = now;
    public int RowLimit { get; } = rowLimit;

    /// <summary>For reports over a period (<see cref="ReportInfo.Period"/>): how many days back from now.</summary>
    public int Days { get; } = days;

    /// <summary>Stale and duplicate data, for the reports about them (D119).</summary>
    public DataCleanup? Cleanup { get; init; }

    /// <summary>Measures today for the progress report when nothing has yet (D165).</summary>
    public ProgressRecorder? Progress { get; init; }

    /// <summary>The turned-on compliance standard a report about one standard covers (<see cref="ReportInfo.Standard"/>).</summary>
    public EffectiveStandard? Standard { get; init; }

    /// <summary>Shares in scope that still exist.</summary>
    public IQueryable<Share> Shares =>
        Db.Shares.AsNoTracking().Where(s => s.State != ShareState.Removed
            && (Scope.ShareId == null || s.Id == Scope.ShareId)
            && (Scope.ServerId == null || s.ServerId == Scope.ServerId));

    /// <summary>Current files in scope.</summary>
    public IQueryable<InventoryFile> Files =>
        Db.Files.AsNoTracking().Where(f => f.RemovedAt == null && Shares.Any(s => s.Id == f.ShareId));

    /// <summary>
    /// Resolves the scope from query values. Returns null with an error when a server or share does not exist, or when
    /// the share is not on the given server.
    /// </summary>
    public static async Task<(ReportScope? Scope, string? Error)> ResolveAsync(DataScopeDbContext db, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        if (shareId is { } sh)
        {
            var share = await db.Shares.AsNoTracking().Where(s => s.Id == sh).Select(s => new { s.ServerId, s.UncPath }).SingleOrDefaultAsync(ct);
            if (share is null || (serverId is { } sv && sv != share.ServerId))
            {
                return (null, "There is no such share.");
            }
            return (new ReportScope(share.ServerId, sh, $"Share {share.UncPath}"), null);
        }
        if (serverId is { } server)
        {
            var host = await db.ManagedServers.AsNoTracking().Where(s => s.Id == server).Select(s => s.Hostname).SingleOrDefaultAsync(ct);
            return host is null ? (null, "There is no such file server.") : (new ReportScope(server, null, $"File server {host}"), null);
        }
        return (new ReportScope(null, null, "All enrolled file servers"), null);
    }

    /// <summary>
    /// The statement every report carries (A15): scope, how fresh the scan and directory data are, how much was covered,
    /// which classification policy applies, and where access could not be fully resolved.
    /// </summary>
    public async Task<List<ReportFact>> CommonFactsAsync(CancellationToken ct)
    {
        var facts = new List<ReportFact> { new("Scope", Scope.Description) };

        var shares = await Shares.Where(s => s.Kind == Core.Inventory.ShareKind.Disk)
            .Select(s => new { s.Included, s.State, s.LastScannedAt, s.LastScanJobId, s.Id }).ToListAsync(ct);
        var included = shares.Where(s => s.Included).ToList();
        var lastScan = shares.Max(s => s.LastScannedAt);
        facts.Add(new("Latest scan finished", lastScan is { } t ? ReportWriters.Text(t) : "No share in scope has been scanned"));
        facts.Add(new("Shares covered",
            $"{included.Count} included: {included.Count(s => s.State == ShareState.Scanned)} scanned completely, " +
            $"{included.Count(s => s.State == ShareState.Partial)} partly, {included.Count(s => s.State == ShareState.Unavailable)} unavailable, " +
            $"{included.Count(s => s.State == ShareState.NotScanned)} not scanned yet" +
            (shares.Count > included.Count ? $"; {shares.Count - included.Count} excluded by an administrator" : "")));

        var jobs = shares.Where(s => s.LastScanJobId != null).Select(s => s.LastScanJobId!.Value).Distinct().ToList();
        var ids = shares.Select(s => s.Id).ToList();
        var gaps = await Db.ScanGaps.AsNoTracking().CountAsync(g => g.ShareId != null && ids.Contains(g.ShareId.Value) && jobs.Contains(g.JobId), ct);
        var notRead = await Files.CountAsync(f => f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial, ct);
        facts.Add(new("Scan gaps and unread files",
            $"Gaps in the latest scans (folders or paths not fully covered): {gaps:N0}. Files whose contents were not read: {notRead:N0}. See the scan coverage report."));

        var custom = await CustomRuleStore.CurrentAsync(Db, ct);
        var older = await Files.CountAsync(f => f.ClassifiedPolicy != null && f.ClassifiedPolicy != custom.PolicyVersion, ct);
        facts.Add(new("Classification policy",
            custom.PolicyVersion + (custom.Count > 0 ? $", with {custom.Count:N0} custom detection {(custom.Count == 1 ? "rule" : "rules")}" : "")
            + (older > 0 ? $" ({older:N0} files were last classified with an earlier version or other custom rules and are re-read on the next scan)" : "")));

        var snapshot = await Access.SnapshotAsync(ct);
        facts.Add(new("Active Directory", snapshot is null
            ? "Not synced yet, so group memberships and who has access cannot be resolved"
            : $"{snapshot.Domain}, synced {ReportWriters.Text(snapshot.CompletedAt)}" + (snapshot.Truncated ? " (the sync hit a size limit; some groups are incomplete)" : "")));

        var unreadPermissions = await Db.Folders.AsNoTracking().CountAsync(f => f.RemovedAt == null && f.SecurityState != PermissionState.Read && Shares.Any(s => s.Id == f.ShareId), ct);
        var serverIds = await Shares.Select(s => s.ServerId).Distinct().ToListAsync(ct);
        var localGroupsUnread = await Db.ManagedServers.AsNoTracking().CountAsync(m => serverIds.Contains(m.Id) && m.LocalGroupsState != LocalGroupsState.Read, ct);
        facts.Add(new("Access resolution",
            $"Folders whose permissions could not be read: {unreadPermissions:N0}. File servers whose local groups could not be read: {localGroupsUnread} of {serverIds.Count}. " +
            "Where these matter, results say Possibly or Unknown instead of a firm answer."));
        return facts;
    }

    public ReportDocument Document(ReportInfo info, List<ReportFact> facts, IReadOnlyList<string> limitations, IReadOnlyList<string> columns, List<object?[]> rows, string? subject = null)
    {
        var truncated = rows.Count > RowLimit;
        if (truncated)
        {
            rows.RemoveRange(RowLimit, rows.Count - RowLimit);
        }
        return new ReportDocument
        {
            Info = info,
            GeneratedAt = Now,
            GeneratedBy = Actor.Name,
            Facts = facts,
            Limitations = limitations,
            Columns = columns,
            Rows = rows,
            Truncated = truncated,
            RowLimit = RowLimit,
            Subject = subject,
        };
    }
}
