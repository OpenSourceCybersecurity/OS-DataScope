using System.Text;
using System.Text.RegularExpressions;
using DataScope.Api.Auth;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// The search box at the top of every page (D128): people, groups, shares, folders, files and servers by name, or a
/// pasted \\server\share\path. Reads only what the other posture screens already show, from the inventory and the
/// latest directory sync.
/// </summary>
internal static class SearchEndpoints
{
    /// <param name="Id">The SID for people and groups, otherwise the row's id.</param>
    /// <param name="Detail">Where it is (a UNC path) or who it is (title and department).</param>
    /// <param name="Label">A file's sensitivity once its contents have been read.</param>
    /// <param name="Exact">The pasted path names exactly this.</param>
    public sealed record SearchHitDto(string Id, string Title, string? Detail, string? Label = null, bool Exact = false, bool Disabled = false);

    /// <param name="Kind">User, Group, Share, Folder, File or Server.</param>
    /// <param name="More">There are more matches than shown; a longer search narrows them.</param>
    /// <param name="TimedOut">This kind took too long and was left out; the other kinds are complete.</param>
    public sealed record SearchGroupDto(string Kind, IReadOnlyList<SearchHitDto> Items, bool More, bool TimedOut = false);

    /// <param name="FilesSearched">False for searches shorter than <see cref="MinFileTermLength"/> characters.</param>
    /// <param name="DirectoryAt">When the directory data was read; null when Active Directory has not been synced.</param>
    public sealed record SearchResultDto(string Query, IReadOnlyList<SearchGroupDto> Groups, bool FilesSearched, DateTimeOffset? DirectoryAt);

    public const int MinTermLength = 2;

    /// <summary>File and folder names are matched by trigrams, which need three characters to narrow anything.</summary>
    public const int MinFileTermLength = 3;

    public const int MaxTermLength = 200;
    public const int DefaultLimit = 5;
    public const int MaxLimit = 20;

