using System.Linq.Expressions;
using System.Text.RegularExpressions;
using DataScope.Api.Endpoints;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Privacy;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.People;

/// <summary>
/// Everything Data Scope already holds about one person (D160), for a subject access or other personal-data request:
/// their directory accounts, files and folders named after them, scan evidence that mentions them, folders they own,
/// what they are the data owner of, and their file activity.
/// Reads only what the inventory, the last scans and the directory sync stored: file contents are not searched here.
/// </summary>
internal sealed class PersonLookup(DataScopeDbContext db, DirectoryStore directory, TimeProvider clock)
{
    /// <summary>At most this many items per kind of reason; a section with more says so.</summary>
    public const int Limit = 500;

    // Labels are stored as text, so they are compared by name, never by order.
    private static readonly List<Sensitivity> Sensitive = [Sensitivity.Confidential, Sensitivity.Restricted];

    public const string Named = "named", Evidence = "evidence", FolderOwner = "owner", DataOwner = "dataOwner";

    /// <summary>How long one section may take before it is left out and marked, so one slow table never holds up the rest.</summary>
    internal static TimeSpan SectionTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public sealed record AccountDto(
        string Sid, string Name, string? DisplayName, string? UserPrincipalName, string? Mail, bool Enabled, string? Title, string? Department,
        DateTimeOffset? LastLogon, IReadOnlyList<string> Groups);

    /// <param name="Why">named, evidence, owner or dataOwner.</param>
    /// <param name="Kind">File or Folder.</param>
    /// <param name="Id">The file's or folder's id.</param>
    /// <param name="Source">files.</param>
    /// <param name="Place">The share it is in.</param>
    /// <param name="Path">The folder inside it, empty at the top.</param>
    /// <param name="Detail">What matched or what they were given.</param>
    public sealed record ItemDto(string Why, string Kind, string Id, Guid ShareId, string Source, string Place, string Path, string Name, string? Label, string? Detail);

    /// <param name="More">There were more than <see cref="Limit"/>; only the first are listed.</param>
    /// <param name="TimedOut">This section took too long and is left out; the others are complete.</param>
    public sealed record SectionDto(string Why, int Count, bool More, bool TimedOut);

    public sealed record ActivityDto(
        int FileEvents, DateTimeOffset? FileFirst, DateTimeOffset? FileLast, IReadOnlyList<string> FileShares, bool TimedOut);

    public sealed record TermsDto(string? Sid, IReadOnlyList<string> Names, IReadOnlyList<string> Addresses);

    /// <param name="Typed">What the administrator asked for.</param>
    /// <param name="Searched">What was searched: the typed terms plus the accounts' names and addresses.</param>
    public sealed record ResultDto(
        DateTimeOffset SearchedAt, TermsDto Typed, TermsDto Searched, IReadOnlyList<AccountDto> Accounts, DateTimeOffset? DirectoryAt,
        IReadOnlyList<SectionDto> Sections, IReadOnlyList<ItemDto> Items, ActivityDto Activity, int Limit);

    /// <summary>The account a SID names in the latest directory sync, or null when there is none.</summary>
    public async Task<bool> KnownUserAsync(string sid, CancellationToken ct)
    {
        var snapshot = await directory.LatestCompletedAsync(ct);
        return snapshot is not null && await db.DirectoryPrincipals.AnyAsync(p => p.SnapshotId == snapshot.Id && p.Sid == sid && p.Kind == PrincipalKind.User, ct);
    }

