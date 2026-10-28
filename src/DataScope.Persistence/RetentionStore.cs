using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Persistence;

/// <summary>
/// How long Data Scope keeps history it no longer shows as current (section <c>Retention</c>), in days. 0 keeps it
/// forever. The audit log, access reviews and their decisions, and classification overrides are
/// evidence and are never purged.
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public const int MinimumDays = 30;

    /// <summary>Files and folders that disappeared from their share.</summary>
    public int RemovedItemsDays { get; set; } = 365;

    /// <summary>The record of each permission change seen on a folder.</summary>
    public int PermissionChangesDays { get; set; } = 730;

    /// <summary>Scan gaps of scans that are no longer any share's latest.</summary>
    public int OldScanGapsDays { get; set; } = 365;

    /// <summary>Sent, skipped or failed emails.</summary>
    public int NotificationsDays { get; set; } = 180;

    /// <summary>Per-scan totals behind the trends. Each share's latest row is always kept.</summary>
    public int ScanHistoryDays { get; set; } = 1095;

    /// <summary>Collected file events and the record of missed events (D106). Alerts are kept.</summary>
    public int ActivityDays { get; set; } = 365;

    /// <summary>How often the worker purges.</summary>
    public int IntervalHours { get; set; } = 24;

    public void Validate()
    {
        foreach (var (name, days) in new[]
        {
            (nameof(RemovedItemsDays), RemovedItemsDays), (nameof(PermissionChangesDays), PermissionChangesDays),
            (nameof(OldScanGapsDays), OldScanGapsDays), (nameof(NotificationsDays), NotificationsDays), (nameof(ScanHistoryDays), ScanHistoryDays),
            (nameof(ActivityDays), ActivityDays),
        })
        {
            if (days != 0 && days < MinimumDays)
            {
                throw new InvalidOperationException($"{Section}:{name} must be 0 (keep forever) or at least {MinimumDays} days.");
            }
        }
        if (IntervalHours is < 1 or > 168)
        {
            throw new InvalidOperationException($"{Section}:{nameof(IntervalHours)} must be from 1 to 168.");
        }
    }
}

public sealed record RetentionResult(long Files, long Folders, long PermissionChanges, long ScanGaps, long Notifications, long ScanHistory,
    long ActivityEvents = 0, long ActivityGaps = 0)
{
    public long Total => Files + Folders + PermissionChanges + ScanGaps + Notifications + ScanHistory + ActivityEvents + ActivityGaps;
}

internal sealed record PurgeCounts(long Events, long Gaps);

/// <summary>
/// Deletes history older than <see cref="RetentionOptions"/> allows, in small batches so scans and the UI are never
/// held up. Anything something else still points at (a review, an owner, a reviewer's label) stays.
/// </summary>
public sealed class RetentionStore(DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock)
{
    public const int BatchSize = 5_000;
    public const string ActorSid = "system";
    public const string ActorName = "Data Scope retention";