    /// <summary>How long one kind may take before it is left out, so a slow file search never holds up people and shares.</summary>
    internal static TimeSpan KindTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public static void MapSearch(this RouteGroupBuilder api)
    {
        api.MapGet("/search", async (string? q, int? limit, DataScopeDbContext db, DirectoryStore directory, CancellationToken ct) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length < MinTermLength || term.Length > MaxTermLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = [$"Search for {MinTermLength} to {MaxTermLength} characters."] });
            }
            if (term.Any(char.IsControl))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Search for printable characters only."] });
            }
            var n = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var search = new Search(db, term.Replace('/', '\\'), n);
            var snapshot = await directory.LatestCompletedAsync(ct);

            var groups = new List<SearchGroupDto>();
            var path = term.StartsWith(@"\\", StringComparison.Ordinal) || term.StartsWith("//", StringComparison.Ordinal);
            var files = term.Length >= MinFileTermLength;
            if (path)
            {
                // A pasted path: what it names exactly comes first, then folders and files under it.
                groups.Add(await search.Run("Share", search.SharesByPath, ct));
                if (files)
                {
                    groups.Add(await search.Run("Folder", search.FoldersByPath, ct));
                    groups.Add(await search.Run("File", search.FilesByPath, ct));
                }
            }
            else
            {
                if (snapshot is not null)
                {
                    groups.Add(await search.Run("User", c => search.Principals(snapshot.Id, PrincipalKind.User, c), ct));
                    groups.Add(await search.Run("Group", c => search.Principals(snapshot.Id, PrincipalKind.Group, c), ct));
                }
                groups.Add(await search.Run("Share", search.Shares, ct));
                groups.Add(await search.Run("Server", search.Servers, ct));
                if (files)
                {
                    groups.Add(await search.Run("Folder", search.Folders, ct));
                    groups.Add(await search.Run("File", search.Files, ct));
                }
            }
            return Results.Ok(new SearchResultDto(term, groups.Where(g => g.Items.Count > 0 || g.TimedOut).ToList(), files, snapshot?.CompletedAt));
        }).RequireAuthorization(Policies.ReadPosture);
    }

    private sealed class Search(DataScopeDbContext db, string term, int limit)
    {
        // Matches come back in no particular order so the database can stop early; these are ranked here.
        private const int Candidates = 50;
        private readonly string _contains = "%" + Like(term) + "%";
        private readonly string _prefix = Like(term) + "%";

        public async Task<SearchGroupDto> Run(string kind, Func<CancellationToken, Task<List<SearchHitDto>>> find, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(KindTimeout);
            try
            {
                var hits = await find(timeout.Token);
                return new SearchGroupDto(kind, hits.Take(limit).ToList(), hits.Count > limit);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested && e is OperationCanceledException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                return new SearchGroupDto(kind, [], More: false, TimedOut: true);
            }
        }

        private IQueryable<Share> LiveShares => db.Shares.AsNoTracking()
            .Where(s => s.State != ShareState.Removed && s.Server!.State != EnrollmentState.Retired);

        public async Task<List<SearchHitDto>> Principals(Guid snapshot, PrincipalKind kind, CancellationToken ct)
        {
            var query = db.DirectoryPrincipals.AsNoTracking().Where(p => p.SnapshotId == snapshot && p.Kind == kind);
            query = Sid.IsValid(term)
                ? query.Where(p => p.Sid == term)
                : query.Where(p => EF.Functions.ILike(p.Name, _contains, "\\")
                    || (p.DisplayName != null && EF.Functions.ILike(p.DisplayName, _contains, "\\"))
                    || (p.UserPrincipalName != null && EF.Functions.ILike(p.UserPrincipalName, _contains, "\\")));
            var rows = await query
                .OrderByDescending(p => EF.Functions.ILike(p.Name, _prefix, "\\") || (p.DisplayName != null && EF.Functions.ILike(p.DisplayName, _prefix, "\\")))
                .ThenByDescending(p => p.Enabled).ThenBy(p => p.DisplayName ?? p.Name).ThenBy(p => p.Sid)
                .Take(limit + 1)
                .Select(p => new { p.Sid, p.Name, p.DisplayName, p.Enabled, p.IsSecurityGroup, p.Title, p.Department, p.UserPrincipalName })
                .ToListAsync(ct);
            return rows.Select(p => new SearchHitDto(
                p.Sid,
                p.DisplayName is { Length: > 0 } d && !string.Equals(d, p.Name, StringComparison.OrdinalIgnoreCase) ? $"{d} ({p.Name})" : p.Name,
                kind == PrincipalKind.Group
                    ? (p.IsSecurityGroup ? "Security group" : "Distribution group")
                    : string.Join(" · ", new[] { p.Title, p.Department }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } who ? who : p.UserPrincipalName,
                Disabled: !p.Enabled)).ToList();
        }

        public async Task<List<SearchHitDto>> Shares(CancellationToken ct)
        {
            var rows = await LiveShares
                .Where(s => EF.Functions.ILike(s.Name, _contains, "\\") || EF.Functions.ILike(s.UncPath, _contains, "\\"))
                .OrderByDescending(s => EF.Functions.ILike(s.Name, _prefix, "\\")).ThenBy(s => s.Kind != ShareKind.Disk).ThenBy(s => s.UncPath)
                .Take(limit + 1)
                .Select(s => new { s.Id, s.Name, s.UncPath }).ToListAsync(ct);
            return rows.Select(s => new SearchHitDto(s.Id.ToString(), s.Name, s.UncPath)).ToList();
        }

        public async Task<List<SearchHitDto>> Servers(CancellationToken ct)
        {
            var rows = await db.ManagedServers.AsNoTracking()
                .Where(s => s.State != EnrollmentState.Retired && EF.Functions.ILike(s.Hostname, _contains, "\\"))
                .OrderByDescending(s => EF.Functions.ILike(s.Hostname, _prefix, "\\")).ThenBy(s => s.Hostname)
                .Take(limit + 1)
                .Select(s => new { s.Id, s.Hostname, s.State, s.Kind }).ToListAsync(ct);
            return rows.Select(s =>
            {
                const string what = "File server";
                return new SearchHitDto(s.Id.ToString(), s.Hostname, s.State == EnrollmentState.Enrolled ? what : $"{what} ({s.State.ToString().ToLowerInvariant()})");
            }).ToList();
        }

        /// <summary>Folders whose own name matches: names starting with the search first, then names containing it.</summary>
        public async Task<List<SearchHitDto>> Folders(CancellationToken ct)
        {
            // Folder names never contain a backslash; a search that does matches anywhere in the share-relative path.
            if (term.Contains('\\', StringComparison.Ordinal))
            {
                return await FolderHits(f => EF.Functions.ILike(f.Path, _contains, "\\"), ct);
            }
            var escaped = RegexEscape(term);
            var starts = $@"(^|\\){escaped}[^\\]*$";
            var contains = $@"(^|\\)[^\\]*{escaped}[^\\]*$";
            var first = await FolderHits(f => Regex.IsMatch(f.Path, starts, RegexOptions.IgnoreCase), ct);
            if (first.Count > limit)
            {
                return first;
            }
            var rest = await FolderHits(f => Regex.IsMatch(f.Path, contains, RegexOptions.IgnoreCase), ct);
            return [.. first, .. rest.Where(r => first.All(f => f.Id != r.Id))];
        }

        private async Task<List<SearchHitDto>> FolderHits(System.Linq.Expressions.Expression<Func<Folder, bool>> match, CancellationToken ct)
        {
            var rows = await (
                from f in db.Folders.AsNoTracking().Where(f => f.RemovedAt == null && f.Path != "").Where(match)
                join s in LiveShares on f.ShareId equals s.Id
                select new { f.Id, f.Path, s.UncPath }).Take(Candidates).ToListAsync(ct);
            return rows.OrderBy(r => LastSegment(r.Path).Length).ThenBy(r => r.Path.Length).ThenBy(r => r.UncPath + r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(r => new SearchHitDto(r.Id.ToString(), LastSegment(r.Path), r.UncPath + "\\" + r.Path)).ToList();
        }

        /// <summary>Files by name: names starting with the search first, then names containing it.</summary>
        public async Task<List<SearchHitDto>> Files(CancellationToken ct)
        {
            if (term.Contains('\\', StringComparison.Ordinal))
            {
                return [];
            }
            var first = await FileHits(f => EF.Functions.ILike(f.Name, _prefix, "\\"), ct);
            if (first.Count > limit)
            {
                return first;
            }
            var rest = await FileHits(f => EF.Functions.ILike(f.Name, _contains, "\\"), ct);
            return [.. first, .. rest.Where(r => first.All(f => f.Id != r.Id))];
        }

        private async Task<List<SearchHitDto>> FileHits(System.Linq.Expressions.Expression<Func<InventoryFile, bool>> match, CancellationToken ct)
        {
            var rows = await (
                from f in db.Files.AsNoTracking().Where(f => f.RemovedAt == null).Where(match)
                join folder in db.Folders on f.FolderId equals folder.Id
                join s in LiveShares on f.ShareId equals s.Id
                select new { f.Id, f.Name, folder.Path, s.UncPath, Label = f.OverrideSensitivity ?? f.Sensitivity })
                .Take(Candidates).ToListAsync(ct);
            return rows.OrderBy(r => r.Name.Length).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.UncPath + r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(r => new SearchHitDto(r.Id.ToString(), r.Name, Unc(r.UncPath, r.Path), LabelText(r.Label))).ToList();
        }

        // ------------------------------------------------------------------ pasted paths

        private (Share Share, string Inside)? _resolved;
        private bool _resolvedDone;

        /// <summary>The share a pasted \\server\share\path is on (longest match), and the path inside it.</summary>
        private async Task<(Share Share, string Inside)?> Resolve(CancellationToken ct)
        {
            if (_resolvedDone)
            {
                return _resolved;
            }
            var parts = term.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                // Share names are unique per server; servers are stored by lower-case name.
                var host = parts[0].ToLowerInvariant();
                var nameKey = parts[1].ToUpperInvariant();
                var share = await LiveShares.Where(s => s.NameKey == nameKey && (s.Server!.Hostname == host || s.Server!.Hostname.StartsWith(host + ".")))
                    .OrderBy(s => s.Server!.Hostname).FirstOrDefaultAsync(ct);
                if (share is not null)
                {
                    _resolved = (share, string.Join('\\', parts.Skip(2)));
                }
            }
            _resolvedDone = true;
            return _resolved;
        }

        public async Task<List<SearchHitDto>> SharesByPath(CancellationToken ct)
        {
            if (await Resolve(ct) is { } r)
            {
                return [new SearchHitDto(r.Share.Id.ToString(), r.Share.Name, r.Share.UncPath, Exact: r.Inside.Length == 0)];
            }
            // Not a known share yet: \\fs0 or \\fs01\H as typed so far.
            var prefix = Like(term.TrimEnd('\\')) + "%";
            var rows = await LiveShares.Where(s => EF.Functions.ILike(s.UncPath, prefix, "\\")).OrderBy(s => s.UncPath).Take(limit + 1)
                .Select(s => new { s.Id, s.Name, s.UncPath }).ToListAsync(ct);
            return rows.Select(s => new SearchHitDto(s.Id.ToString(), s.Name, s.UncPath)).ToList();
        }

        public async Task<List<SearchHitDto>> FoldersByPath(CancellationToken ct)
        {
            if (await Resolve(ct) is not { } r)
            {
                return [];
            }
            var hits = new List<SearchHitDto>();
            if (r.Inside.Length > 0)
            {
                var hash = InventoryStore.PathHash(r.Inside);
                var exact = await db.Folders.AsNoTracking().Where(f => f.ShareId == r.Share.Id && f.PathHash == hash && f.RemovedAt == null)
                    .Select(f => new { f.Id, f.Path }).FirstOrDefaultAsync(ct);
                if (exact is not null)
                {
                    hits.Add(new SearchHitDto(exact.Id.ToString(), LastSegment(exact.Path), Unc(r.Share.UncPath, exact.Path), Exact: true));
                }
            }
            // Folders under the path, or starting with its last part as typed so far, shallowest first.
            var under = r.Inside.Length == 0 ? "%" : Like(r.Inside) + "%";
            var rows = await db.Folders.AsNoTracking()
                .Where(f => f.ShareId == r.Share.Id && f.RemovedAt == null && f.Path != "" && EF.Functions.ILike(f.Path, under, "\\"))
                .OrderBy(f => f.Depth).ThenBy(f => f.Path).Take(limit + 1)
                .Select(f => new { f.Id, f.Path }).ToListAsync(ct);
            hits.AddRange(rows.Where(f => hits.All(h => h.Id != f.Id.ToString()))
                .Select(f => new SearchHitDto(f.Id.ToString(), LastSegment(f.Path), Unc(r.Share.UncPath, f.Path))));
            return hits;
        }

        public async Task<List<SearchHitDto>> FilesByPath(CancellationToken ct)
        {
            if (await Resolve(ct) is not { } r || r.Inside.Length == 0)
            {
                return [];
            }
            // A file named exactly, or files in the folder named, or files in the parent starting with the last part.
            var cut = r.Inside.LastIndexOf('\\');
            var parentPath = cut < 0 ? "" : r.Inside[..cut];
            var name = r.Inside[(cut + 1)..];
            var hits = new List<SearchHitDto>();
            async Task<Guid?> FolderId(string path)
            {
                var hash = InventoryStore.PathHash(path);
                return await db.Folders.AsNoTracking().Where(f => f.ShareId == r.Share.Id && f.PathHash == hash && f.RemovedAt == null)
                    .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
            }
            if (await FolderId(parentPath) is { } parent)
            {
                var key = name.ToUpperInvariant();
                var prefix = Like(name) + "%";
                var rows = await db.Files.AsNoTracking()
                    .Where(f => f.FolderId == parent && f.RemovedAt == null && EF.Functions.ILike(f.Name, prefix, "\\"))
                    .OrderByDescending(f => f.NameKey == key).ThenBy(f => f.NameKey).Take(limit + 1)
                    .Select(f => new { f.Id, f.Name, f.NameKey, Label = f.OverrideSensitivity ?? f.Sensitivity }).ToListAsync(ct);
                hits.AddRange(rows.Select(f => new SearchHitDto(f.Id.ToString(), f.Name, Unc(r.Share.UncPath, parentPath), LabelText(f.Label), Exact: f.NameKey == key)));
            }
            if (hits.Count <= limit && await FolderId(r.Inside) is { } folder)
            {
                var rows = await db.Files.AsNoTracking().Where(f => f.FolderId == folder && f.RemovedAt == null)
                    .OrderBy(f => f.NameKey).Take(limit + 1 - hits.Count)
                    .Select(f => new { f.Id, f.Name, Label = f.OverrideSensitivity ?? f.Sensitivity }).ToListAsync(ct);
                hits.AddRange(rows.Select(f => new SearchHitDto(f.Id.ToString(), f.Name, Unc(r.Share.UncPath, r.Inside), LabelText(f.Label))));
            }
            return hits;
        }
    }

    private static string Unc(string share, string path) => path.Length == 0 ? share : share + "\\" + path;

    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];

    private static string? LabelText(Sensitivity s) => s == Sensitivity.Unknown ? null : s.ToString();

    private static string Like(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Escapes a search for a PostgreSQL regular expression, so it matches literally.</summary>
    internal static string RegexEscape(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (@"\.^$|?*+()[]{}".Contains(c, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
