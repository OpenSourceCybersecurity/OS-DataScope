using DataScope.Api.Access;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Who can reach what, computed from the share and NTFS permissions the last scan read and the latest directory
/// sync. Every answer is a range with a certainty, never a claim that someone opened a file, and never a live Windows
/// access check. Nothing here changes permissions.
/// </summary>
internal static class AccessEndpoints
{
    public sealed record DirectoryRefDto(Guid? SnapshotId, DateTimeOffset? SyncedAt, string? Domain, string LocalGroups);

    public sealed record ShareAccessDto(
        Guid ShareId, Guid ServerId, string Hostname, string Name, string UncPath, PermissionSetDto Share, Guid? RootFolderId,
        RangeDto? OpenAccess, IReadOnlyDictionary<string, long> FolderPermissionStates, long Boundaries, DirectoryRefDto Directory);

    /// <param name="Explicit">Has entries of its own, not just inherited ones: where permissions change.</param>
    /// <param name="Protected">Does not inherit from its parent.</param>
    public sealed record BoundaryDto(
        Guid FolderId, string Path, int Depth, string State, bool? Protected, bool? Explicit, TrusteeDto? Owner, RangeDto OpenAccess);

    public sealed record FolderAccessDto(
        Guid FolderId, Guid ShareId, string ShareName, Guid ServerId, string Hostname, string UncPath, string Path, bool Removed,
        PermissionSetDto Share, PermissionSetDto Folder, RangeDto OpenFolder, RangeDto OpenFiles, int UsersCertain, int UsersPossible,
        DirectoryRefDto Directory);

    public sealed record FileAccessDto(
        Guid FileId, Guid FolderId, string UncPath, RangeDto OpenAccess, int UsersCertain, int UsersPossible,
        IReadOnlyList<UserAccessDto> Users, DirectoryRefDto Directory);

    /// <param name="Status">Open (any domain user can read), PossiblyOpen (can't be ruled out) or Unknown (permissions not read).</param>
    public sealed record ExposureItemDto(
        Guid FolderId, Guid ShareId, string Hostname, string ShareName, string UncPath, string Status, RangeDto OpenAccess,
        int RestrictedFiles, int ConfidentialFiles);

    public sealed record ExposureDto(
        Page<ExposureItemDto> Folders, int FoldersChecked, int Open, int PossiblyOpen, int Unknown, long SensitiveFilesOpen,
        bool Truncated, DirectoryRefDto Directory);

    public sealed record ReachItemDto(
        Guid FolderId, Guid ShareId, string Hostname, string ShareName, string UncPath, string Rights, string UpTo, string Certainty);

    public sealed record ReachDto(UserAccessDto User, string Target, Page<ReachItemDto> Places, long FoldersWithAccess, long FoldersCertain, bool Truncated, DirectoryRefDto Directory);

    /// <summary>Folders with sensitive files considered by one exposure request.</summary>
    internal const int MaxExposureFolders = 20_000;

    /// <summary>Permission boundaries considered by one reach request.</summary>
    private const int MaxReachFolders = 50_000;

    private sealed class ShareRow
    {
        public Guid ShareId { get; init; }
        public required string Name { get; init; }
        public required string UncPath { get; init; }
        public PermissionState State { get; init; }
        public long? SecurityId { get; init; }
        public DateTimeOffset? ReadAt { get; init; }
        public Guid ServerId { get; init; }
        public required string Hostname { get; init; }
        public LocalGroupsState LocalGroupsState { get; init; }
    }

    internal sealed class PlaceRow
    {
        public Guid FolderId { get; init; }
        public required string Path { get; init; }
        public int Depth { get; init; }
        public PermissionState FolderState { get; init; }
        public long? FolderSecurityId { get; init; }
        public Guid ShareId { get; init; }
        public required string ShareName { get; init; }
        public required string UncPath { get; init; }
        public PermissionState ShareState { get; init; }
        public long? ShareSecurityId { get; init; }
        public Guid ServerId { get; init; }
        public required string Hostname { get; init; }
    }

