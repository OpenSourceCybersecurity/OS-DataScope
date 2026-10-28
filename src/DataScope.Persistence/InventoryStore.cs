using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace DataScope.Persistence;

/// <summary>A share reported by a server during enumeration.</summary>
public sealed record DiscoveredShare(string Name, string? Remark, ShareKind Kind);

/// <summary>A child folder found while listing a folder.</summary>
public sealed record DiscoveredFolder(string Name, DateTimeOffset? ModifiedUtc, int Attributes);

/// <summary>A file found while listing a folder (metadata only).</summary>
public sealed record DiscoveredFile(string Name, long Size, DateTimeOffset? CreatedUtc, DateTimeOffset? ModifiedUtc, int Attributes);

/// <summary>Something in a folder that could not be covered.</summary>
public sealed record DiscoveredGap(string Path, GapKind Kind, string Message);

/// <summary>Next folder a job should list.</summary>
public sealed record FrontierFolder(long Seq, Guid FolderId, Guid ShareId, string ShareName, string Path, int Depth);

/// <summary>
/// Writes the share/folder/file inventory for scan jobs. The set of folders still to list
/// (the frontier) is stored per job, so a paused, crashed or restarted job resumes exactly
/// where it stopped, and each folder's results commit atomically with its removal from the frontier.
/// </summary>
public sealed class InventoryStore(DataScopeDbContext db, TimeProvider clock)
{
    private const int FileBatch = 2000;
    private readonly PermissionStore _permissions = new(db, clock);

    /// <summary>Permission writes that share this store's descriptor cache.</summary>
    public PermissionStore Permissions => _permissions;

    public static byte[] PathHash(string relativePath) => SHA256.HashData(Encoding.UTF8.GetBytes(PathText.Key(relativePath)));

