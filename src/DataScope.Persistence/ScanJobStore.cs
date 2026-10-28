using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>
/// Durable job queue on PostgreSQL. Workers claim jobs with a time-limited
/// lease; a crashed worker's job becomes claimable again when the lease runs
/// out, and resumes from its last checkpoint.
/// </summary>
public sealed class ScanJobStore(DataScopeDbContext db, TimeProvider clock)
{
    public const int MaxAttempts = 5;

    /// <summary>Atomically claims the oldest runnable job, or returns null.</summary>
    public async Task<ScanJob?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expires = now + lease;
        var queued = nameof(ScanJobState.Queued);
        var running = nameof(ScanJobState.Running);
        // SKIP LOCKED lets several workers poll without blocking or double-claiming.
        var claimed = await db.ScanJobs
            .FromSql($"""
                UPDATE scan_jobs SET
                    state = {running},
                    lease_owner = {workerId},
                    lease_expires_at = {expires},
                    attempts = attempts + 1,
                    started_at = COALESCE(started_at, {now}),
                    updated_at = {now}
                WHERE id = (
                    SELECT id FROM scan_jobs
                    WHERE (state = {queued} OR (state = {running} AND lease_expires_at < {now}))
                      AND attempts < {MaxAttempts}
                    ORDER BY created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *, xmin
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    /// <summary>
    /// Starts the scans that are due by each server's schedule (D116). A scan already under way is never doubled: the
    /// scheduled time counts as dealt with. A scan the schedule paused at its stop hour continues instead of starting
    /// again. A scheduled time missed by more than <see cref="ScanSchedule.CatchUp"/> waits for the next one.
    /// A full scan that is due (D130) takes the place of the quick scan due at the same time, and of a quick scan the
    /// schedule paused, since it reads everything that one would. While a quick scan is running the full scan waits
    /// for it to end, within the catch-up time.
    /// Returns how many scans were started or continued.
    /// </summary>
    public async Task<int> EnsureScheduledAsync(TimeZoneInfo zone, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var servers = await db.ManagedServers.AsNoTracking()
            .Where(s => s.State != EnrollmentState.Retired && (s.ScanFrequency != ScanFrequency.Off || s.FullScanFrequency != FullScanFrequency.Off))
            .Select(s => s.Id).ToListAsync(ct);
        var started = 0;
        var policy = servers.Count > 0 ? await CustomRuleStore.PolicyVersionAsync(db, ct) : ClassificationPolicy.CurrentVersion;
        foreach (var id in servers)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Locking the server row keeps two workers from starting the same scheduled scan.
            var server = (await db.ManagedServers.FromSql($"SELECT *, xmin FROM managed_servers WHERE id = {id} FOR UPDATE SKIP LOCKED").ToListAsync(ct)).SingleOrDefault();
            if (server is null || server.State == EnrollmentState.Retired)
            {
                continue;
            }
            var due = Due(ScanSchedule.LastDue(server.Schedule, now, zone), server.LastScheduledAt, now);
            var fullDue = Due(ScanSchedule.LastFullDue(server.Schedule, server.FullScan, now, zone), server.LastFullScheduledAt, now);
            if (due is null && fullDue is null)
            {
                continue;
            }
            var open = await db.ScanJobs
                .Where(j => j.ServerId == id && (j.State == ScanJobState.Queued || j.State == ScanJobState.Running || j.State == ScanJobState.Paused))
                .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
            if (fullDue is { } full)
            {
                if (open is { Kind: ScanKind.Full })
                {
                    server.LastFullScheduledAt = full;
                }
                else if (open is null or { State: ScanJobState.Paused, KeepToWindow: true })
                {
                    if (open is not null)
                    {
                        open.State = ScanJobState.Cancelled;
                        open.RequestedState = null;
                        open.LastError = ReplacedByFullNote;
                        open.FinishedAt = now;
                        open.UpdatedAt = now;
                        open = null;
                    }
                    if (!await db.ScanJobs.AnyAsync(j => j.ServerId == id && j.Kind == ScanKind.Full && j.CreatedAt >= full, ct))
                    {
                        open = NewScheduledJob(id, ScanKind.Full, server, now, policy);
                        db.ScanJobs.Add(open);
                        started++;
                    }
                    server.LastFullScheduledAt = full;
                }
                if (server.LastFullScheduledAt == full && due is { } replaced)
                {
                    // The full scan reads everything the quick scan due now would have.
                    server.LastScheduledAt = replaced;
                    due = null;
                }
            }
            if (due is { } quick)
            {
                server.LastScheduledAt = quick;
                if (open is { State: ScanJobState.Paused, KeepToWindow: true })
                {
                    open.State = ScanJobState.Queued;
                    open.LastError = null;
                    open.UpdatedAt = now;
                    started++;
                }
                else if (open is null && !await db.ScanJobs.AnyAsync(j => j.ServerId == id && j.CreatedAt >= quick, ct))
                {
                    db.ScanJobs.Add(NewScheduledJob(id, ScanKind.Quick, server, now, policy));
                    started++;
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        return started;
    }

    public const string ReplacedByFullNote = "Replaced by the scheduled full scan, which reads every file.";

    /// <summary>A scheduled time not yet dealt with and not missed by more than <see cref="ScanSchedule.CatchUp"/>.</summary>
    private static DateTimeOffset? Due(DateTimeOffset? last, DateTimeOffset? dealtWith, DateTimeOffset now) =>
        last is { } d && now - d <= ScanSchedule.CatchUp && !(dealtWith >= d) ? d : null;

    /// <summary>
    /// A scan the schedule started. It keeps to the server's scan window when there is one: paused at the stop hour,
    /// it continues at the next scheduled time. A full scan does too, so it never runs into the working day.
    /// </summary>
    private static ScanJob NewScheduledJob(Guid serverId, ScanKind kind, ManagedServer server, DateTimeOffset now, string policy) => new()
    {
        Id = Guid.CreateVersion7(),
        ServerId = serverId,
        Kind = kind,
        PolicyVersion = policy,
        RequestedBySid = ScanSchedule.Requester,
        Scheduled = true,
        KeepToWindow = kind == ScanKind.Quick || (server.ScanFrequency != ScanFrequency.Off && server.ScanStopHour is not null),
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Pauses a running scheduled scan at its stop hour. It keeps its place and continues at the next scheduled time.
    /// </summary>
    public Task<bool> PauseForWindowAsync(Guid jobId, string workerId, CancellationToken ct = default) =>
        FinishAsync(jobId, workerId, ScanJobState.Paused, WindowNote, ct);

    public const string WindowNote = "Paused at the end of the scan window. It continues from here at the next scheduled time.";

    /// <summary>Records progress and extends the lease. Returns false if the worker no longer owns the job.</summary>
    public async Task<bool> CheckpointAsync(Guid jobId, string workerId, long done, long total, string? checkpoint, TimeSpan lease, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rows = await db.ScanJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == workerId && j.State == ScanJobState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.ItemsDone, done)
                .SetProperty(j => j.ItemsTotal, total)
                .SetProperty(j => j.Checkpoint, checkpoint)
                .SetProperty(j => j.LeaseExpiresAt, now + lease)
                .SetProperty(j => j.UpdatedAt, now), ct);
        return rows == 1;
    }

    /// <summary>Whether the schedule still looks after the job; false once someone paused or resumed it by hand.</summary>
    public Task<bool> KeepsToWindowAsync(Guid jobId, CancellationToken ct = default) =>
        db.ScanJobs.Where(j => j.Id == jobId).Select(j => j.KeepToWindow).SingleOrDefaultAsync(ct);

    /// <summary>Returns a pause/cancel request made through the API, if any.</summary>
    public Task<ScanJobState?> GetRequestedStateAsync(Guid jobId, CancellationToken ct = default) =>
        db.ScanJobs.Where(j => j.Id == jobId).Select(j => j.RequestedState).SingleOrDefaultAsync(ct);

    /// <summary>Moves a claimed job to a new state, enforcing the state machine.</summary>
    public async Task<bool> FinishAsync(Guid jobId, string workerId, ScanJobState to, string? error = null, CancellationToken ct = default)
    {
        ScanJobStateMachine.EnsureTransition(ScanJobState.Running, to);
        var now = clock.GetUtcNow();
        var terminal = ScanJobStateMachine.IsTerminal(to);
        var rows = await db.ScanJobs
            .Where(j => j.Id == jobId && j.LeaseOwner == workerId && j.State == ScanJobState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, to)
                .SetProperty(j => j.RequestedState, (ScanJobState?)null)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.FinishedAt, terminal ? now : null)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (rows == 1 && terminal)
        {
            // A finished job never resumes, so its list of folders still to visit is dead weight.
            await db.ScanFrontier.Where(f => f.JobId == jobId).ExecuteDeleteAsync(ct);
        }
        return rows == 1;
    }
}