    public static void MapAccess(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/access").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/shares/{id:guid}", async (Guid id, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var share = await ShareAsync(db, id, ct);
            if (share is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(share.ServerId, ct);
            var sd = share.State == PermissionState.Read ? await access.DescriptorAsync(share.SecurityId, ct) : null;
            var root = await db.Folders.AsNoTracking().Where(f => f.ShareId == id && f.Depth == 0 && f.RemovedAt == null)
                .Select(f => new { f.Id, f.SecurityState, f.SecurityId }).FirstOrDefaultAsync(ct);
            RangeDto? open = null;
            if (root is not null)
            {
                var place = await access.PermissionsAsync(share.State, share.SecurityId, root.SecurityState, root.SecurityId, ct);
                open = AccessViews.Range(access.AnyDomainUser(graph, key, place, AccessTarget.Files));
            }
            var states = await db.Folders.Where(f => f.ShareId == id && f.RemovedAt == null)
                .GroupBy(f => f.SecurityState).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
            var boundaries = await Boundaries(db, id).LongCountAsync(ct);
            return Results.Ok(new ShareAccessDto(
                share.ShareId, share.ServerId, share.Hostname, share.Name, share.UncPath,
                AccessViews.Permissions(graph, share.State, share.ReadAt, sd, share: true, countMembers: true),
                root?.Id, open,
                Enum.GetValues<PermissionState>().ToDictionary(s => s.ToString(), s => states.Where(x => x.Key == s).Sum(x => x.Count)),
                boundaries,
                await DirectoryRefAsync(access, share.LocalGroupsState, ct)));
        });

        group.MapGet("/shares/{id:guid}/boundaries", async (Guid id, int? limit, int? offset, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var share = await ShareAsync(db, id, ct);
            if (share is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(share.ServerId, ct);
            var query = Boundaries(db, id);
            var total = await query.CountAsync(ct);
            var page = await query.OrderBy(f => f.Depth == 0 ? 0 : 1).ThenBy(f => f.Path).Skip(o).Take(l)
                .Select(f => new { f.Id, f.Path, f.Depth, f.SecurityState, f.SecurityId }).ToListAsync(ct);
            var items = new List<BoundaryDto>(page.Count);
            foreach (var f in page)
            {
                var place = await access.PermissionsAsync(share.State, share.SecurityId, f.SecurityState, f.SecurityId, ct);
                var sd = place.Permissions.Folder;
                items.Add(new BoundaryDto(
                    f.Id, f.Path, f.Depth, place.Permissions.FolderState.ToString(), sd?.IsProtected, sd?.HasExplicitEntries,
                    AccessViews.Trustee(graph, sd?.Owner), AccessViews.Range(access.AnyDomainUser(graph, key, place, AccessTarget.Files))));
            }
            return Results.Ok(new Page<BoundaryDto>(items, l, o, total));
        });

        group.MapGet("/folders/{id:guid}", async (Guid id, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var folder = await access.FolderAsync(id, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(folder.ServerId, ct);
            var place = await access.PermissionsAsync(folder.ShareState, folder.ShareSecurityId, folder.FolderState, folder.FolderSecurityId, ct);
            var shareReadAt = await db.Shares.Where(s => s.Id == folder.ShareId).Select(s => s.SecurityReadAt).SingleAsync(ct);
            var users = access.WhoHasAccess(graph, key, place, AccessTarget.Files);
            return Results.Ok(new FolderAccessDto(
                folder.FolderId, folder.ShareId, folder.ShareName, folder.ServerId, folder.Hostname, Unc(folder.UncPath, folder.Path), folder.Path,
                folder.Removed,
                AccessViews.Permissions(graph, place.Permissions.ShareState, shareReadAt, place.Permissions.Share, share: true, countMembers: true),
                AccessViews.Permissions(graph, place.Permissions.FolderState, folder.FolderReadAt, place.Permissions.Folder, share: false, countMembers: true),
                AccessViews.Range(access.AnyDomainUser(graph, key, place, AccessTarget.Folder)),
                AccessViews.Range(access.AnyDomainUser(graph, key, place, AccessTarget.Files)),
                users.Count(u => u.Rights > Rights.None),
                users.Count(u => u.Rights == Rights.None),
                await DirectoryRefAsync(access, folder.LocalGroupsState, ct)));
        });

        group.MapGet("/folders/{id:guid}/users", async (
            Guid id, string? target, string? search, string? minRights, bool? certainOnly, int? limit, int? offset, AccessService access, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var errors = new Dictionary<string, string[]>();
            if (!AccessViews.TryTarget(target, out var t))
            {
                errors["target"] = ["Use Files (files in this folder) or Folder (the folder itself)."];
            }
            var min = Rights.None;
            if (minRights is not null && (!Enum.TryParse(minRights, ignoreCase: true, out min) || !Enum.IsDefined(min)))
            {
                errors["minRights"] = [$"Use one of: {string.Join(", ", Enum.GetNames<Rights>())}."];
            }
            if (search is { Length: > 200 })
            {
                errors["search"] = ["Search for at most 200 characters."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var folder = await access.FolderAsync(id, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(folder.ServerId, ct);
            var place = await access.PermissionsAsync(folder.ShareState, folder.ShareSecurityId, folder.FolderState, folder.FolderSecurityId, ct);
            IEnumerable<AccessResult> users = access.WhoHasAccess(graph, key, place, t);
            if (min > Rights.None)
            {
                users = users.Where(u => u.UpTo >= min);
            }
            if (certainOnly == true)
            {
                users = users.Where(u => u.Rights > Rights.None);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                users = users.Where(u => u.UserSid == term || graph.NameOf(u.UserSid).Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            var list = users.ToList();
            return Results.Ok(new Page<UserAccessDto>(list.Skip(o).Take(l).Select(u => AccessViews.User(graph, u)).ToList(), l, o, list.Count));
        });

        group.MapGet("/folders/{id:guid}/explain", async (Guid id, string? sid, string? target, AccessService access, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!AccessViews.TryTarget(target, out var t))
            {
                errors["target"] = ["Use Files (files in this folder) or Folder (the folder itself)."];
            }
            if (sid != AccessViews.AnyUserToken && !Sid.IsValid(sid))
            {
                errors["sid"] = [$"Give the account's SID, or \"{AccessViews.AnyUserToken}\" for any domain user."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var folder = await access.FolderAsync(id, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(folder.ServerId, ct);
            var place = await access.PermissionsAsync(folder.ShareState, folder.ShareSecurityId, folder.FolderState, folder.FolderSecurityId, ct);
            if (sid != AccessViews.AnyUserToken && graph.Find(sid!) is { Kind: not PrincipalKind.User })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Explain access for a user account. To see a group's reach, open its members."] });
            }
            var result = sid == AccessViews.AnyUserToken
                ? access.AnyDomainUser(graph, key, place, t)
                : AccessCalculator.Evaluate(graph, sid!, place.Permissions, t);
            return Results.Ok(AccessViews.Explain(graph, result, t));
        });

        api.MapGet("/files/{id:guid}/access", async (Guid id, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var file = await db.Files.AsNoTracking().Where(f => f.Id == id).Select(f => new { f.Id, f.FolderId, f.Name }).SingleOrDefaultAsync(ct);
            if (file is null)
            {
                return Results.NotFound();
            }
            var folder = await access.FolderAsync(file.FolderId, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            var (graph, key) = await access.GraphAsync(folder.ServerId, ct);
            var place = await access.PermissionsAsync(folder.ShareState, folder.ShareSecurityId, folder.FolderState, folder.FolderSecurityId, ct);
            var users = access.WhoHasAccess(graph, key, place, AccessTarget.Files);
            return Results.Ok(new FileAccessDto(
                file.Id, file.FolderId, Unc(folder.UncPath, folder.Path, file.Name),
                AccessViews.Range(access.AnyDomainUser(graph, key, place, AccessTarget.Files)),
                users.Count(u => u.Rights > Rights.None), users.Count(u => u.Rights == Rights.None),
                users.Take(25).Select(u => AccessViews.User(graph, u)).ToList(),
                await DirectoryRefAsync(access, folder.LocalGroupsState, ct)));
        }).RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/exposure", async (
            Guid? serverId, Guid? shareId, string? minSensitivity, string? status, int? limit, int? offset,
            DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var errors = new Dictionary<string, string[]>();
            var min = Sensitivity.Confidential;
            if (minSensitivity is not null && (!Enum.TryParse(minSensitivity, ignoreCase: true, out min) || min < Sensitivity.Confidential || !Enum.IsDefined(min)))
            {
                errors["minSensitivity"] = ["Use Confidential or Restricted."];
            }
            if (status is not null && status is not ("Open" or "PossiblyOpen" or "Unknown"))
            {
                errors["status"] = ["Use Open, PossiblyOpen or Unknown."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var exposure = await ExposureAsync(db, access, min, serverId, shareId, ct);
            var items = exposure.Items;
            var filtered = status is null ? items : items.Where(i => i.Status == status).ToList();
            return Results.Ok(new ExposureDto(
                new Page<ExposureItemDto>(filtered.Skip(o).Take(l).ToList(), l, o, filtered.Count),
                exposure.FoldersChecked,
                items.Count(i => i.Status == "Open"),
                items.Count(i => i.Status == "PossiblyOpen"),
                items.Count(i => i.Status == "Unknown"),
                exposure.SensitiveFilesOpen,
                exposure.Truncated,
                await DirectoryRefAsync(access, null, ct)));
        });

        group.MapGet("/principals/{sid}/reach", async (
            string sid, string? target, Guid? serverId, int? limit, int? offset, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var errors = new Dictionary<string, string[]>();
            if (!AccessViews.TryTarget(target, out var t))
            {
                errors["target"] = ["Use Files (files in this folder) or Folder (the folder itself)."];
            }
            if (sid != AccessViews.AnyUserToken && !Sid.IsValid(sid))
            {
                errors["sid"] = [$"Give the account's SID, or \"{AccessViews.AnyUserToken}\" for any domain user."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var (directoryGraph, _) = await access.GraphAsync(null, ct);
            if (sid != AccessViews.AnyUserToken && directoryGraph.Find(sid) is { Kind: not PrincipalKind.User })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Show reach for a user account. A group's reach is the reach of its members."] });
            }

            // Every folder's access depends only on its share's and its own permissions, so evaluate each distinct pair once.
            var folders = db.Folders.Where(f => f.RemovedAt == null);
            if (serverId is { } sv)
            {
                folders = folders.Where(f => db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == sv));
            }
            var combos = await (from f in folders
                                join s in db.Shares on f.ShareId equals s.Id
                                group f by new { s.ServerId, ShareState = s.SecurityState, ShareSecurityId = s.SecurityId, f.SecurityState, f.SecurityId } into g
                                select new { g.Key.ServerId, g.Key.ShareState, g.Key.ShareSecurityId, FolderState = g.Key.SecurityState, FolderSecurityId = g.Key.SecurityId, Count = g.LongCount() })
                .ToListAsync(ct);
            var results = new Dictionary<(Guid, string), AccessResult>();
            UserAccessDto? user = null;
            long withAccess = 0, certain = 0;
            foreach (var c in combos)
            {
                var (graph, key) = await access.GraphAsync(c.ServerId, ct);
                var place = await access.PermissionsAsync(c.ShareState, c.ShareSecurityId, c.FolderState, c.FolderSecurityId, ct);
                var result = sid == AccessViews.AnyUserToken
                    ? access.AnyDomainUser(graph, key, place, t)
                    : AccessCalculator.Evaluate(graph, sid, place.Permissions, t);
                user ??= AccessViews.User(graph, result);
                results[(c.ServerId, place.Key)] = result;
                if (result.UpTo > Rights.None && result.Certainty != Certainty.Unknown)
                {
                    withAccess += c.Count;
                    certain += result.Rights > Rights.None ? c.Count : 0;
                }
            }
            user ??= sid == AccessViews.AnyUserToken
                ? new UserAccessDto(directoryGraph.AnyDomainUserSid, "Any domain user", true, "No access", "No access", nameof(Certainty.Likely), null)
                : new UserAccessDto(sid, directoryGraph.NameOf(sid), directoryGraph.Find(sid)?.Enabled ?? true, "No access", "No access", nameof(Certainty.Likely), null);

            // List the places where permissions are set (share roots and folders with their own entries), not every folder below them.
            var boundaryFolders = folders.Where(f => f.Depth == 0 || f.SecurityState != PermissionState.Read
                || db.SecurityDescriptors.Any(d => d.Id == f.SecurityId && (d.IsProtected || d.HasExplicitEntries)));
            var rows = await Places(db, boundaryFolders).OrderBy(p => p.Hostname).ThenBy(p => p.ShareName).ThenBy(p => p.Path)
                .Take(MaxReachFolders + 1).ToListAsync(ct);
            var truncated = rows.Count > MaxReachFolders;
            var items = new List<ReachItemDto>();
            foreach (var p in rows.Take(MaxReachFolders))
            {
                var place = await access.PermissionsAsync(p.ShareState, p.ShareSecurityId, p.FolderState, p.FolderSecurityId, ct);
                if (results.TryGetValue((p.ServerId, place.Key), out var r) && r.UpTo > Rights.None && r.Certainty != Certainty.Unknown)
                {
                    items.Add(new ReachItemDto(p.FolderId, p.ShareId, p.Hostname, p.ShareName, Unc(p.UncPath, p.Path),
                        AccessViews.Label(r.Rights), AccessViews.Label(r.UpTo), r.Certainty.ToString()));
                }
            }
            return Results.Ok(new ReachDto(user, t.ToString(), new Page<ReachItemDto>(items.Skip(o).Take(l).ToList(), l, o, items.Count),
                withAccess, certain, truncated, await DirectoryRefAsync(access, null, ct)));
        });
    }

    /// <param name="Items">Folders that are open, possibly open or unknown, most exposed first.</param>
    /// <param name="FoldersChecked">Folders with sensitive files that were evaluated.</param>
    internal sealed record ExposureResult(IReadOnlyList<ExposureItemDto> Items, int FoldersChecked, long SensitiveFilesOpen, bool Truncated);

    /// <summary>
    /// Folders holding files labelled at least <paramref name="min"/> that any domain user can, or might, read. Shared by
    /// the exposure endpoint, the dashboard and the risky permissions report so they always agree.
    /// </summary>
    internal static async Task<ExposureResult> ExposureAsync(
        DataScopeDbContext db, AccessService access, Sensitivity min, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        var labels = Enum.GetValues<Sensitivity>().Where(s => s >= min).ToList();
        // File servers only (D155): the NTFS permissions are what is counted.
        var files = db.Files.Where(f => f.RemovedAt == null && labels.Contains(f.OverrideSensitivity ?? f.Sensitivity)
            && db.Shares.Any(s => s.Id == f.ShareId && s.Server!.Kind == SourceKind.FileServer));
        if (shareId is { } sh)
        {
            files = files.Where(f => f.ShareId == sh);
        }
        if (serverId is { } sv)
        {
            files = files.Where(f => db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == sv));
        }
        var counts = await files
            .GroupBy(f => f.FolderId)
            .Select(g => new
            {
                FolderId = g.Key,
                Restricted = g.Count(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Restricted),
                Total = g.Count(),
            })
            .OrderByDescending(x => x.Restricted).ThenByDescending(x => x.Total)
            .Take(MaxExposureFolders + 1)
            .ToListAsync(ct);
        var truncated = counts.Count > MaxExposureFolders;
        if (truncated)
        {
            counts.RemoveAt(counts.Count - 1);
        }
        var ids = counts.Select(c => c.FolderId).ToList();
        var places = await Places(db, db.Folders.Where(f => ids.Contains(f.Id) && f.RemovedAt == null)).ToDictionaryAsync(p => p.FolderId, ct);

        var items = new List<ExposureItemDto>();
        long openFiles = 0;
        foreach (var c in counts)
        {
            if (!places.TryGetValue(c.FolderId, out var p))
            {
                continue;
            }
            var (graph, key) = await access.GraphAsync(p.ServerId, ct);
            var place = await access.PermissionsAsync(p.ShareState, p.ShareSecurityId, p.FolderState, p.FolderSecurityId, ct);
            var any = access.AnyDomainUser(graph, key, place, AccessTarget.Files);
            var state = any.Certainty == Certainty.Unknown ? "Unknown"
                : any.CanRead ? "Open"
                : any.MightRead ? "PossiblyOpen"
                : null;
            if (state is null)
            {
                continue;
            }
            if (state == "Open")
            {
                openFiles += c.Total;
            }
            items.Add(new ExposureItemDto(p.FolderId, p.ShareId, p.Hostname, p.ShareName, Unc(p.UncPath, p.Path), state,
                AccessViews.Range(any), c.Restricted, c.Total - c.Restricted));
        }
        var ordered = items
            .OrderBy(i => i.Status switch { "Open" => 0, "PossiblyOpen" => 1, _ => 2 })
            .ThenByDescending(i => i.RestrictedFiles).ThenByDescending(i => i.ConfidentialFiles).ThenBy(i => i.UncPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ExposureResult(ordered, places.Count, openFiles, truncated);
    }

    /// <summary>
    /// The places where permissions are set (share roots, and folders with their own entries or unread permissions) in
    /// scope, ordered by server, share and path. At most <paramref name="max"/> plus one, so callers can tell when there
    /// were more.
    /// </summary>
    internal static Task<List<PlaceRow>> BoundaryPlacesAsync(DataScopeDbContext db, Guid? serverId, Guid? shareId, int max, CancellationToken ct)
    {
        var folders = db.Folders.Where(f => f.RemovedAt == null
            && (f.Depth == 0 || f.SecurityState != PermissionState.Read
                || db.SecurityDescriptors.Any(d => d.Id == f.SecurityId && (d.IsProtected || d.HasExplicitEntries))));
        if (shareId is { } sh)
        {
            folders = folders.Where(f => f.ShareId == sh);
        }
        if (serverId is { } sv)
        {
            folders = folders.Where(f => db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == sv));
        }
        return Places(db, folders).OrderBy(p => p.Hostname).ThenBy(p => p.ShareName).ThenBy(p => p.Path).Take(max + 1).ToListAsync(ct);
    }

    internal static string UncOf(string shareUnc, string path, string? file = null) => Unc(shareUnc, path, file);

    private static IQueryable<Folder> Boundaries(DataScopeDbContext db, Guid shareId) =>
        db.Folders.AsNoTracking().Where(f => f.ShareId == shareId && f.RemovedAt == null
            && (f.Depth == 0 || f.SecurityState != PermissionState.Read
                || db.SecurityDescriptors.Any(d => d.Id == f.SecurityId && (d.IsProtected || d.HasExplicitEntries))));

    internal static IQueryable<PlaceRow> Places(DataScopeDbContext db, IQueryable<Folder> folders) =>
        from f in folders.AsNoTracking()
        join s in db.Shares on f.ShareId equals s.Id
        join m in db.ManagedServers on s.ServerId equals m.Id
        select new PlaceRow
        {
            FolderId = f.Id,
            Path = f.Path,
            Depth = f.Depth,
            FolderState = f.SecurityState,
            FolderSecurityId = f.SecurityId,
            ShareId = s.Id,
            ShareName = s.Name,
            UncPath = s.UncPath,
            ShareState = s.SecurityState,
            ShareSecurityId = s.SecurityId,
            ServerId = m.Id,
            Hostname = m.Hostname,
        };

    private static Task<ShareRow?> ShareAsync(DataScopeDbContext db, Guid id, CancellationToken ct) =>
        (from s in db.Shares.AsNoTracking()
         join m in db.ManagedServers on s.ServerId equals m.Id
         where s.Id == id
         select new ShareRow
         {
             ShareId = s.Id,
             Name = s.Name,
             UncPath = s.UncPath,
             State = s.SecurityState,
             SecurityId = s.SecurityId,
             ReadAt = s.SecurityReadAt,
             ServerId = m.Id,
             Hostname = m.Hostname,
             LocalGroupsState = m.LocalGroupsState,
         }).SingleOrDefaultAsync(ct);

    internal static async Task<DirectoryRefDto> DirectoryRefAsync(AccessService access, LocalGroupsState? localGroups, CancellationToken ct)
    {
        var snapshot = await access.SnapshotAsync(ct);
        return new DirectoryRefDto(snapshot?.Id, snapshot?.CompletedAt, snapshot?.Domain, localGroups?.ToString() ?? "NotApplicable");
    }

    private static string Unc(string shareUnc, string path, string? file = null)
    {
        var folder = path.Length == 0 ? shareUnc : shareUnc.TrimEnd('\\') + "\\" + path;
        return file is null ? folder : folder.TrimEnd('\\') + "\\" + file;
    }
}