    /// <summary>
    /// Records the server's current share list. New disk shares are included by default; shares the server
    /// no longer reports are marked Removed (kept for history); the server is marked reachable.
    /// </summary>
    public async Task SyncSharesAsync(Guid serverId, string hostname, IReadOnlyList<DiscoveredShare> discovered, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var existing = await db.Shares.Where(s => s.ServerId == serverId).ToListAsync(ct);
        var byKey = existing.ToDictionary(s => s.NameKey);
        var seen = new HashSet<string>();
        foreach (var d in discovered.Where(d => ShareName.IsValid(d.Name)))
        {
            var key = ShareName.Key(d.Name);
            if (!seen.Add(key))
            {
                continue;
            }
            if (byKey.TryGetValue(key, out var share))
            {
                share.Name = d.Name;
                share.Remark = Truncate(d.Remark, 256);
                share.Kind = d.Kind;
                if (d.Kind != ShareKind.Disk)
                {
                    share.Included = false;
                }
                if (share.State == ShareState.Removed)
                {
                    share.State = ShareState.NotScanned;
                }
                share.UpdatedAt = now;
            }
            else
            {
                db.Shares.Add(new Share
                {
                    Id = Guid.CreateVersion7(),
                    ServerId = serverId,
                    Name = d.Name,
                    NameKey = key,
                    UncPath = ShareName.Unc(hostname, d.Name),
                    Remark = Truncate(d.Remark, 256),
                    Kind = d.Kind,
                    Included = d.Kind == ShareKind.Disk,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
        foreach (var gone in existing.Where(s => !seen.Contains(s.NameKey) && s.State != ShareState.Removed))
        {
            gone.State = ShareState.Removed;
            gone.UpdatedAt = now;
        }
        var server = await db.ManagedServers.SingleAsync(s => s.Id == serverId, ct);
        if (server.State != EnrollmentState.Retired)
        {
            server.State = EnrollmentState.Enrolled;
        }
        server.LastSeenAt = now;
        server.LastError = null;
        server.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks the server unreachable with a short, path-free reason.</summary>
    public async Task MarkServerErrorAsync(Guid serverId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.ManagedServers.Where(s => s.Id == serverId && s.State != EnrollmentState.Retired)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, EnrollmentState.Error)
                .SetProperty(x => x.LastError, Truncate(reason, 2000))
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    /// <summary>Puts the root folder of every included share on the job's frontier. Returns the number of shares.</summary>
    public async Task<int> SeedAsync(Guid jobId, Guid serverId, CancellationToken ct)
    {
        var shares = await db.Shares.AsNoTracking()
            .Where(s => s.ServerId == serverId && s.Included && s.Kind == ShareKind.Disk && s.State != ShareState.Removed)
            .Select(s => s.Id).ToListAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var shareId in shares)
        {
            var ids = await UpsertFoldersAsync(jobId, shareId, null, 0, [("", null, 0)], ct);
            await EnqueueAsync(jobId, shareId, 0, ids, ct);
        }
        await tx.CommitAsync(ct);
        return shares.Count;
    }

    /// <summary>The oldest unlisted folder for the job, or null when the walk is finished.</summary>
    public async Task<FrontierFolder?> NextAsync(Guid jobId, CancellationToken ct)
    {
        var row = await (
            from f in db.ScanFrontier.AsNoTracking()
            join folder in db.Folders on f.FolderId equals folder.Id
            join share in db.Shares on f.ShareId equals share.Id
            where f.JobId == jobId
            orderby f.Seq
            select new FrontierFolder(f.Seq, f.FolderId, f.ShareId, share.Name, folder.Path, f.Depth)).FirstOrDefaultAsync(ct);
        return row;
    }

    /// <summary>
    /// Stores the result of listing one folder and removes it from the frontier, in one transaction.
    /// Children of a fully listed folder that were not seen this time are marked removed.
    /// </summary>
    public async Task CompleteFolderAsync(
        Guid jobId,
        FrontierFolder folder,
        IReadOnlyList<DiscoveredFolder> subfolders,
        IReadOnlyList<DiscoveredFile> files,
        IReadOnlyList<DiscoveredGap> gaps,
        bool listedCompletely,
        bool enqueueSubfolders,
        CancellationToken ct,
        DiscoveredSecurity? security = null,
        IReadOnlySet<string>? holdBack = null)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Excluded subfolders (D124) are recorded as still there, so they are not taken for removed, but never listed.
        var held = holdBack is { Count: > 0 } ? subfolders.Where(s => holdBack.Contains(s.Name)).ToList() : [];
        var walked = held.Count > 0 ? subfolders.Where(s => !holdBack!.Contains(s.Name)).ToList() : subfolders;
        var childIds = await UpsertFoldersAsync(jobId, folder.ShareId, folder.FolderId, folder.Depth + 1,
            [.. walked.Select(s => (PathText.Child(folder.Path, s.Name), s.ModifiedUtc, s.Attributes))], ct);
        await UpsertFoldersAsync(jobId, folder.ShareId, folder.FolderId, folder.Depth + 1,
            [.. held.Select(s => (PathText.Child(folder.Path, s.Name), s.ModifiedUtc, s.Attributes))], ct);
        if (enqueueSubfolders)
        {
            await EnqueueAsync(jobId, folder.ShareId, folder.Depth + 1, childIds, ct);
        }
        foreach (var chunk in files.Chunk(FileBatch))
        {
            await UpsertFilesAsync(jobId, folder.ShareId, folder.FolderId, chunk, ct);
        }
        foreach (var gap in gaps)
        {
            db.ScanGaps.Add(new ScanGap { JobId = jobId, ShareId = folder.ShareId, Path = gap.Path, Kind = gap.Kind, Message = Truncate(gap.Message, 500)!, At = now });
        }
        await db.SaveChangesAsync(ct);

        if (listedCompletely)
        {
            await db.Folders.Where(f => f.Id == folder.FolderId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastWalkedJobId, jobId), ct);
            // Only a complete listing proves something is gone.
            await db.Files.Where(f => f.FolderId == folder.FolderId && f.LastSeenJobId != jobId && f.RemovedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
            await db.Folders.Where(f => f.ParentId == folder.FolderId && f.LastSeenJobId != jobId && f.RemovedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
        }
        if (security is not null)
        {
            await _permissions.RecordFolderAsync(jobId, folder.FolderId, security, ct);
        }
        await db.ScanFrontier.Where(f => f.Seq == folder.Seq).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Records a gap that is not tied to listing one folder (share unreachable, job limit).</summary>
    public async Task AddGapAsync(Guid jobId, Guid? shareId, string path, GapKind kind, string message, CancellationToken ct)
    {
        db.ScanGaps.Add(new ScanGap { JobId = jobId, ShareId = shareId, Path = path, Kind = kind, Message = Truncate(message, 500)!, At = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes one folder off the frontier without listing it: an exclusion added while the scan was paused covers it.</summary>
    public Task<int> DropFolderAsync(long seq, CancellationToken ct) =>
        db.ScanFrontier.Where(f => f.Seq == seq).ExecuteDeleteAsync(ct);

    /// <summary>Drops every remaining frontier entry for a share (it became unreachable) and returns how many.</summary>
    public Task<int> DropShareFrontierAsync(Guid jobId, Guid shareId, CancellationToken ct) =>
        db.ScanFrontier.Where(f => f.JobId == jobId && f.ShareId == shareId).ExecuteDeleteAsync(ct);

    public Task<int> ClearFrontierAsync(Guid jobId, CancellationToken ct) =>
        db.ScanFrontier.Where(f => f.JobId == jobId).ExecuteDeleteAsync(ct);

    /// <summary>Folders listed so far by the job, and folders still waiting.</summary>
    public async Task<(long Walked, long Pending)> ProgressAsync(Guid jobId, CancellationToken ct)
    {
        var walked = await db.Folders.LongCountAsync(f => f.LastWalkedJobId == jobId, ct);
        var pending = await db.ScanFrontier.LongCountAsync(f => f.JobId == jobId, ct);
        return (walked, pending);
    }

    public Task<long> EntriesSeenAsync(Guid jobId, CancellationToken ct) =>
        db.Files.LongCountAsync(f => f.LastSeenJobId == jobId, ct);

    /// <summary>
    /// Updates each scanned share's totals and state after the walk. A share with no gaps also gets
    /// anything not seen in this job marked removed (catching descendants of removed folders).
    /// </summary>
    public async Task FinalizeAsync(Guid jobId, Guid serverId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var shareIds = await db.Folders.Where(f => f.LastSeenJobId == jobId).Select(f => f.ShareId).Distinct().ToListAsync(ct);
        var gapShares = await db.ScanGaps.Where(g => g.JobId == jobId && g.ShareId != null)
            .GroupBy(g => g.ShareId!.Value)
            .Select(g => new { ShareId = g.Key, Count = g.Count(), Unavailable = g.Any(x => x.Kind == GapKind.ShareUnavailable) })
            .ToDictionaryAsync(g => g.ShareId, ct);
        foreach (var shareId in shareIds.Union(gapShares.Keys))
        {
            var gap = gapShares.GetValueOrDefault(shareId);
            if (gap is null)
            {
                await db.Folders.Where(f => f.ShareId == shareId && f.LastSeenJobId != jobId && f.RemovedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
                await db.Files.Where(f => f.ShareId == shareId && f.LastSeenJobId != jobId && f.RemovedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.RemovedAt, now), ct);
            }
            var folders = await db.Folders.LongCountAsync(f => f.ShareId == shareId && f.RemovedAt == null, ct);
            var live = db.Files.Where(f => f.ShareId == shareId && f.RemovedAt == null);
            var fileCount = await live.LongCountAsync(ct);
            var fileBytes = await live.SumAsync(f => (long?)f.Size, ct) ?? 0;
            var state = gap is null ? ShareState.Scanned : gap.Unavailable ? ShareState.Unavailable : ShareState.Partial;
            await db.Shares.Where(s => s.Id == shareId && s.ServerId == serverId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.LastScanJobId, jobId)
                .SetProperty(x => x.LastScannedAt, now)
                .SetProperty(x => x.FolderCount, folders)
                .SetProperty(x => x.FileCount, fileCount)
                .SetProperty(x => x.TotalBytes, fileBytes)
                .SetProperty(x => x.GapCount, gap == null ? 0 : gap.Count)
                .SetProperty(x => x.UpdatedAt, now), ct);
        }
    }

    /// <summary>
    /// Records the totals of every share this job finished, for trends. Run after classification, so the sensitive
    /// counts are this scan's. Running it again for the same job updates the rows.
    /// </summary>
    public async Task RecordHistoryAsync(Guid jobId, CancellationToken ct)
    {
        var shares = await db.Shares.AsNoTracking().Where(s => s.LastScanJobId == jobId && s.LastScannedAt != null)
            .Select(s => new { s.Id, s.State, s.FileCount, s.TotalBytes, s.GapCount, s.LastScannedAt }).ToListAsync(ct);
        foreach (var share in shares)
        {
            var live = db.Files.Where(f => f.ShareId == share.Id && f.RemovedAt == null);
            var labels = await live.Where(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Restricted || (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Confidential)
                .GroupBy(f => f.OverrideSensitivity ?? f.Sensitivity).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
            var notRead = await live.LongCountAsync(f => f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial, ct);
            var row = await db.ShareScanHistory.SingleOrDefaultAsync(h => h.ShareId == share.Id && h.JobId == jobId, ct);
            if (row is null)
            {
                row = new ShareScanHistory { ShareId = share.Id, JobId = jobId };
                db.ShareScanHistory.Add(row);
            }
            row.CompletedAt = clock.GetUtcNow();
            row.State = share.State;
            row.Files = share.FileCount;
            row.Bytes = share.TotalBytes;
            row.Gaps = share.GapCount;
            row.RestrictedFiles = labels.Where(l => l.Key == Sensitivity.Restricted).Sum(l => l.Count);
            row.ConfidentialFiles = labels.Where(l => l.Key == Sensitivity.Confidential).Sum(l => l.Count);
            row.FilesNotRead = notRead;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Guid>> UpsertFoldersAsync(Guid jobId, Guid shareId, Guid? parentId, int depth,
        List<(string Path, DateTimeOffset? Modified, int Attributes)> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        await using var cmd = await CommandAsync("""
            INSERT INTO folders (id, share_id, parent_id, path, path_hash, depth, modified_at_source, attributes, first_seen_job_id, last_seen_job_id)
            SELECT u.id, @share, @parent, u.path, u.hash, @depth, u.modified, u.attributes, @job, @job
            FROM unnest(@ids, @paths, @hashes, @modified, @attributes) AS u(id, path, hash, modified, attributes)
            ON CONFLICT (share_id, path_hash) DO UPDATE SET
                path = EXCLUDED.path, parent_id = EXCLUDED.parent_id, depth = EXCLUDED.depth,
                modified_at_source = EXCLUDED.modified_at_source, attributes = EXCLUDED.attributes,
                last_seen_job_id = EXCLUDED.last_seen_job_id, removed_at = NULL
            RETURNING id
            """, ct);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("share", shareId));
        cmd.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter<int>("depth", depth));
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("job", jobId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", rows.Select(_ => Guid.CreateVersion7()).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("paths", rows.Select(r => r.Path).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<byte[][]>("hashes", rows.Select(r => PathHash(r.Path)).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter("modified", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(r => r.Modified).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("attributes", rows.Select(r => r.Attributes).ToArray()));
        var ids = new List<Guid>(rows.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    private async Task UpsertFilesAsync(Guid jobId, Guid shareId, Guid folderId, IReadOnlyList<DiscoveredFile> files, CancellationToken ct)
    {
        await using var cmd = await CommandAsync("""
            INSERT INTO files (id, share_id, folder_id, name, name_key, extension, size, created_at_source, modified_at_source, attributes, first_seen_job_id, last_seen_job_id)
            SELECT u.id, @share, @folder, u.name, u.name_key, u.ext, u.size, u.created, u.modified, u.attributes, @job, @job
            FROM unnest(@ids, @names, @keys, @exts, @sizes, @created, @modified, @attributes) AS u(id, name, name_key, ext, size, created, modified, attributes)
            ON CONFLICT (folder_id, name_key) DO UPDATE SET
                name = EXCLUDED.name, extension = EXCLUDED.extension, size = EXCLUDED.size,
                created_at_source = EXCLUDED.created_at_source, modified_at_source = EXCLUDED.modified_at_source,
                attributes = EXCLUDED.attributes, last_seen_job_id = EXCLUDED.last_seen_job_id, removed_at = NULL
            """, ct);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("share", shareId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("folder", folderId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("job", jobId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", files.Select(_ => Guid.CreateVersion7()).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("names", files.Select(f => f.Name).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("keys", files.Select(f => f.Name.ToUpperInvariant()).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("exts", files.Select(f => PathText.Extension(f.Name)).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<long[]>("sizes", files.Select(f => f.Size).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = files.Select(f => f.CreatedUtc).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("modified", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = files.Select(f => f.ModifiedUtc).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("attributes", files.Select(f => f.Attributes).ToArray()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnqueueAsync(Guid jobId, Guid shareId, int depth, List<Guid> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0)
        {
            return;
        }
        await using var cmd = await CommandAsync("""
            INSERT INTO scan_frontier (job_id, folder_id, share_id, depth)
            SELECT @job, f, @share, @depth FROM unnest(@folders) AS f
            ON CONFLICT (job_id, folder_id) DO NOTHING
            """, ct);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("job", jobId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("share", shareId));
        cmd.Parameters.Add(new NpgsqlParameter<int>("depth", depth));
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("folders", folderIds.ToArray()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbCommand> CommandAsync(string sql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return cmd;
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
