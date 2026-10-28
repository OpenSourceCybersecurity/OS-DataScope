using DataScope.Api.Access;
using DataScope.Api.Compliance;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Sensitive Data Exposure Map (D125): where Confidential and Restricted files are, who can read them, through which
/// permission entries, and what limits each answer. Read-only, computed from the latest scans and directory sync.
/// </summary>
internal static class ExposureMapEndpoints
{
    /// <param name="People">Enabled accounts that can read at least one sensitive file; <c>PeoplePossible</c> might.</param>
    /// <param name="OpenFiles">Sensitive files any domain user can read.</param>
    public sealed record SummaryDto(int SensitiveFolders, long RestrictedFiles, long ConfidentialFiles, int OpenFolders, int PossiblyOpenFolders, int UnknownFolders,
        int LimitedFolders, long OpenFiles, int People, int PeoplePossible, int Groups);

    /// <summary>Everything that limits how far the map can be trusted.</summary>
    /// <param name="FilesNotRead">Files on scanned shares whose contents were not read, so sensitive data there is unknown.</param>
    /// <param name="FilesExcluded">Of those, files an administrator excluded from reading.</param>
    /// <param name="SharesNotFullyScanned">Scanned shares whose last scan was partial, failed or has not run.</param>
    /// <param name="SharesLeftOut">Shares left out of scans; anything shown for them is as last seen.</param>
    /// <param name="SharesPermissionsNotRead">Scanned shares whose share permissions could not be read, so access is a range.</param>
    /// <param name="FoldersUnknown">Sensitive folders whose permissions could not be read: who can read them is unknown.</param>
    /// <param name="FoldersUncertain">Sensitive folders where some people only might be able to read.</param>
    /// <param name="DisabledAccounts">Disabled accounts whose permissions would let them read sensitive files; not counted as people.</param>
    /// <param name="Truncated">More sensitive folders than the map evaluates; the busiest were kept.</param>
    public sealed record CoverageDto(long FilesNotRead, long FilesExcluded, int SharesNotFullyScanned, int SharesLeftOut, int SharesPermissionsNotRead,
        int FoldersUnknown, int FoldersUncertain, int DisabledAccounts, bool Truncated, int FolderLimit);

    public sealed record ShareDto(Guid ShareId, Guid ServerId, string Hostname, string Name, string UncPath, bool Included, string State, string SharePermissions,
        long Restricted, long Confidential, int SensitiveFolders, int OpenFolders, int PossiblyOpenFolders, int UnknownFolders, int People, int PeoplePossible,
        long FilesNotRead);

    /// <summary>A permission entry that lets people read a folder's files, and how many it reaches there.</summary>
    public sealed record GrantDto(TrusteeDto Trustee, string Rights, int People, int PeoplePossible);

    /// <param name="Status">Open, PossiblyOpen or Unknown for any domain user, or Limited when only some people can read.</param>
    public sealed record FolderDto(Guid FolderId, Guid ShareId, Guid ServerId, string Hostname, string ShareName, string UncPath, int Restricted, int Confidential,
        string Status, RangeDto AnyDomainUser, int People, int PeoplePossible, IReadOnlyList<GrantDto> Grants);

    /// <param name="People">Enabled accounts this entry certainly lets read sensitive files; <c>PeoplePossible</c> might.</param>
    /// <param name="MembersCertain">Everyone the trustee includes, from the directory, whatever they can read.</param>
    public sealed record GroupDto(TrusteeDto Trustee, int People, int PeoplePossible, int? MembersCertain, int? MembersPossible, int Folders, int Shares,
        long Restricted, long Confidential, string HighestRights);

    /// <param name="Restricted">Restricted files the account can read; <c>PossibleFiles</c> are ones it only might.</param>
    /// <param name="Via">The trustees that most often give it access, most often first.</param>
    public sealed record PersonDto(string Sid, string Name, long Restricted, long Confidential, long PossibleFiles, int Folders, int Shares, IReadOnlyList<string> Via);