    public async Task<RetentionResult> PurgeAsync(RetentionOptions options, CancellationToken ct)
    {
        options.Validate();
        var now = clock.GetUtcNow();
        DateTimeOffset? Cutoff(int days) => days == 0 ? null : now.AddDays(-days);

        var files = await RepeatAsync(Cutoff(options.RemovedItemsDays), """
            DELETE FROM files WHERE id IN (
                SELECT f.id FROM files f
                WHERE f.removed_at < @cutoff
                  AND NOT EXISTS (SELECT 1 FROM classification_overrides o WHERE o.file_id = f.id)
                LIMIT @batch)
            """, ct);
        // Leaf folders first; each pass frees their parents for the next.
        var folders = await RepeatAsync(Cutoff(options.RemovedItemsDays), """
            DELETE FROM folders WHERE id IN (
                SELECT f.id FROM folders f
                WHERE f.removed_at < @cutoff
                  AND NOT EXISTS (SELECT 1 FROM folders c WHERE c.parent_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM files x WHERE x.folder_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM folder_security_changes x WHERE x.folder_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM scan_frontier x WHERE x.folder_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM owner_assignments x WHERE x.folder_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM access_reviews x WHERE x.folder_id = f.id)
                LIMIT @batch)
            """, ct, untilNone: true);
        var changes = await RepeatAsync(Cutoff(options.PermissionChangesDays), """
            DELETE FROM folder_security_changes WHERE id IN (
                SELECT id FROM folder_security_changes WHERE at < @cutoff LIMIT @batch)
            """, ct);
        // Changes go before folders on the next run too, so folders freed by them follow a day later.
        var gaps = await RepeatAsync(Cutoff(options.OldScanGapsDays), """
            DELETE FROM scan_gaps WHERE id IN (
                SELECT g.id FROM scan_gaps g
                WHERE g.at < @cutoff
                  AND NOT EXISTS (SELECT 1 FROM shares s WHERE s.last_scan_job_id = g.job_id)
                LIMIT @batch)
            """, ct);
        var notifications = await RepeatAsync(Cutoff(options.NotificationsDays), """
            DELETE FROM notification_outbox WHERE id IN (
                SELECT id FROM notification_outbox
                WHERE state <> 'Pending' AND created_at < @cutoff
                LIMIT @batch)
            """, ct);
        var history = await RepeatAsync(Cutoff(options.ScanHistoryDays), """
            DELETE FROM share_scan_history WHERE id IN (
                SELECT h.id FROM share_scan_history h
                WHERE h.completed_at < @cutoff
                  AND EXISTS (SELECT 1 FROM share_scan_history n WHERE n.share_id = h.share_id AND n.completed_at > h.completed_at)
                LIMIT @batch)
            """, ct);

        var (activityEvents, activityGaps) = await PurgeActivityAsync(Cutoff(options.ActivityDays), ct);

        var result = new RetentionResult(files, folders, changes, gaps, notifications, history, activityEvents, activityGaps);
        if (result.Total > 0)
        {
            await audit.AppendAsync(ActorSid, ActorName, "retention.purge", "database", new
            {
                Removed = result,
                Kept = new { options.RemovedItemsDays, options.PermissionChangesDays, options.OldScanGapsDays, options.NotificationsDays, options.ScanHistoryDays, options.ActivityDays },
            }, ct);
        }
        return result;
    }

    /// <summary>
    /// Collected events are evidence the services' login cannot delete (D98). They are purged through a database
    /// function owned by the schema owner, which deletes only what is older than the cutoff and refuses a cutoff less
    /// than 30 days ago, whoever calls it (D106).
    /// </summary>
    private async Task<(long Events, long Gaps)> PurgeActivityAsync(DateTimeOffset? cutoff, CancellationToken ct)
    {
        if (cutoff is null)
        {
            return (0, 0);
        }
        long events = 0, gaps = 0;
        while (true)
        {
            var batch = await db.Database.SqlQuery<PurgeCounts>($"SELECT events, gaps FROM datascope_purge_activity({cutoff.Value}, {BatchSize})").SingleAsync(ct);
            events += batch.Events;
            gaps += batch.Gaps;
            if (batch.Events < BatchSize && batch.Gaps < BatchSize)
            {
                return (events, gaps);
            }
        }
    }

    /// <summary>Runs a batched delete until a batch comes back short, or (for nested folders) until nothing is left.</summary>
    private async Task<long> RepeatAsync(DateTimeOffset? cutoff, string sql, CancellationToken ct, bool untilNone = false)
    {
        if (cutoff is null)
        {
            return 0;
        }
        long total = 0;
        while (true)
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(sql,
                [new NpgsqlParameter("cutoff", cutoff.Value), new NpgsqlParameter("batch", BatchSize)], ct);
            total += deleted;
            if (untilNone ? deleted == 0 : deleted < BatchSize)
            {
                return total;
            }
        }
    }
}
