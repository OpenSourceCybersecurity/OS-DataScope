using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>Read-mostly views over the share, folder and file inventory, plus coverage gaps.</summary>
internal static class InventoryEndpoints
{
    public sealed record ShareDto(
        Guid Id, Guid ServerId, string Hostname, string Name, string UncPath, string? Remark, string Kind,
        bool Included, string State, DateTimeOffset? LastScannedAt, Guid? LastScanJobId,
        long FolderCount, long FileCount, long TotalBytes, int GapCount, string? OwnerName,
        string? ExclusionReason = null, string? ExcludedByName = null, DateTimeOffset? ExcludedAt = null,
        string Source = ClassificationEndpoints.FileShareSource);

    /// <param name="Reason">Required when leaving a share out of scans (D124), so others know why later.</param>
    public sealed record UpdateShareRequest(bool? Included, string? Reason = null);

    public sealed record FolderDto(Guid Id, string Name, string Path, int Depth, DateTimeOffset? ModifiedAt, bool Listed, bool Removed);

    public sealed record FileDto(Guid Id, string Name, string Extension, long Size, DateTimeOffset? CreatedAt, DateTimeOffset? ModifiedAt, bool Removed);

    public sealed record GapDto(long Id, Guid JobId, Guid? ShareId, string? ShareName, string Path, string Kind, string Message, DateTimeOffset At);

    public sealed record CoverageDto(
        int Servers, int SharesTotal, int SharesIncluded, int SharesScanned, int SharesPartial, int SharesUnavailable, int SharesNotScanned,
        long Folders, long Files, long Bytes, IReadOnlyDictionary<string, int> GapsByKind);

    // Filter the shares first: EF cannot translate filters applied after projecting into a record constructor.
    private static IQueryable<ShareDto> ShareQuery(DataScopeDbContext db, IQueryable<Share> shares) =>
        from s in shares.AsNoTracking()
        join server in db.ManagedServers on s.ServerId equals server.Id
        select new ShareDto(s.Id, s.ServerId, server.Hostname, s.Name, s.UncPath, s.Remark, s.Kind.ToString(), s.Included,
            s.State.ToString(), s.LastScannedAt, s.LastScanJobId, s.FolderCount, s.FileCount, s.TotalBytes, s.GapCount,
            // The owner of the share itself: the active assignment on its top folder.
            (from a in db.OwnerAssignments
             join f in db.Folders on a.FolderId equals f.Id
             where a.ShareId == s.Id && a.EndedAt == null && f.Depth == 0
             select a.OwnerName).FirstOrDefault(),
            s.ExclusionReason, s.ExcludedByName, s.ExcludedAt,
            ClassificationEndpoints.FileShareSource);