    public sealed record MapDto(SummaryDto Summary, CoverageDto Coverage, IReadOnlyList<ShareDto> Shares, IReadOnlyList<FolderDto> Folders, int FoldersTotal,
        IReadOnlyList<GroupDto> Groups, int GroupsTotal, IReadOnlyList<PersonDto> People, int PeopleTotal, string Minimum, AccessEndpoints.DirectoryRefDto Directory,
        string? Standard);

    public sealed record ViaDto(TrusteeDto Trustee, bool Certain, string Rights);

    public sealed record PersonFolderDto(Guid FolderId, Guid ShareId, string UncPath, int Restricted, int Confidential, string Status, string Rights, string UpTo,
        bool Certain, IReadOnlyList<ViaDto> Via);

    public sealed record PersonDetailDto(UserAccessDto Account, PersonDto? Totals, IReadOnlyList<PersonFolderDto> Folders, int FoldersTotal, AccessEndpoints.DirectoryRefDto Directory);

    public sealed record GroupFolderDto(Guid FolderId, Guid ShareId, string UncPath, int Restricted, int Confidential, string Status, string Rights, int People, int PeoplePossible);

    public sealed record GroupDetailDto(TrusteeDto Trustee, GroupDto? Totals, IReadOnlyList<GroupFolderDto> Folders, int FoldersTotal, AccessEndpoints.DirectoryRefDto Directory);

    internal const int MaxFolders = 500;
    internal const int MaxGroups = 100;
    internal const int MaxPeople = 200;
    private const int MaxGrantsPerFolder = 6;

    public static void MapExposureMap(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/exposure-map").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/", async (Guid? serverId, Guid? shareId, string? minSensitivity, string? standard, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            if (!TryMinimum(minSensitivity, out var min))
            {
                return BadMinimum();
            }
            var (std, standardProblem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (standardProblem is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["standard"] = [standardProblem] });
            }
            var map = await ExposureMap.BuildAsync(db, access, min, serverId, shareId, std, ct);
            var groups = Groups(map);
            var people = People(map);
            var folders = map.Folders
                .OrderByDescending(f => f.Status == "Open").ThenByDescending(f => f.PeopleCertain).ThenByDescending(f => f.Restricted).ThenByDescending(f => f.Files)
                .ThenBy(f => f.Unc, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(new MapDto(
                Summary(map, groups.Count),
                Coverage(map),
                Shares(map),
                folders.Take(MaxFolders).Select(f => Folder(map, f)).ToList(), folders.Count,
                groups.Take(MaxGroups).ToList(), groups.Count,
                people.Take(MaxPeople).ToList(), people.Count,
                min.ToString(),
                await AccessEndpoints.DirectoryRefAsync(access, null, ct),
                std?.Id));
        });

