using DataScope.Core.ScanJobs;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>What removing a server changed, for the audit entry and the confirmation.</summary>
public sealed record ServerRemoval(int ScansCancelled, int Shares, long Folders, long Files, bool ActivityStopped);

public enum RemoveServerOutcome
{
    Removed,
    NotFound,
    AlreadyRemoved,
    ScanRunning,
}

/// <summary>
/// Removes a server from Data Scope and brings it back. Removing never deletes anything: the server is marked Retired,
/// its queued and paused scans are cancelled, file activity collection stops, and its shares, folders and files are
/// marked removed, exactly as if the server had been switched off. Every screen, report and review already leaves
/// removed items out, and the history (scans, findings, owners, reviews, file activity and the audit log) is kept.
/// Enrolling the same name again brings it back; its next scan marks whatever is still there as present again.
/// </summary>
public sealed class ServerStore(DataScopeDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Runs inside the caller's transaction. Refuses while a scan is running, because the worker owns that scan until
    /// its next checkpoint and could mark folders present again after they were removed.
    /// </summary>
    public async Task<(RemoveServerOutcome Outcome, ServerRemoval? Removal)> RemoveAsync(Guid serverId, string actorSid, CancellationToken ct)
    {
        var server = await db.ManagedServers.FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null)
        {
            return (RemoveServerOutcome.NotFound, null);
        }
        if (server.State == EnrollmentState.Retired)
        {
            return (RemoveServerOutcome.AlreadyRemoved, null);
        }
        if (await db.ScanJobs.AnyAsync(j => j.ServerId == serverId && j.State == ScanJobState.Running, ct))
        {
            return (RemoveServerOutcome.ScanRunning, null);
        }

        var now = clock.GetUtcNow();
        // A worker's claim updates the job row too, so whichever commits second sees the other's state.
        var cancelled = await db.ScanJobs.Where(j => j.ServerId == serverId && (j.State == ScanJobState.Queued || j.State == ScanJobState.Paused))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, ScanJobState.Cancelled)
                .SetProperty(j => j.RequestedState, (ScanJobState?)null)
                .SetProperty(j => j.LastError, "The server was removed from Data Scope.")
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (await db.ScanJobs.AnyAsync(j => j.ServerId == serverId && j.State == ScanJobState.Running, ct))
        {
            return (RemoveServerOutcome.ScanRunning, null);
        }

        var activity = await db.ActivitySources.Where(a => a.ServerId == serverId && a.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Enabled, false)
                .SetProperty(a => a.ChangedBySid, actorSid)
                .SetProperty(a => a.UpdatedAt, now), ct);

        var shares = db.Shares.Where(s => s.ServerId == serverId);
        var folders = await db.Folders.Where(f => f.RemovedAt == null && shares.Any(s => s.Id == f.ShareId))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
        var files = await db.Files.Where(f => f.RemovedAt == null && shares.Any(s => s.Id == f.ShareId))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
        var shareCount = await shares.Where(s => s.State != ShareState.Removed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ShareState.Removed).SetProperty(x => x.UpdatedAt, now), ct);

        server.State = EnrollmentState.Retired;
        server.LastError = null;
        server.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (RemoveServerOutcome.Removed, new ServerRemoval(cancelled, shareCount, folders, files, activity > 0));
    }

    /// <summary>Brings a removed server back as Pending. Its shares come back with its next scan.</summary>
    public async Task<ManagedServer?> RestoreAsync(string hostname, CancellationToken ct)
    {
        var server = await db.ManagedServers.FirstOrDefaultAsync(s => s.Hostname == hostname && s.State == EnrollmentState.Retired, ct);
        if (server is null)
        {
            return null;
        }
        server.State = EnrollmentState.Pending;
        server.LastError = null;
        server.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return server;
    }
}