    public static void MapInventory(this RouteGroupBuilder api)
    {
        var shares = api.MapGroup("/shares").RequireAuthorization(Policies.ReadPosture);

        shares.MapGet("/", async (Guid? serverId, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            // Shares of a removed server are left out unless that server is asked for.
            var filtered = db.Shares.Where(s => serverId == null ? s.Server!.State != EnrollmentState.Retired : s.ServerId == serverId);
            var total = await filtered.CountAsync(ct);
            var items = await ShareQuery(db, filtered.OrderBy(s => s.Server!.Hostname).ThenBy(s => s.NameKey).Skip(o).Take(l)).ToListAsync(ct);
            return new Page<ShareDto>(items, l, o, total);
        });

        shares.MapGet("/{id:guid}", async (Guid id, DataScopeDbContext db, CancellationToken ct) =>
            await ShareQuery(db, db.Shares.Where(s => s.Id == id)).FirstOrDefaultAsync(ct) is { } s ? Results.Ok(s) : Results.NotFound());

        shares.MapPatch("/{id:guid}", async (Guid id, UpdateShareRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (share is null)
            {
                return Results.NotFound();
            }
            if (request.Included is not { } included)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["included"] = ["Set included to true or false."] });
            }
            if (included && share.Kind != ShareKind.Disk)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["included"] = [$"{share.Name} is an administrative or non-disk share and is never scanned. Scan the share that holds the data instead."],
                });
            }
            var reason = request.Reason?.Trim();
            if (!included && share.Included && ScanScope.ReasonProblem(reason) is { } reasonProblem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = [reasonProblem] });
            }
            if (share.Included != included)
            {
                var actor = Actor.From(user);
                var now = clock.GetUtcNow();
                var previous = share.ExclusionReason;
                share.Included = included;
                share.UpdatedAt = now;
                share.ExclusionReason = included ? null : reason;
                share.ExcludedAt = included ? null : now;
                share.ExcludedBySid = included ? null : actor.Sid;
                share.ExcludedByName = included ? null : actor.Name;
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await audit.AppendAsync(actor.Sid, actor.Name, included ? "share.include" : "share.exclude", share.UncPath,
                    included ? new { share.Id, PreviousReason = previous } : new { share.Id, Reason = reason, share.FileCount, share.TotalBytes }, ct);
                await tx.CommitAsync(ct);
            }
            return Results.Ok(await ShareQuery(db, db.Shares.Where(s => s.Id == id)).SingleAsync(ct));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Children of a folder; without parentId, the children of the share root.
        shares.MapGet("/{id:guid}/folders", async (Guid id, Guid? parentId, bool? includeRemoved, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            if (!await db.Shares.AnyAsync(s => s.Id == id, ct))
            {
                return Results.NotFound();
            }
            var rootHash = InventoryStore.PathHash("");
            var parent = parentId ?? await db.Folders.Where(f => f.ShareId == id && f.PathHash == rootHash).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return Results.Ok(new Page<FolderDto>([], l, o, 0));
            }
            var query = db.Folders.AsNoTracking().Where(f => f.ShareId == id && f.ParentId == parent && (includeRemoved == true || f.RemovedAt == null));
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(f => f.Path).Skip(o).Take(l).ToListAsync(ct);
            var items = rows.Select(f => new FolderDto(f.Id, LastSegment(f.Path), f.Path, f.Depth, f.ModifiedAtSource, f.LastWalkedJobId != null, f.RemovedAt != null)).ToList();
            return Results.Ok(new Page<FolderDto>(items, l, o, total));
        });

        // Gaps from the share's most recent scan.
        shares.MapGet("/{id:guid}/gaps", async (Guid id, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var share = await db.Shares.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (share is null)
            {
                return Results.NotFound();
            }
            var (l, o) = Paging.Clamp(limit, offset);
            var query = db.ScanGaps.AsNoTracking().Where(g => g.ShareId == id && g.JobId == share.LastScanJobId);
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(g => g.Id).Skip(o).Take(l)
                .Select(g => new GapDto(g.Id, g.JobId, g.ShareId, share.Name, g.Path, g.Kind.ToString(), g.Message, g.At)).ToListAsync(ct);
            return Results.Ok(new Page<GapDto>(items, l, o, total));
        });

        api.MapGet("/folders/{id:guid}/files", async (Guid id, bool? includeRemoved, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (!await db.Folders.AnyAsync(f => f.Id == id, ct))
            {
                return Results.NotFound();
            }
            var (l, o) = Paging.Clamp(limit, offset);
            var query = db.Files.AsNoTracking().Where(f => f.FolderId == id && (includeRemoved == true || f.RemovedAt == null));
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(f => f.NameKey).Skip(o).Take(l)
                .Select(f => new FileDto(f.Id, f.Name, f.Extension, f.Size, f.CreatedAtSource, f.ModifiedAtSource, f.RemovedAt != null)).ToListAsync(ct);
            return Results.Ok(new Page<FileDto>(items, l, o, total));
        }).RequireAuthorization(Policies.ReadPosture);

        api.MapGet("/scan-jobs/{id:guid}/gaps", async (Guid id, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (!await db.ScanJobs.AnyAsync(j => j.Id == id, ct))
            {
                return Results.NotFound();
            }
            var (l, o) = Paging.Clamp(limit, offset);
            var query = from g in db.ScanGaps.AsNoTracking()
                        where g.JobId == id
                        join s in db.Shares on g.ShareId equals s.Id into sj
                        from s in sj.DefaultIfEmpty()
                        select new { g, ShareName = s == null ? null : s.Name };
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(x => x.g.Id).Skip(o).Take(l)
                .Select(x => new GapDto(x.g.Id, x.g.JobId, x.g.ShareId, x.ShareName, x.g.Path, x.g.Kind.ToString(), x.g.Message, x.g.At)).ToListAsync(ct);
            return Results.Ok(new Page<GapDto>(items, l, o, total));
        }).RequireAuthorization(Policies.ReadPosture);

        // What is covered and what is not, across every share, from each share's latest scan.
        // source=files counts only file shares (D146).
        api.MapGet("/coverage", async (string? source, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (!ClassificationEndpoints.TryParseSource(source, out var kind))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = [ClassificationEndpoints.SourceProblem] });
            }
            var list = await db.Shares.AsNoTracking().Where(s => s.State != ShareState.Removed && (kind == null || s.Server!.Kind == kind)).ToListAsync(ct);
            var lastJobs = list.Where(s => s.LastScanJobId != null).Select(s => s.LastScanJobId!.Value).Distinct().ToList();
            var shareIds = list.Select(s => s.Id).ToList();
            var gaps = await db.ScanGaps.AsNoTracking()
                .Where(g => g.ShareId != null && shareIds.Contains(g.ShareId.Value) && lastJobs.Contains(g.JobId))
                .Join(db.Shares, g => g.ShareId, s => s.Id, (g, s) => new { g.Kind, g.JobId, s.LastScanJobId })
                .Where(x => x.JobId == x.LastScanJobId)
                .GroupBy(x => x.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var included = list.Where(s => s.Included).ToList();
            return Results.Ok(new CoverageDto(
                await db.ManagedServers.CountAsync(s => s.State != EnrollmentState.Retired && (kind == null || s.Kind == kind), ct),
                list.Count,
                included.Count,
                included.Count(s => s.State == ShareState.Scanned),
                included.Count(s => s.State == ShareState.Partial),
                included.Count(s => s.State == ShareState.Unavailable),
                included.Count(s => s.State == ShareState.NotScanned),
                list.Sum(s => s.FolderCount),
                list.Sum(s => s.FileCount),
                list.Sum(s => s.TotalBytes),
                gaps.ToDictionary(g => g.Key.ToString(), g => g.Count)));
        }).RequireAuthorization(Policies.ReadPosture);
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }
}