        // One account: every sensitive folder it can or might read, and the entries that let it.
        group.MapGet("/people/{sid}", async (string sid, Guid? serverId, Guid? shareId, string? minSensitivity, string? standard, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            if (!Sid.IsValid(sid))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Give the account's SID."] });
            }
            if (!TryMinimum(minSensitivity, out var min))
            {
                return BadMinimum();
            }
            var (std, standardProblem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (standardProblem is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["standard"] = [standardProblem] });
            }
            var map = await ExposureMap.BuildAsync(db, access, min, serverId, shareId, std, ct);
            if (map.Directory.Find(sid) is { Kind: not PrincipalKind.User })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["This is a group. Open it under Groups to see what it gives access to."] });
            }
            var rows = new List<PersonFolderDto>();
            Rights best = Rights.None, bestUpTo = Rights.None;
            foreach (var f in map.Folders)
            {
                if (f.Exposure.Readers.FirstOrDefault(r => Same(r.Sid, sid)) is not { } reader)
                {
                    continue;
                }
                var graph = map.GraphOf(f.Place.ServerId);
                best = (Rights)Math.Max((int)best, (int)reader.Rights);
                bestUpTo = (Rights)Math.Max((int)bestUpTo, (int)reader.UpTo);
                rows.Add(new PersonFolderDto(f.Place.FolderId, f.Place.ShareId, f.Unc, f.Restricted, f.Confidential, f.Status,
                    AccessViews.Label(reader.Rights), AccessViews.Label(reader.UpTo), reader.Certain,
                    reader.Grants.Select(g => new ViaDto(AccessViews.Trustee(graph, g.Trustee)!, g.Certain, AccessViews.Label(g.Rights))).ToList()));
            }
            var ordered = rows.OrderByDescending(r => r.Certain).ThenByDescending(r => r.Restricted).ThenByDescending(r => r.Restricted + r.Confidential)
                .ThenBy(r => r.UncPath, StringComparer.OrdinalIgnoreCase).ToList();
            var entry = map.Directory.Find(sid);
            var account = new UserAccessDto(sid, map.Directory.NameOf(sid), entry?.Enabled ?? true, AccessViews.Label(best), AccessViews.Label(bestUpTo),
                rows.All(r => r.Certain) ? nameof(Certainty.Likely) : nameof(Certainty.Partial), null);
            return Results.Ok(new PersonDetailDto(account, People(map).FirstOrDefault(p => p.Sid == sid),
                ordered.Take(MaxFolders).ToList(), ordered.Count, await AccessEndpoints.DirectoryRefAsync(access, null, ct)));
        });

        // One trustee (usually a group): every sensitive folder where one of its entries lets people read, and how many.
        group.MapGet("/groups/{sid}", async (string sid, Guid? serverId, Guid? shareId, string? minSensitivity, string? standard, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            if (!Sid.IsValid(sid))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Give the group's SID."] });
            }
            if (!TryMinimum(minSensitivity, out var min))
            {
                return BadMinimum();
            }
            var (std, standardProblem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (standardProblem is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["standard"] = [standardProblem] });
            }
            var map = await ExposureMap.BuildAsync(db, access, min, serverId, shareId, std, ct);
            var rows = new List<GroupFolderDto>();
            foreach (var f in map.Folders)
            {
                var via = f.Exposure.Readers.SelectMany(r => r.Grants.Where(g => Same(g.Trustee, sid)).Select(g => (Reader: r, Grant: g))).ToList();
                if (via.Count == 0)
                {
                    continue;
                }
                var certain = via.Count(v => v.Reader.Certain && v.Grant.Certain);
                rows.Add(new GroupFolderDto(f.Place.FolderId, f.Place.ShareId, f.Unc, f.Restricted, f.Confidential, f.Status,
                    AccessViews.Label(via.Max(v => v.Grant.Rights)), certain, via.Count - certain));
            }
            var ordered = rows.OrderByDescending(r => r.People).ThenByDescending(r => r.Restricted).ThenBy(r => r.UncPath, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new GroupDetailDto(AccessViews.Trustee(map.Directory, sid)!, Groups(map).FirstOrDefault(g => Same(g.Trustee.Sid, sid)),
                ordered.Take(MaxFolders).ToList(), ordered.Count, await AccessEndpoints.DirectoryRefAsync(access, null, ct)));
        });
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryMinimum(string? text, out Sensitivity min)
    {
        min = Sensitivity.Confidential;
        return text is null || (Enum.TryParse(text, ignoreCase: true, out min) && min is Sensitivity.Confidential or Sensitivity.Restricted);
    }

    private static IResult BadMinimum() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["minSensitivity"] = ["Use Confidential or Restricted."] });

    private static SummaryDto Summary(ExposureMap map, int groups)
    {
        var certain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var possible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in map.Folders.SelectMany(f => f.Exposure.Readers))
        {
            (r.Certain ? certain : possible).Add(r.Sid);
        }
        possible.ExceptWith(certain);
        return new SummaryDto(
            map.Folders.Count,
            map.Folders.Sum(f => (long)f.Restricted),
            map.Folders.Sum(f => (long)f.Confidential),
            map.Folders.Count(f => f.Status == "Open"),
            map.Folders.Count(f => f.Status == "PossiblyOpen"),
            map.Folders.Count(f => f.Status == "Unknown"),
            map.Folders.Count(f => f.Status == "Limited"),
            map.Folders.Where(f => f.Status == "Open").Sum(f => (long)f.Files),
            certain.Count, possible.Count, groups);
    }

    private static CoverageDto Coverage(ExposureMap map)
    {
        var scanned = map.Shares.Where(s => s.Included).ToList();
        return new CoverageDto(
            scanned.Sum(s => s.FilesNotRead),
            scanned.Sum(s => s.FilesExcluded),
            scanned.Count(s => s.State is ShareState.Partial or ShareState.Unavailable or ShareState.NotScanned),
            map.Shares.Count(s => !s.Included),
            scanned.Count(s => s.Permissions != PermissionState.Read),
            map.Folders.Count(f => f.Status == "Unknown"),
            map.Folders.Count(f => f.Status != "Unknown" && (f.PeoplePossible > 0 || f.Exposure.AnyDomainUser.Certainty == Certainty.Partial)),
            map.Folders.SelectMany(f => f.Exposure.Disabled).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            map.Truncated,
            AccessEndpoints.MaxExposureFolders);
    }

    private static List<ShareDto> Shares(ExposureMap map)
    {
        var byShare = map.Folders.GroupBy(f => f.Place.ShareId).ToDictionary(g => g.Key, g => g.ToList());
        return map.Shares.Select(s =>
        {
            var folders = byShare.GetValueOrDefault(s.ShareId) ?? [];
            var certain = folders.SelectMany(f => f.Exposure.Readers).Where(r => r.Certain).Select(r => r.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var possible = folders.SelectMany(f => f.Exposure.Readers).Where(r => !r.Certain && !certain.Contains(r.Sid)).Select(r => r.Sid)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new ShareDto(s.ShareId, s.ServerId, s.Hostname, s.Name, s.UncPath, s.Included, s.State.ToString(), s.Permissions.ToString(),
                folders.Sum(f => (long)f.Restricted), folders.Sum(f => (long)f.Confidential), folders.Count,
                folders.Count(f => f.Status == "Open"), folders.Count(f => f.Status == "PossiblyOpen"), folders.Count(f => f.Status == "Unknown"),
                certain.Count, possible, s.FilesNotRead);
        })
        .OrderByDescending(s => s.Restricted + s.Confidential > 0).ThenByDescending(s => s.OpenFolders).ThenByDescending(s => s.People)
        .ThenByDescending(s => s.Restricted).ThenBy(s => s.UncPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static FolderDto Folder(ExposureMap map, SensitiveFolder f)
    {
        var graph = map.GraphOf(f.Place.ServerId);
        var grants = f.Exposure.Readers
            .SelectMany(r => r.Grants.Select(g => (Reader: r, Grant: g)))
            .GroupBy(x => x.Grant.Trustee, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var certain = g.Count(x => x.Reader.Certain && x.Grant.Certain);
                return new GrantDto(AccessViews.Trustee(graph, g.Key)!, AccessViews.Label(g.Max(x => x.Grant.Rights)), certain, g.Count() - certain);
            })
            .OrderByDescending(g => g.People).ThenByDescending(g => g.PeoplePossible).ThenBy(g => g.Trustee.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxGrantsPerFolder)
            .ToList();
        return new FolderDto(f.Place.FolderId, f.Place.ShareId, f.Place.ServerId, f.Place.Hostname, f.Place.ShareName, f.Unc, f.Restricted, f.Confidential,
            f.Status, AccessViews.Range(f.Exposure.AnyDomainUser), f.PeopleCertain, f.PeoplePossible, grants);
    }

    /// <summary>Trustees whose entries let people read sensitive files, the ones reaching the most people first.</summary>
    private static List<GroupDto> Groups(ExposureMap map)
    {
        var acc = new Dictionary<string, GroupAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in map.Folders)
        {
            foreach (var r in f.Exposure.Readers)
            {
                foreach (var g in r.Grants)
                {
                    if (!acc.TryGetValue(g.Trustee, out var a))
                    {
                        acc[g.Trustee] = a = new GroupAcc(map.GraphOf(f.Place.ServerId));
                    }
                    (r.Certain && g.Certain ? a.Certain : a.Possible).Add(r.Sid);
                    a.Highest = (Rights)Math.Max((int)a.Highest, (int)g.Rights);
                    if (a.Folders.Add(f.Place.FolderId))
                    {
                        a.Restricted += f.Restricted;
                        a.Confidential += f.Confidential;
                        a.Shares.Add(f.Place.ShareId);
                    }
                }
            }
        }
        return acc.Select(kv =>
        {
            var a = kv.Value;
            a.Possible.ExceptWith(a.Certain);
            var trustee = AccessViews.Trustee(a.Graph, kv.Key)!;
            (int Certain, int Possible)? members = a.Graph.Find(kv.Key) is { Kind: not PrincipalKind.User } || WellKnownSid.IsBroad(kv.Key) ? a.Graph.CountMembers(kv.Key) : null;
            return new GroupDto(trustee, a.Certain.Count, a.Possible.Count, members?.Certain, members?.Possible, a.Folders.Count, a.Shares.Count,
                a.Restricted, a.Confidential, AccessViews.Label(a.Highest));
        })
        .OrderByDescending(g => g.People).ThenByDescending(g => g.Restricted + g.Confidential).ThenBy(g => g.Trustee.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    /// <summary>Accounts that can read sensitive files, the ones reaching the most Restricted, then sensitive, files first.</summary>
    private static List<PersonDto> People(ExposureMap map)
    {
        var acc = new Dictionary<string, PersonAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in map.Folders)
        {
            var graph = map.GraphOf(f.Place.ServerId);
            foreach (var r in f.Exposure.Readers)
            {
                if (!acc.TryGetValue(r.Sid, out var a))
                {
                    acc[r.Sid] = a = new PersonAcc();
                }
                if (r.Certain)
                {
                    a.Restricted += f.Restricted;
                    a.Confidential += f.Confidential;
                }
                else
                {
                    a.Possible += f.Files;
                }
                a.Folders++;
                a.Shares.Add(f.Place.ShareId);
                foreach (var g in r.Grants)
                {
                    var name = Same(g.Trustee, r.Sid) ? "Direct entry" : graph.NameOf(g.Trustee);
                    a.Via[name] = a.Via.GetValueOrDefault(name) + 1;
                }
            }
        }
        return acc.Select(kv => new PersonDto(kv.Key, map.Directory.NameOf(kv.Key), kv.Value.Restricted, kv.Value.Confidential, kv.Value.Possible,
                kv.Value.Folders, kv.Value.Shares.Count,
                kv.Value.Via.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(v => v.Key).ToList()))
            .OrderByDescending(p => p.Restricted).ThenByDescending(p => p.Restricted + p.Confidential).ThenByDescending(p => p.PossibleFiles)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class GroupAcc(DirectoryGraph graph)
    {
        public DirectoryGraph Graph { get; } = graph;
        public HashSet<string> Certain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Possible { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<Guid> Folders { get; } = [];
        public HashSet<Guid> Shares { get; } = [];
        public long Restricted { get; set; }
        public long Confidential { get; set; }
        public Rights Highest { get; set; }
    }

    private sealed class PersonAcc
    {
        public long Restricted { get; set; }
        public long Confidential { get; set; }
        public long Possible { get; set; }
        public int Folders { get; set; }
        public HashSet<Guid> Shares { get; } = [];
        public Dictionary<string, int> Via { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
