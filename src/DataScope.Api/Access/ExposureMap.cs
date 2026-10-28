using DataScope.Api.Compliance;
using DataScope.Api.Endpoints;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Access;

/// <summary>Who can read one sensitive place, worked out once per distinct set of share and folder permissions.</summary>
/// <param name="Readers">Enabled accounts that can, or might, read files there. Empty when the permissions are unknown.</param>
/// <param name="Disabled">Disabled accounts whose permissions would let them read here.</param>
internal sealed record PlaceExposure(AccessResult AnyDomainUser, IReadOnlyList<Reader> Readers, IReadOnlyList<string> Disabled)
{
    public bool Unknown => AnyDomainUser.Certainty == Certainty.Unknown;
}

/// <summary>One account that can (or might) read files in a place, and the permission entries that give it that.</summary>
/// <param name="Certain">Data Scope can show the account reads files here; otherwise it only might.</param>
/// <param name="Grants">Trustees of every NTFS allow entry that lets the account read, with whether membership is certain.</param>
internal sealed record Reader(string Sid, Rights Rights, Rights UpTo, bool Certain, IReadOnlyList<(string Trustee, bool Certain, Rights Rights)> Grants);

/// <summary>A folder holding Confidential or Restricted files, and who can read them.</summary>
internal sealed record SensitiveFolder(AccessEndpoints.PlaceRow Place, int Restricted, int Confidential, PlaceExposure Exposure)
{
    public int Files => Restricted + Confidential;

    public string Unc => AccessEndpoints.UncOf(Place.UncPath, Place.Path);

    /// <summary>Open, PossiblyOpen or Unknown for any domain user; Limited when only some people can read it.</summary>
    public string Status => Exposure.Unknown ? "Unknown"
        : Exposure.AnyDomainUser.CanRead ? "Open"
        : Exposure.AnyDomainUser.MightRead ? "PossiblyOpen"
        : "Limited";

    public int PeopleCertain => Exposure.Readers.Count(r => r.Certain);

    public int PeoplePossible => Exposure.Readers.Count(r => !r.Certain);
}

/// <summary>Per-share coverage that tells how far the sensitive data and access shown can be trusted.</summary>
internal sealed record ShareCoverage(Guid ShareId, Guid ServerId, string Hostname, string Name, string UncPath, bool Included, ShareState State,
    PermissionState Permissions, long FilesNotRead, long FilesExcluded);

/// <summary>
/// Sensitive Data Exposure Map (D125): Confidential and Restricted files by folder, who can read them, through which
/// permission entries, and what limits the answer. Built from the same access engine, caches and sensitive-file
/// counts as Who has access, the exposure list and Risks, so the numbers agree. Computed, not a Windows access check.
/// </summary>
internal sealed class ExposureMap
{
    public required IReadOnlyList<SensitiveFolder> Folders { get; init; }
    public required IReadOnlyList<ShareCoverage> Shares { get; init; }
    public required Func<Guid, DirectoryGraph> GraphOf { get; init; }
    public required DirectoryGraph Directory { get; init; }
    public bool Truncated { get; init; }

