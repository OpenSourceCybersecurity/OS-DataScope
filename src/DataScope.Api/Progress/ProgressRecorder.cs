using DataScope.Api.Access;
using DataScope.Api.Endpoints;
using DataScope.Api.Risks;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Progress;

/// <summary>
/// Measures the posture and keeps one row a day (D165). A day's row is measured again whenever a scan, a directory sync
/// or a change someone made (anything in the audit log) is newer than it, so each day ends with its last state. Only
/// counts are kept: no names, paths or contents.
/// </summary>
internal sealed class ProgressRecorder(
    DataScopeDbContext db, RiskEngine engine, RiskTriage triage, AccessService access, TimeProvider clock)
{
    /// <summary>Rows older than this are deleted.</summary>
    public const int KeepDays = 1_830;

    private static readonly List<Sensitivity> Sensitive = [Sensitivity.Confidential, Sensitivity.Restricted];

    /// <summary>When anything that changes the picture last happened: a scan, a directory sync or an audited change.</summary>
    public async Task<DateTimeOffset?> LastChangeAsync(CancellationToken ct)
    {
        var scan = await db.Shares.Where(s => s.LastScannedAt != null).MaxAsync(s => s.LastScannedAt, ct);
        var change = await db.AppAuditLog.OrderByDescending(a => a.Sequence).Select(a => (DateTimeOffset?)a.At).FirstOrDefaultAsync(ct);
        var sync = (await access.SnapshotAsync(ct))?.CompletedAt;
        return new[] { scan, change, sync }.Max();
    }

    /// <summary>
    /// Measures today's row when there is none yet or something changed since it was measured. Returns the row, or
    /// null when today's was already current.
    /// </summary>
    public async Task<PostureSnapshot?> RecordIfChangedAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var existing = await db.PostureSnapshots.AsNoTracking().Where(s => s.Day == today).Select(s => (DateTimeOffset?)s.TakenAt).FirstOrDefaultAsync(ct);
        if (existing is { } taken && (await LastChangeAsync(ct) is not { } changed || changed <= taken))
        {
            return null;
        }
        return await RecordAsync(ct);
    }

    /// <summary>Measures now and saves it as today's row, replacing an earlier one from today.</summary>
    public async Task<PostureSnapshot> RecordAsync(CancellationToken ct)
    {
        var snapshot = await MeasureAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO posture_snapshots (day, taken_at, last_scan_at, risks_high, risks_medium, risks_low, risks_accepted, risks_snoozed, risks_limited,
                files, files_not_read, restricted, confidential, sensitive_open, shares_unowned)
            VALUES ({snapshot.Day}, {snapshot.TakenAt}, {snapshot.LastScanAt}, {snapshot.RisksHigh}, {snapshot.RisksMedium}, {snapshot.RisksLow},
                {snapshot.RisksAccepted}, {snapshot.RisksSnoozed}, {snapshot.RisksLimited}, {snapshot.Files}, {snapshot.FilesNotRead}, {snapshot.Restricted},
                {snapshot.Confidential}, {snapshot.SensitiveOpen}, {snapshot.SharesUnowned})
            ON CONFLICT (day) DO UPDATE SET taken_at = excluded.taken_at, last_scan_at = excluded.last_scan_at, risks_high = excluded.risks_high,
                risks_medium = excluded.risks_medium, risks_low = excluded.risks_low, risks_accepted = excluded.risks_accepted,
                risks_snoozed = excluded.risks_snoozed, risks_limited = excluded.risks_limited, files = excluded.files,
                files_not_read = excluded.files_not_read, restricted = excluded.restricted, confidential = excluded.confidential,
                sensitive_open = excluded.sensitive_open, shares_unowned = excluded.shares_unowned
            """, ct);
        var cutoff = snapshot.Day.AddDays(-KeepDays);
        await db.PostureSnapshots.Where(s => s.Day < cutoff).ExecuteDeleteAsync(ct);
        return snapshot;
    }

    /// <summary>The posture now, measured the same way the Risks and Overview pages measure it.</summary>
    public async Task<PostureSnapshot> MeasureAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var scan = await engine.CheckAsync(null, null, permissionsOnly: false, ct);
        var risks = await triage.AnnotateAsync(scan.Items, ct);
        var open = risks.Where(r => r.Status == RiskStatus.Open).ToList();

        var files = db.Files.AsNoTracking().Where(f => f.RemovedAt == null);
        var byState = await files
            .GroupBy(f => new { Level = f.OverrideSensitivity ?? f.Sensitivity, f.ContentState })
            .Select(g => new { g.Key.Level, g.Key.ContentState, Count = g.LongCount() })
            .ToListAsync(ct);
        var sensitive = byState.Where(g => g.Level >= Sensitivity.Confidential).ToList();

        var exposure = await AccessEndpoints.ExposureAsync(db, access, Sensitivity.Confidential, null, null, ct);
        // Counted as Overview counts them.
        var unowned = await (from f in db.Folders
                             join s in db.Shares on f.ShareId equals s.Id
                             where f.Depth == 0 && f.RemovedAt == null && s.Kind == ShareKind.Disk && s.Included && s.State != ShareState.Removed
                                 && !db.OwnerAssignments.Any(a => a.ShareId == s.Id && a.EndedAt == null)
                             select f.Id).CountAsync(ct);

        return new PostureSnapshot
        {
            Day = DateOnly.FromDateTime(now.UtcDateTime),
            TakenAt = now,
            LastScanAt = await db.Shares.Where(s => s.LastScannedAt != null).MaxAsync(s => s.LastScannedAt, ct),
            RisksHigh = open.Count(r => r.Severity == RiskSeverity.High),
            RisksMedium = open.Count(r => r.Severity == RiskSeverity.Medium),
            RisksLow = open.Count(r => r.Severity == RiskSeverity.Low),
            RisksAccepted = risks.Count(r => r.Status == RiskStatus.Accepted),
            RisksSnoozed = risks.Count(r => r.Status == RiskStatus.Snoozed),
            RisksLimited = scan.Limits.Count > 0,
            Files = byState.Sum(g => g.Count),
            FilesNotRead = byState.Where(g => g.ContentState is not (ContentState.Scanned or ContentState.Partial)).Sum(g => g.Count),
            Restricted = sensitive.Where(g => g.Level == Sensitivity.Restricted).Sum(g => g.Count),
            Confidential = sensitive.Where(g => g.Level == Sensitivity.Confidential).Sum(g => g.Count),
            SensitiveOpen = exposure.SensitiveFilesOpen,
            SharesUnowned = unowned,
        };
    }
}
