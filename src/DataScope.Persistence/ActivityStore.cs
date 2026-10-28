using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

internal sealed record LearnedPath(string Key, string LocalPath);

public sealed record IngestResult(int Stored, int Duplicates, int Ignored, int Unreadable, long? LastRecordId);

/// <summary>A source the collector has claimed: which server and log to read, and from where.</summary>
/// <summary>Where to read from after checking the log, and any records found missing.</summary>
public sealed record LogCheckResult(long? ReadAfter, int LogGeneration, MissedRecords? Missed);

public sealed record ClaimedActivitySource(Guid ServerId, string Hostname, string Channel, long? LastRecordId, int LogGeneration);

/// <summary>
/// Stores collected Windows file events and the collection state of each server's log. Ingest is idempotent (the
/// same event twice is one row), keeps every event's raw text, and matches events to shares and scanned files and
/// folders without ever changing what the event said (D97 to D100).
/// </summary>
public sealed class ActivityStore(DataScopeDbContext db, TimeProvider clock)
{
    public const string SecurityChannel = "Security";

    /// <summary>Raw text kept for an event that could not be read, which may be over the parser's limit.</summary>
    public const int MaxRawChars = SecurityEventXml.MaxChars;

    /// <summary>
    /// Stores a batch of events read from one server's log, oldest first, and advances the source's position past
    /// them, in one transaction.
    /// </summary>
    public async Task<IngestResult> IngestAsync(Guid serverId, string channel, IReadOnlyList<RawSecurityEvent> batch, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var source = await db.ActivitySources.FirstAsync(s => s.ServerId == serverId && s.Channel == channel, ct);
        var generation = source.LogGeneration;
        var ordered = batch.DistinctBy(e => e.RecordId).OrderBy(e => e.RecordId).ToList();
        if (ordered.Count == 0)
        {
            return new IngestResult(0, 0, 0, 0, source.LastRecordId);
        }
        var recordIds = ordered.Select(e => e.RecordId).ToList();
        var existing = (await db.FileAuditEvents
            .Where(e => e.ServerId == serverId && e.Channel == channel && e.LogGeneration == generation && recordIds.Contains(e.RecordId))
            .Select(e => e.RecordId).ToListAsync(ct)).ToHashSet();

        var shares = await db.Shares.Where(s => s.ServerId == serverId).ToListAsync(ct);
        var byName = shares.ToDictionary(s => s.NameKey);
        var learned = await LearnFromStoredAsync(serverId, shares, now, ct);
        var map = BuildMap(shares);
        var handles = new Dictionary<(string? Logon, long? Process, long Handle), FileAuditEvent>();
        var rows = new List<FileAuditEvent>();
        int duplicates = 0, ignored = 0, unreadable = 0;

        foreach (var raw in ordered)
        {
            if (existing.Contains(raw.RecordId))
            {
                duplicates++;
                continue;
            }
            if (!SecurityEventXml.TryParse(raw.Xml, out var ev, out var error))
            {
                unreadable++;
                rows.Add(Unreadable(serverId, channel, generation, raw, error!, now));
                continue;
            }
            if (ev!.RecordId != raw.RecordId)
            {
                unreadable++;
                rows.Add(Unreadable(serverId, channel, generation, raw, "The event's EventRecordID does not match the record id the log reported.", now));
                continue;
            }
            if (ActivityInterpreter.Interpret(ev) is not { } e)
            {
                ignored++;
                continue;
            }
            var row = Row(serverId, channel, generation, ev, e, raw.Xml, now);
            row.EventShareName = e.ShareName;
            row.EventShareLocalPath = e.ShareLocalPath is { Length: <= 1024 } shareRoot ? shareRoot : null;

            if (e.ShareName is { } shareName && byName.TryGetValue(ShareName.Key(shareName), out var named))
            {
                if (e.ShareLocalPath is { } root && named.LocalPathSource != ShareLocalPathSource.Admin
                    && !string.Equals(named.LocalPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    named.LocalPath = root;
                    named.LocalPathSource = ShareLocalPathSource.Event;
                    named.LocalPathSetAt = now;
                    learned.Add(named);
                    map = BuildMap(shares);
                }
                row.ShareId = named.Id;
                row.RelativePath = e.ShareRelativePath;
            }
            else if (map.Match(row.ObjectPath) is { } match)
            {
                row.ShareId = match.ShareId;
                row.RelativePath = match.RelativePath;
            }

            if (e.Operation == ActivityOperation.Deleted && e.HandleId is { } deletedHandle)
            {
                var key = (e.LogonId, e.ProcessId, deletedHandle);
                var opened = handles.TryGetValue(key, out var inBatch) ? inBatch : await LastOnHandleAsync(serverId, generation, key, raw.RecordId, ct);
                if (opened is { AccessMask: { } mask, ObjectPath: not null } && (mask & FileAccessMask.Delete) != 0)
                {
                    row.ObjectPath = opened.ObjectPath;
                    row.ShareId = opened.ShareId;
                    row.RelativePath = opened.RelativePath;
                    row.PathSource = ActivityPathSource.Correlated;
                    row.CorrelatedRecordId = opened.RecordId;
                }
                // The handle is closed; a later 4660 on a reused value must not borrow this path.
                handles[key] = row;
            }
            else if (ev.EventId is 4656 or 4663 && e.HandleId is { } handle and not 0)
            {
                handles[(e.LogonId, e.ProcessId, handle)] = row;
            }
            rows.Add(row);
        }

        await ResolveObjectKindsAsync(rows, ct);
        db.FileAuditEvents.AddRange(rows);
        var lastRecord = ordered[^1].RecordId;
        source.LastRecordId = Math.Max(source.LastRecordId ?? 0, lastRecord);
        source.EventsStored += rows.Count;
        source.EventsIgnored += ignored;
        source.EventsUnreadable += unreadable;
        var newest = rows.Where(r => r.EventId != 0).Select(r => (DateTimeOffset?)r.EventTime).Max();
        if (newest > (source.LastEventAt ?? DateTimeOffset.MinValue))
        {
            source.LastEventAt = newest;
        }
        source.UpdatedAt = now;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        foreach (var share in learned)
        {
            await MatchUnmatchedAsync(share, ct);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new IngestResult(rows.Count - unreadable, duplicates, ignored, unreadable, source.LastRecordId);
    }

    /// <summary>
    /// Brings stored events up to date with what Data Scope knows now, without changing the events themselves:
    /// learns share folders from 5140 and 5145 events stored before the share was listed, matches events in those
    /// folders to the share, and, after a scan, marks events whose path is now a scanned file or folder. The
    /// collector calls this on every pass, so matching catches up even when no new events arrive.
    /// </summary>
    public async Task CatchUpAsync(Guid serverId, string channel, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var shares = await db.Shares.Where(s => s.ServerId == serverId).ToListAsync(ct);
        var learned = await LearnFromStoredAsync(serverId, shares, now, ct);
        var source = await db.ActivitySources.FirstAsync(s => s.ServerId == serverId && s.Channel == channel, ct);
        var lastScan = await db.ScanJobs.Where(j => j.ServerId == serverId && j.FinishedAt != null).MaxAsync(j => j.FinishedAt, ct);
        var rescanned = lastScan is not null && (source.KindsCheckedAt is null || lastScan > source.KindsCheckedAt);
        if (learned.Count == 0 && !rescanned)
        {
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        foreach (var share in learned)
        {
            await MatchUnmatchedAsync(share, ct);
        }
        await ResolveStoredKindsAsync(serverId, ct);
        source.KindsCheckedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Marks stored events not yet matched to a scanned file or folder, using the inventory's own keys (SHA-256 of
    /// the upper-cased share-relative path, the upper-cased file name).
    /// </summary>
    private async Task ResolveStoredKindsAsync(Guid serverId, CancellationToken ct)
    {
        var unknown = nameof(ActivityObjectKind.Unknown);
        var folder = nameof(ActivityObjectKind.Folder);
        var file = nameof(ActivityObjectKind.File);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE file_audit_events e SET object_kind = {folder}
            FROM folders f
            WHERE e.server_id = {serverId} AND e.object_kind = {unknown} AND e.relative_path IS NOT NULL
              AND f.share_id = e.share_id AND f.path_hash = sha256(convert_to(upper(e.relative_path), 'UTF8'))
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE file_audit_events e SET object_kind = {file}
            FROM folders f JOIN files x ON x.folder_id = f.id
            WHERE e.server_id = {serverId} AND e.object_kind = {unknown} AND e.relative_path IS NOT NULL AND e.relative_path <> ''
              AND f.share_id = e.share_id
              AND f.path_hash = sha256(convert_to(upper(CASE WHEN strpos(e.relative_path, E'\\') = 0 THEN '' ELSE regexp_replace(e.relative_path, E'\\\\[^\\\\]*$', '') END), 'UTF8'))
              AND x.name_key = upper(regexp_replace(e.relative_path, E'^.*\\\\', ''))
            """, ct);
    }

    private async Task<HashSet<Share>> LearnFromStoredAsync(Guid serverId, List<Share> shares, DateTimeOffset now, CancellationToken ct)
    {
        var learned = new HashSet<Share>();
        var unknown = shares.Where(s => s.LocalPath is null && s.State != ShareState.Removed).ToList();
        if (unknown.Count == 0)
        {
            return learned;
        }
        var keys = unknown.Select(s => s.NameKey).ToArray();
        var seen = await db.Database.SqlQuery<LearnedPath>($"""
            SELECT DISTINCT ON (upper(event_share_name)) upper(event_share_name) AS key, event_share_local_path AS local_path
            FROM file_audit_events
            WHERE server_id = {serverId} AND event_share_local_path IS NOT NULL AND upper(event_share_name) = ANY({keys})
            ORDER BY upper(event_share_name), log_generation DESC, record_id DESC
            """).ToListAsync(ct);
        foreach (var share in unknown)
        {
            // Share names are case-insensitive; the event may spell it differently from the share listing.
            if (seen.FirstOrDefault(x => x.Key == share.NameKey) is { } latest)
            {
                share.LocalPath = latest.LocalPath;
                share.LocalPathSource = ShareLocalPathSource.Event;
                share.LocalPathSetAt = now;
                learned.Add(share);
            }
        }
        return learned;
    }

    private static SharePathMap BuildMap(IEnumerable<Share> shares) =>
        new(shares.Where(s => s.LocalPath is not null).Select(s => (s.Id, s.LocalPath!)));

    /// <summary>
    /// The most recent earlier event on the same handle in the same logon session and process (D100). Handle values
    /// are reused, so only the latest one counts, and it must have been opened for DELETE.
    /// </summary>
    private Task<FileAuditEvent?> LastOnHandleAsync(Guid serverId, int generation, (string? Logon, long? Process, long Handle) key, long before, CancellationToken ct) =>
        db.FileAuditEvents.AsNoTracking()
            .Where(e => e.ServerId == serverId && e.LogGeneration == generation && e.HandleId == key.Handle
                && e.LogonId == key.Logon && e.ProcessId == key.Process && e.RecordId < before
                && (e.EventId == 4656 || e.EventId == 4663 || e.EventId == 4660))
            .OrderByDescending(e => e.RecordId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Marks events whose path is a scanned file or folder as such, so "read" and "list folder" can be told apart.</summary>
    private async Task ResolveObjectKindsAsync(List<FileAuditEvent> rows, CancellationToken ct)
    {
        var located = rows.Where(r => r.ShareId is not null && r.RelativePath is not null && r.Operation != ActivityOperation.ShareConnect).ToList();
        if (located.Count == 0)
        {
            return;
        }
        var shareIds = located.Select(r => r.ShareId!.Value).Distinct().ToList();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in located)
        {
            paths.Add(PathText.Key(r.RelativePath!));
            paths.Add(PathText.Key(Parent(r.RelativePath!)));
        }
        var hashes = paths.Select(InventoryStore.PathHash).ToList();
        var folders = await db.Folders.AsNoTracking()
            .Where(f => shareIds.Contains(f.ShareId) && hashes.Contains(f.PathHash))
            .Select(f => new { f.Id, f.ShareId, f.Path })
            .ToListAsync(ct);
        var folderByPath = folders.ToDictionary(f => (f.ShareId, PathText.Key(f.Path)), f => f.Id);
        var names = located.Select(r => Name(r.RelativePath!).ToUpperInvariant()).Distinct().ToList();
        var folderIds = folders.Select(f => f.Id).ToList();
        var files = (await db.Files.AsNoTracking()
            .Where(f => folderIds.Contains(f.FolderId) && names.Contains(f.NameKey))
            .Select(f => new { f.FolderId, f.NameKey })
            .ToListAsync(ct)).Select(f => (f.FolderId, f.NameKey)).ToHashSet();

        foreach (var r in located)
        {
            var share = r.ShareId!.Value;
            if (folderByPath.ContainsKey((share, PathText.Key(r.RelativePath!))))
            {
                r.ObjectKind = ActivityObjectKind.Folder;
            }
            else if (r.RelativePath!.Length > 0 && folderByPath.TryGetValue((share, PathText.Key(Parent(r.RelativePath))), out var parent)
                && files.Contains((parent, Name(r.RelativePath).ToUpperInvariant())))
            {
                r.ObjectKind = ActivityObjectKind.File;
            }
        }
    }

    private static string Parent(string relative) => relative.LastIndexOf('\\') is var i and >= 0 ? relative[..i] : "";

    private static string Name(string relative) => relative[(relative.LastIndexOf('\\') + 1)..];

    /// <summary>
    /// Fills in the share for this server's events that are inside the share's folder but were stored before the
    /// folder was known. The event itself is not changed (D98, D99).
    /// </summary>
    private async Task<int> MatchUnmatchedAsync(Share share, CancellationToken ct)
    {
        if (LocalPath.Normalize(share.LocalPath) is not { } root)
        {
            return 0;
        }
        var trimmed = root.TrimEnd('\\');
        var prefix = trimmed + "\\";
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE file_audit_events SET
                share_id = {share.Id},
                relative_path = CASE WHEN length(object_path) <= {prefix.Length} THEN '' ELSE substr(object_path, {prefix.Length + 1}) END
            WHERE server_id = {share.ServerId} AND share_id IS NULL AND object_path IS NOT NULL
              AND (upper(object_path) = upper({trimmed}) OR starts_with(upper(object_path), upper({prefix})))
            """, ct);
    }

    /// <summary>Sets a share's local folder by hand (an admin), and matches stored events to it.</summary>
    public async Task<Share?> SetShareLocalPathAsync(Guid shareId, string? localPath, CancellationToken ct)
    {
        var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == shareId, ct);
        if (share is null)
        {
            return null;
        }
        var now = clock.GetUtcNow();
        share.LocalPath = LocalPath.Normalize(localPath);
        share.LocalPathSource = share.LocalPath is null ? null : ShareLocalPathSource.Admin;
        share.LocalPathSetAt = share.LocalPath is null ? null : now;
        share.UpdatedAt = now;
        // The caller may already hold a transaction (to write the audit entry with the change).
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.SaveChangesAsync(ct);
        await MatchUnmatchedAsync(share, ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
        return share;
    }

    private static FileAuditEvent Row(Guid serverId, string channel, int generation, SecurityEvent ev, InterpretedEvent e, string xml, DateTimeOffset now) => new()
    {
        ServerId = serverId,
        Channel = channel,
        RecordId = ev.RecordId,
        LogGeneration = generation,
        EventId = ev.EventId,
        Provider = ev.Provider,
        EventTime = ev.TimeCreated,
        IngestedAt = now,
        PrincipalSid = e.PrincipalSid,
        PrincipalName = e.PrincipalName,
        LogonId = e.LogonId,
        ProcessId = e.ProcessId,
        ProcessName = e.ProcessName,
        HandleId = e.HandleId,
        ClientAddress = e.ClientAddress,
        ObjectPath = e.ObjectPath,
        PathSource = e.ObjectPath is null ? ActivityPathSource.None : ActivityPathSource.Event,
        Operation = e.Operation,
        Certainty = e.Certainty,
        Outcome = e.Outcome,
        AccessMask = e.AccessMask,
        OldSecurity = e.OldSecurity,
        NewSecurity = e.NewSecurity,
        RawXml = xml,
    };

    private static FileAuditEvent Unreadable(Guid serverId, string channel, int generation, RawSecurityEvent raw, string error, DateTimeOffset now) => new()
    {
        ServerId = serverId,
        Channel = channel,
        RecordId = raw.RecordId,
        LogGeneration = generation,
        EventTime = now,
        IngestedAt = now,
        PathSource = ActivityPathSource.None,
        Operation = ActivityOperation.Unreadable,
        Certainty = ActivityCertainty.Performed,
        Outcome = ActivityOutcome.Success,
        ParseError = error.Length > 500 ? error[..500] : error,
        RawXml = PathText.Sanitize(raw.Xml.Length > MaxRawChars ? raw.Xml[..MaxRawChars] : raw.Xml, out _),
    };

    /// <summary>Turns collection on or off for a server; creates its source row the first time.</summary>
    public async Task<ActivitySource> SetEnabledAsync(Guid serverId, bool enabled, string actorSid, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var source = await db.ActivitySources.FirstOrDefaultAsync(s => s.ServerId == serverId && s.Channel == SecurityChannel, ct);
        if (source is null)
        {
            source = new ActivitySource { ServerId = serverId, Channel = SecurityChannel, State = ActivitySourceState.Waiting };
            db.ActivitySources.Add(source);
        }
        if (enabled && !source.Enabled)
        {
            source.NextReadAt = now;
        }
        source.Enabled = enabled;
        source.ChangedBySid = actorSid;
        source.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return source;
    }

    /// <summary>Claims the next enabled source that is due and not being read by another worker.</summary>
    public async Task<ClaimedActivitySource?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expires = now + lease;
        var claimed = await db.ActivitySources
            .FromSql($"""
                UPDATE activity_sources SET lease_owner = {workerId}, lease_expires_at = {expires}
                WHERE (server_id, channel) = (
                    SELECT a.server_id, a.channel FROM activity_sources a
                    JOIN managed_servers s ON s.id = a.server_id
                    WHERE a.enabled AND s.state <> 'Retired'
                      AND (a.next_read_at IS NULL OR a.next_read_at <= {now})
                      AND (a.lease_expires_at IS NULL OR a.lease_expires_at < {now})
                    ORDER BY a.next_read_at NULLS FIRST
                    FOR UPDATE OF a SKIP LOCKED
                    LIMIT 1)
                RETURNING *, xmin
                """)
            .AsNoTracking().ToListAsync(ct);
        if (claimed.SingleOrDefault() is not { } source)
        {
            return null;
        }
        var hostname = await db.ManagedServers.Where(s => s.Id == source.ServerId).Select(s => s.Hostname).SingleAsync(ct);
        return new ClaimedActivitySource(source.ServerId, hostname, source.Channel, source.LastRecordId, source.LogGeneration);
    }

    /// <summary>Records a finished read and releases the claim. An error keeps the position, so nothing is skipped.</summary>
    public async Task ReleaseAsync(Guid serverId, string channel, string workerId, DateTimeOffset nextReadAt, string? error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var state = error is null ? ActivitySourceState.Ok : ActivitySourceState.Error;
        var message = error is { Length: > 2000 } ? error[..2000] : error;
        await db.ActivitySources
            .Where(s => s.ServerId == serverId && s.Channel == channel && s.LeaseOwner == workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.LastReadAt, x => error == null ? now : x.LastReadAt)
                .SetProperty(x => x.LastError, x => error == null ? x.LastError : message)
                .SetProperty(x => x.LastErrorAt, x => error == null ? x.LastErrorAt : now)
                .SetProperty(x => x.NextReadAt, nextReadAt)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Compares the position with what the log holds now (D102): records missing since the last read become a gap, a
    /// log that started again gets a new generation, and the log's size and range are kept for the health view.
    /// </summary>
    public async Task<LogCheckResult> CheckLogAsync(Guid serverId, string channel, SecurityLogInfo log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var now = clock.GetUtcNow();
        var source = await db.ActivitySources.SingleAsync(s => s.ServerId == serverId && s.Channel == channel, ct);
        var check = CollectionPosition.Check(source.LastRecordId, log);
        if (check.Missed is { } missed)
        {
            db.ActivityGaps.Add(new ActivityGap
            {
                ServerId = serverId,
                Channel = channel,
                LogGeneration = source.LogGeneration,
                Reason = missed.Reason,
                FirstRecordId = missed.FirstRecordId,
                LastRecordId = missed.LastRecordId,
                DetectedAt = now,
                LogOldestEventAt = log.OldestEventAt,
            });
        }
        if (check.NewGeneration)
        {
            source.LogGeneration++;
            source.LastRecordId = null;
        }
        else if (check.ReadAfter > (source.LastRecordId ?? 0))
        {
            source.LastRecordId = check.ReadAfter;
        }
        source.LogOldestRecordId = log.OldestRecordId;
        source.LogNewestRecordId = log.NewestRecordId;
        source.LogRecordCount = log.RecordCount;
        source.LogOldestEventAt = log.OldestEventAt;
        source.LogSizeBytes = log.SizeBytes;
        source.LogMaxSizeBytes = log.MaxSizeBytes;
        source.LogFull = log.IsFull;
        source.LogCheckedAt = now;
        source.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return new LogCheckResult(check.NewGeneration ? null : source.LastRecordId, source.LogGeneration, check.Missed);
    }

    /// <summary>
    /// After a read that reached the end of the log, moves the position to the newest record that existed before the
    /// read, collected or not, so records that were never file events don't later look like a gap.
    /// </summary>
    public async Task MarkCaughtUpAsync(Guid serverId, string channel, int logGeneration, long throughRecordId, CancellationToken ct) =>
        await db.ActivitySources
            .Where(s => s.ServerId == serverId && s.Channel == channel && s.LogGeneration == logGeneration
                && (s.LastRecordId == null || s.LastRecordId < throughRecordId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRecordId, throughRecordId), ct);

    public async Task<bool> RenewLeaseAsync(Guid serverId, string channel, string workerId, TimeSpan lease, CancellationToken ct)
    {
        var expires = clock.GetUtcNow() + lease;
        return await db.ActivitySources
            .Where(s => s.ServerId == serverId && s.Channel == channel && s.LeaseOwner == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, expires), ct) == 1;
    }
}