    public static async Task<ExposureMap> BuildAsync(DataScopeDbContext db, AccessService access, Sensitivity min, Guid? serverId, Guid? shareId,
        EffectiveStandard? standard, CancellationToken ct)
    {
        var labels = Enum.GetValues<Sensitivity>().Where(s => s >= min).ToList();
        // File servers only (D155): the NTFS permissions are what is drawn.
        var files = db.Files.Where(f => f.RemovedAt == null && labels.Contains(f.OverrideSensitivity ?? f.Sensitivity)
            && db.Shares.Any(s => s.Id == f.ShareId && s.Server!.Kind == SourceKind.FileServer));
        if (standard is not null)
        {
            // Only the files under this compliance standard (D126); coverage below still counts every file not read.
            files = ComplianceScope.Covered(db, files, standard);
        }
        var shares = db.Shares.Where(s => s.Kind == ShareKind.Disk && s.State != ShareState.Removed && s.Server!.Kind == SourceKind.FileServer);
        if (shareId is { } sh)
        {
            files = files.Where(f => f.ShareId == sh);
            shares = shares.Where(s => s.Id == sh);
        }
        if (serverId is { } sv)
        {
            files = files.Where(f => db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == sv));
            shares = shares.Where(s => s.ServerId == sv);
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
            .Take(AccessEndpoints.MaxExposureFolders + 1)
            .ToListAsync(ct);
        var truncated = counts.Count > AccessEndpoints.MaxExposureFolders;
        if (truncated)
        {
            counts.RemoveAt(counts.Count - 1);
        }
        var ids = counts.Select(c => c.FolderId).ToList();
        var places = await AccessEndpoints.Places(db, db.Folders.Where(f => ids.Contains(f.Id) && f.RemovedAt == null)).ToDictionaryAsync(p => p.FolderId, ct);

        var graphs = new Dictionary<Guid, DirectoryGraph>();
        var evaluated = new Dictionary<(Guid, string), PlaceExposure>();
        var folders = new List<SensitiveFolder>(counts.Count);
        foreach (var c in counts)
        {
            if (!places.TryGetValue(c.FolderId, out var p))
            {
                continue;
            }
            var (graph, key) = await access.GraphAsync(p.ServerId, ct);
            graphs[p.ServerId] = graph;
            var place = await access.PermissionsAsync(p.ShareState, p.ShareSecurityId, p.FolderState, p.FolderSecurityId, ct);
            if (!evaluated.TryGetValue((p.ServerId, place.Key), out var exposure))
            {
                exposure = Evaluate(access, graph, key, place);
                evaluated[(p.ServerId, place.Key)] = exposure;
            }
            folders.Add(new SensitiveFolder(p, c.Restricted, c.Total - c.Restricted, exposure));
        }

        var notRead = await db.Files
            .Where(f => f.RemovedAt == null && f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial && shares.Any(s => s.Id == f.ShareId))
            .GroupBy(f => f.ShareId)
            .Select(g => new { ShareId = g.Key, NotRead = g.LongCount(), Excluded = g.LongCount(f => f.ContentState == ContentState.Excluded) })
            .ToDictionaryAsync(x => x.ShareId, ct);
        var shareRows = await (from s in shares
                               join m in db.ManagedServers on s.ServerId equals m.Id
                               where m.State != EnrollmentState.Retired
                               orderby m.Hostname, s.NameKey
                               select new { s.Id, s.ServerId, m.Hostname, s.Name, s.UncPath, s.Included, s.State, s.SecurityState }).ToListAsync(ct);
        var coverage = shareRows.Select(s => new ShareCoverage(s.Id, s.ServerId, s.Hostname, s.Name, s.UncPath, s.Included, s.State, s.SecurityState,
            notRead.GetValueOrDefault(s.Id)?.NotRead ?? 0, notRead.GetValueOrDefault(s.Id)?.Excluded ?? 0)).ToList();

        var (directory, _) = await access.GraphAsync(null, ct);
        return new ExposureMap
        {
            Folders = folders,
            Shares = coverage,
            Directory = directory,
            GraphOf = id => graphs.GetValueOrDefault(id) ?? directory,
            Truncated = truncated,
        };
    }

    /// <summary>Readers of one place and the entries that let them read, from the cached results for that permission set.</summary>
    private static PlaceExposure Evaluate(AccessService access, DirectoryGraph graph, string key, PlacePermissions place)
    {
        var any = access.AnyDomainUser(graph, key, place, AccessTarget.Files);
        if (any.Certainty == Certainty.Unknown)
        {
            // Without the folder's permissions anyone might read it; listing every account would say nothing.
            return new PlaceExposure(any, [], []);
        }
        var folderAcl = place.Permissions.Folder;
        var readers = new List<Reader>();
        var disabled = new List<string>();
        foreach (var r in access.WhoHasAccess(graph, key, place, AccessTarget.Files))
        {
            if (!r.MightRead)
            {
                continue;
            }
            if (graph.Find(r.UserSid) is { Enabled: false })
            {
                disabled.Add(r.UserSid);
                continue;
            }
            // Every entry that lets this account read, not only the first one Windows would use: removing one of
            // several still leaves the others, and the map should show each group that opens the place.
            var grants = folderAcl is null ? [] : AccessCalculator.ReadGrants(folderAcl, AccessTarget.Files, graph.TokenFor(r.UserSid))
                .GroupBy(x => x.Ace.Sid!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Trustee: g.Key, Certain: g.Any(x => x.Match == Membership.Yes), Rights: g.Max(x => AccessMask.ToRights(x.Ace.Mask))))
                .ToList();
            readers.Add(new Reader(r.UserSid, r.Rights, r.UpTo, r.CanRead, grants));
        }
        return new PlaceExposure(any, readers, disabled);
    }
}