    public async Task<ResultDto> FindAsync(PersonTerms typed, CancellationToken ct)
    {
        var snapshot = await directory.LatestCompletedAsync(ct);
        var accounts = snapshot is null ? [] : await AccountsAsync(snapshot.Id, typed, ct);
        var names = typed.Names.ToList();
        var addresses = typed.Addresses.ToList();
        foreach (var a in accounts)
        {
            foreach (var n in PersonSearch.AccountNames(a.Name, a.DisplayName).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                names.Add(n);
            }
            foreach (var m in new[] { a.Mail, a.UserPrincipalName }.OfType<string>().Select(m => m.Trim().ToLowerInvariant()).Where(PersonSearch.IsAddress))
            {
                if (!addresses.Contains(m, StringComparer.Ordinal))
                {
                    addresses.Add(m);
                }
            }
        }
        var sids = accounts.Select(a => a.Sid).ToList();
        var search = new Search(db, names, addresses, sids);

        var sections = new List<SectionDto>();
        var items = new List<ItemDto>();
        async Task Run(string why, Func<CancellationToken, Task<List<ItemDto>>> find)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SectionTimeout);
            try
            {
                var found = await find(timeout.Token);
                items.AddRange(found.Take(Limit));
                sections.Add(new SectionDto(why, Math.Min(found.Count, Limit), found.Count > Limit, false));
            }
            catch (Exception e) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested && e is OperationCanceledException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                sections.Add(new SectionDto(why, 0, false, true));
            }
        }
        await Run(Named, search.NamedAsync);
        await Run(Evidence, search.EvidenceAsync);
        await Run(FolderOwner, search.OwnedFoldersAsync);
        await Run(DataOwner, search.DataOwnerAsync);
        var activity = await ActivityAsync(search, ct);

        return new ResultDto(clock.GetUtcNow(), Terms(typed.Sid, typed.Names, typed.Addresses), Terms(typed.Sid, names, addresses), accounts, snapshot?.CompletedAt,
            sections, items, activity, Limit);
    }

    private static TermsDto Terms(string? sid, IReadOnlyList<string> names, IReadOnlyList<string> addresses) => new(sid, [.. names], [.. addresses]);

    /// <summary>The account picked, and accounts whose mail or sign-in name is one of the addresses typed.</summary>
    private async Task<List<AccountDto>> AccountsAsync(Guid snapshot, PersonTerms typed, CancellationToken ct)
    {
        var exact = typed.Addresses.Select(PersonSearch.Like).ToArray();
        var rows = await db.DirectoryPrincipals.AsNoTracking()
            .Where(p => p.SnapshotId == snapshot && p.Kind == PrincipalKind.User
                && (p.Sid == typed.Sid
                    || (p.Mail != null && exact.Any(x => EF.Functions.ILike(p.Mail, x)))
                    || (p.UserPrincipalName != null && exact.Any(x => EF.Functions.ILike(p.UserPrincipalName, x)))))
            .OrderBy(p => p.Sid != typed.Sid).ThenBy(p => p.Name)
            .Take(10)
            .ToListAsync(ct);
        var sids = rows.Select(r => r.Sid).ToList();
        var groups = await (
            from m in db.DirectoryMemberships.AsNoTracking()
            where m.SnapshotId == snapshot && sids.Contains(m.MemberSid)
            join g in db.DirectoryPrincipals.AsNoTracking().Where(g => g.SnapshotId == snapshot) on m.GroupSid equals g.Sid
            select new { m.MemberSid, g.Name }).ToListAsync(ct);
        return [.. rows.Select(p => new AccountDto(p.Sid, p.Name, p.DisplayName, p.UserPrincipalName, p.Mail, p.Enabled, p.Title, p.Department, p.LastLogon,
            [.. groups.Where(g => g.MemberSid == p.Sid).Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).Take(100)]))];
    }

    private static async Task<ActivityDto> ActivityAsync(Search search, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SectionTimeout);
        try
        {
            var file = await search.FileActivityAsync(timeout.Token);
            return new ActivityDto(file.Count, file.First, file.Last, file.Shares, false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested && e is OperationCanceledException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            return new ActivityDto(0, null, null, [], true);
        }
    }

    private sealed class Search(DataScopeDbContext db, List<string> names, List<string> addresses, List<string> sids)
    {
        // Names and addresses anywhere in a name; addresses exactly in an address field.
        private readonly string[] _contains = [.. names.Concat(addresses).Select(PersonSearch.Contains)];

        private IQueryable<Share> LiveShares => db.Shares.AsNoTracking()
            .Where(s => s.State != ShareState.Removed && s.Server!.State != EnrollmentState.Retired);

        private sealed record FileRow(Guid Id, string Name, string Path, Guid ShareId, string UncPath, Sensitivity Label, SourceKind Kind);

        private async Task<List<FileRow>> FilesAsync(Expression<Func<InventoryFile, bool>> match, CancellationToken ct) => await (
            from f in db.Files.AsNoTracking().Where(f => f.RemovedAt == null).Where(match)
            join folder in db.Folders on f.FolderId equals folder.Id
            join s in LiveShares on f.ShareId equals s.Id
            orderby s.UncPath, folder.Path, f.Name
            select new FileRow(f.Id, f.Name, folder.Path, s.Id, s.UncPath, f.OverrideSensitivity ?? f.Sensitivity, s.Server!.Kind))
            .Take(Limit + 1).ToListAsync(ct);

        private static ItemDto File(string why, FileRow r, string? detail) => new(why, "File", r.Id.ToString(), r.ShareId, Source(r.Kind), r.UncPath, r.Path,
            r.Name, LabelText(r.Label), detail);

        /// <summary>Files and folders with one of the names or addresses in their own name.</summary>
        public async Task<List<ItemDto>> NamedAsync(CancellationToken ct)
        {
            var files = await FilesAsync(f => _contains.Any(p => EF.Functions.ILike(f.Name, p)), ct);
            var items = files.Select(r => File(Named, r, Matched(r.Name))).ToList();
            if (items.Count > Limit)
            {
                return items;
            }
            // A folder's own name is the last part of its path.
            var terms = string.Join('|', names.Concat(addresses).Select(SearchEndpoints.RegexEscape));
            var pattern = $@"(^|\\)[^\\]*({terms})[^\\]*$";
            var folders = await (
                from f in db.Folders.AsNoTracking().Where(f => f.RemovedAt == null && f.Path != "" && Regex.IsMatch(f.Path, pattern, RegexOptions.IgnoreCase))
                join s in LiveShares on f.ShareId equals s.Id
                orderby s.UncPath, f.Path
                select new { FolderId = f.Id, f.Path, ShareId = s.Id, s.UncPath, s.Server!.Kind })
                .Take(Limit + 1 - items.Count).ToListAsync(ct);
            items.AddRange(folders.Select(f => new ItemDto(Named, "Folder", f.FolderId.ToString(), f.ShareId, Source(f.Kind), f.UncPath, Parent(f.Path), LastSegment(f.Path),
                null, Matched(LastSegment(f.Path)))));
            return items;
        }

        /// <summary>
        /// Files whose stored scan evidence (D36: masked snippets around a possible match) mentions one of the names or
        /// addresses. Only text near something a detector flagged is stored, so this finds some files, never all.
        /// </summary>
        public async Task<List<ItemDto>> EvidenceAsync(CancellationToken ct)
        {
            var rules = new Dictionary<Guid, (SortedSet<string> Rules, SortedSet<string> Terms)>();
            foreach (var term in names.Concat(addresses))
            {
                var pattern = PersonSearch.Contains(term);
                var hits = await db.Findings.AsNoTracking()
                    .Where(x => x.Evidence.Any(e => EF.Functions.ILike(e, pattern)))
                    .Select(x => new { x.FileId, x.RuleId })
                    .Take(Limit * 4).ToListAsync(ct);
                foreach (var h in hits)
                {
                    if (!rules.TryGetValue(h.FileId, out var r))
                    {
                        rules[h.FileId] = r = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    r.Rules.Add(h.RuleId);
                    r.Terms.Add(term);
                }
            }
            if (rules.Count == 0)
            {
                return [];
            }
            var ids = rules.Keys.ToList();
            var files = await FilesAsync(f => ids.Contains(f.Id), ct);
            return [.. files.Select(r => File(Evidence, r, $"Mentions {string.Join(", ", rules[r.Id].Terms)} near a possible match ({string.Join(", ", rules[r.Id].Rules)})"))];
        }

        /// <summary>
        /// Folders on file shares whose NTFS owner is one of their accounts. Only the highest such folder of a branch is
        /// listed: a folder whose parent they also own is left out.
        /// </summary>
        public async Task<List<ItemDto>> OwnedFoldersAsync(CancellationToken ct)
        {
            if (sids.Count == 0)
            {
                return [];
            }
            var owned = db.SecurityDescriptors.Where(d => d.OwnerSid != null && sids.Contains(d.OwnerSid)).Select(d => d.Id);
            var rows = await (
                from f in db.Folders.AsNoTracking()
                where f.RemovedAt == null && f.SecurityId != null && owned.Contains(f.SecurityId.Value)
                    && !db.Folders.Any(p => p.Id == f.ParentId && p.SecurityId != null && owned.Contains(p.SecurityId.Value))
                join s in LiveShares on f.ShareId equals s.Id
                orderby s.UncPath, f.Path
                select new { FolderId = f.Id, f.Path, ShareId = s.Id, s.UncPath, s.Server!.Kind })
                .Take(Limit + 1).ToListAsync(ct);
            return [.. rows.Select(f => new ItemDto(FolderOwner, "Folder", f.FolderId.ToString(), f.ShareId, Source(f.Kind), f.UncPath, Parent(f.Path),
                f.Path.Length == 0 ? "(top of the share)" : LastSegment(f.Path), null, "Owner of the folder in Windows"))];
        }

        /// <summary>Folders they are the data owner or backup owner of in Data Scope.</summary>
        public async Task<List<ItemDto>> DataOwnerAsync(CancellationToken ct)
        {
            if (sids.Count == 0)
            {
                return [];
            }
            var rows = await (
                from o in db.OwnerAssignments.AsNoTracking().Where(o => o.EndedAt == null && (sids.Contains(o.OwnerSid) || (o.BackupSid != null && sids.Contains(o.BackupSid))))
                join f in db.Folders on o.FolderId equals f.Id
                join s in LiveShares on o.ShareId equals s.Id
                orderby s.UncPath, f.Path
                select new { FolderId = f.Id, f.Path, ShareId = s.Id, s.UncPath, s.Server!.Kind, Backup = !sids.Contains(o.OwnerSid) })
                .Take(Limit + 1).ToListAsync(ct);
            return [.. rows.Select(f => new ItemDto(DataOwner, "Folder", f.FolderId.ToString(), f.ShareId, Source(f.Kind), f.UncPath, Parent(f.Path),
                f.Path.Length == 0 ? "(top of the share)" : LastSegment(f.Path), null, f.Backup ? "Backup data owner" : "Data owner"))];
        }

        public async Task<(int Count, DateTimeOffset? First, DateTimeOffset? Last, List<string> Shares)> FileActivityAsync(CancellationToken ct)
        {
            if (sids.Count == 0)
            {
                return (0, null, null, []);
            }
            var events = db.FileAuditEvents.AsNoTracking().Where(e => e.PrincipalSid != null && sids.Contains(e.PrincipalSid));
            var summary = await events.GroupBy(_ => 1).Select(g => new { Count = g.Count(), First = g.Min(e => e.EventTime), Last = g.Max(e => e.EventTime) }).FirstOrDefaultAsync(ct);
            if (summary is null)
            {
                return (0, null, null, []);
            }
            var shares = await events.Where(e => e.EventShareName != null).GroupBy(e => e.EventShareName!)
                .OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ToListAsync(ct);
            return (summary.Count, summary.First, summary.Last, shares);
        }

        private string Matched(string name) =>
            names.Concat(addresses).FirstOrDefault(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)) is { } t ? $"Name contains {t}" : "Name matches";
    }

    internal static string Source(SourceKind kind) => "files";

    private static string Parent(string path) => path.LastIndexOf('\\') is var cut and >= 0 ? path[..cut] : "";

    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];

    private static string? LabelText(Sensitivity s) => s == Sensitivity.Unknown ? null : s.ToString();
}
