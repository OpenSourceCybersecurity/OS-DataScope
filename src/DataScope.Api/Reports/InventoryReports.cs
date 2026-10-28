using DataScope.Api.Endpoints;
using DataScope.Api.Reviews;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>One kind of report.</summary>
internal interface IReport
{
    ReportInfo Info { get; }

    Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct);
}

/// <summary>Every file labelled Confidential or Restricted, with what was found and who set the label.</summary>
internal sealed class SensitiveDataReport : IReport
{
    /// <summary>The labels the reports call sensitive.</summary>
    public static readonly List<Sensitivity> SensitiveLabels = [Sensitivity.Confidential, Sensitivity.Restricted];

    public ReportInfo Info { get; } = new("sensitive-data", "Sensitive data inventory",
        "Every file labelled Confidential or Restricted: what was found, how confident the match is, and whether a reviewer set the label.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        // Labels are stored as text, so compare against the list rather than with >= in SQL.
        var sensitive = SensitiveLabels;
        var rows = await (from f in ctx.Files
                          where sensitive.Contains(f.OverrideSensitivity ?? f.Sensitivity)
                          join folder in db.Folders on f.FolderId equals folder.Id
                          join share in db.Shares on f.ShareId equals share.Id
                          join server in db.ManagedServers on share.ServerId equals server.Id
                          orderby (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Restricted ? 0 : 1, server.Hostname, share.Name, folder.Path, f.NameKey
                          select new
                          {
                              f.Id,
                              server.Hostname,
                              share.UncPath,
                              folder.Path,
                              f.Name,
                              Label = f.OverrideSensitivity ?? f.Sensitivity,
                              Overridden = f.OverrideSensitivity != null,
                              f.Size,
                              f.ModifiedAtSource,
                              f.ClassifiedAt,
                              f.ClassifiedPolicy,
                          }).Take(ctx.RowLimit + 1).ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var findings = (await db.Findings.AsNoTracking().Where(x => ids.Contains(x.FileId))
                .Select(x => new { x.FileId, x.RuleId, x.Category, x.Confidence, x.MatchCount }).ToListAsync(ct))
            .ToLookup(x => x.FileId);
        var reviewers = await db.ClassificationOverrides.AsNoTracking().Where(o => ids.Contains(o.FileId))
            .GroupBy(o => o.FileId).Select(g => g.OrderByDescending(o => o.Id).Select(o => new { o.FileId, o.ReviewerName }).First())
            .ToDictionaryAsync(x => x.FileId, x => x.ReviewerName, ct);

        var table = rows.Select(r =>
        {
            var found = findings[r.Id].ToList();
            var categories = found.GroupBy(x => x.Category)
                .Select(g => $"{g.Key.Label()} ({g.Sum(x => x.MatchCount)} matches, {g.Max(x => x.Confidence)} confidence)");
            return new object?[]
            {
                r.Hostname, r.UncPath, AccessEndpoints.UncOf(r.UncPath, r.Path, r.Name), r.Label.ToString(),
                r.Overridden ? $"Reviewer: {reviewers.GetValueOrDefault(r.Id, "unknown")}" : "Policy",
                string.Join("; ", categories), string.Join(" ", found.Select(x => x.RuleId).Distinct().Order(StringComparer.Ordinal)),
                found.Count == 0 ? null : found.Max(x => x.Confidence).ToString(),
                r.Size, r.ModifiedAtSource, r.ClassifiedAt, r.ClassifiedPolicy,
            };
        }).ToList();

        var facts = await ctx.CommonFactsAsync(ct);
        return ctx.Document(Info, facts,
        [
            "Labels come from pattern matches. Each one is a possible match with a confidence, not a confirmed record of personal or health data.",
            "Files whose contents were not read (unsupported, too large, offline, access denied, encrypted) are not listed here even if they hold sensitive data. They are counted in the scan coverage report.",
            "No matched text is included. Evidence snippets, already masked, are visible only in Data Scope to roles allowed to see them.",
            "A reviewer can change a label; the label source column says when that happened.",
        ],
        ["Server", "Share", "File", "Label", "Label set by", "What was found", "Rules", "Highest confidence", "Size (bytes)", "Modified", "Classified", "Policy version"],
        table);
    }
}

/// <summary>Every disk share with its size, sensitive files, owner and review schedule.</summary>
internal sealed class ShareOwnerReport : IReport
{
    public ReportInfo Info { get; } = new("shares-owners", "Share and owner inventory",
        "Every disk share: how much it holds, how many sensitive files, who owns it, when it was last reviewed, and whether any domain user can read it.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        var shares = await (from s in ctx.Shares
                            where s.Kind == ShareKind.Disk
                            join m in db.ManagedServers on s.ServerId equals m.Id
                            orderby m.Hostname, s.NameKey
                            select new { s.Id, s.ServerId, m.Hostname, s.UncPath, s.Included, s.State, s.LastScannedAt, s.FolderCount, s.FileCount, s.TotalBytes, s.SecurityState, s.SecurityId })
            .Take(ctx.RowLimit + 1).ToListAsync(ct);
        var ids = shares.Select(s => s.Id).ToList();
        var labels = await db.Files.AsNoTracking().Where(f => ids.Contains(f.ShareId) && f.RemovedAt == null && SensitiveDataReport.SensitiveLabels.Contains(f.OverrideSensitivity ?? f.Sensitivity))
            .GroupBy(f => new { f.ShareId, Label = f.OverrideSensitivity ?? f.Sensitivity })
            .Select(g => new { g.Key.ShareId, g.Key.Label, Count = g.LongCount() }).ToListAsync(ct);
        var roots = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.ShareId) && f.Depth == 0 && f.RemovedAt == null)
            .Select(f => new { f.Id, f.ShareId, f.SecurityState, f.SecurityId }).ToListAsync(ct);
        var rootByShare = roots.GroupBy(r => r.ShareId).ToDictionary(g => g.Key, g => g.First());
        var owners = await db.OwnerAssignments.AsNoTracking().Where(a => ids.Contains(a.ShareId) && a.EndedAt == null).ToListAsync(ct);
        var rootIds = roots.Select(r => r.Id).ToHashSet();
        var rootOwner = owners.Where(a => rootIds.Contains(a.FolderId)).ToDictionary(a => a.ShareId);
        var status = await OwnerEndpoints.StatusAsync(db, directory, owners.SelectMany(a => new[] { a.OwnerSid, a.BackupSid }).OfType<string>(), ct);
        var reviewed = await OwnerEndpoints.LastReviewedAsync(db, roots.Select(r => r.Id).ToList(), ct);

        var table = new List<object?[]>(shares.Count);
        foreach (var s in shares)
        {
            long Count(Sensitivity label) => labels.Where(x => x.ShareId == s.Id && x.Label == label).Sum(x => x.Count);
            var root = rootByShare.GetValueOrDefault(s.Id);
            string open = "Not scanned";
            if (root is not null)
            {
                var (graph, key) = await ctx.Access.GraphAsync(s.ServerId, ct);
                var place = await ctx.Access.PermissionsAsync(s.SecurityState, s.SecurityId, root.SecurityState, root.SecurityId, ct);
                open = Exposure(ctx.Access.AnyDomainUser(graph, key, place, AccessTarget.Files));
            }
            var a = rootOwner.GetValueOrDefault(s.Id);
            DateOnly? last = root is not null && reviewed.TryGetValue(root.Id, out var d) ? d : null;
            table.Add(
            [
                s.Hostname, s.UncPath, s.Included, s.State.ToString(), s.LastScannedAt, s.FolderCount, s.FileCount, s.TotalBytes,
                Count(Sensitivity.Restricted), Count(Sensitivity.Confidential), open,
                a?.OwnerName ?? "No owner", a is null ? null : status.GetValueOrDefault(a.OwnerSid, "Unknown"), a?.BackupName, a?.Department,
                owners.Count(o => o.ShareId == s.Id && !rootIds.Contains(o.FolderId)),
                a?.ReviewFrequency.ToString(), last, a is not null && last is { } lr ? lr.AddMonths(a.ReviewFrequency.Months()) : null,
            ]);
        }

        var facts = await ctx.CommonFactsAsync(ct);
        return ctx.Document(Info, facts,
        [
            "Owners are the people an administrator confirmed in Data Scope. They are not read from Windows.",
            "\"Any domain user can read\" is computed from the share and NTFS permissions the latest scan read and the latest directory sync. It is not a Windows access check and not evidence that anyone opened a file.",
            "Sensitive file counts include only files whose contents were read. Owner status is from the latest directory sync.",
            "Owned subfolders are folders below the share with an owner of their own.",
        ],
        ["Server", "Share", "Scanned", "Scan state", "Last scan", "Folders", "Files", "Size (bytes)", "Restricted files", "Confidential files",
         "Any domain user can read", "Owner", "Owner account", "Backup owner", "Department", "Owned subfolders", "Review every", "Last reviewed", "Next review due"],
        table);
    }

    /// <summary>The answer to "can any domain user read files here", in words that keep the uncertainty.</summary>
    public static string Exposure(AccessResult any) =>
        any.Certainty == Certainty.Unknown ? "Unknown (permissions not read)"
        : any.CanRead ? "Yes"
        : any.MightRead ? "Possibly"
        : "No";
}

/// <summary>What each share's latest scan covered, and what it could not.</summary>
internal sealed class CoverageReport : IReport
{
    internal static readonly ContentState[] NotReadReasons =
        [ContentState.Unsupported, ContentState.TooLarge, ContentState.Offline, ContentState.AccessDenied, ContentState.NotFound,
         ContentState.Encrypted, ContentState.Unreadable, ContentState.TimedOut, ContentState.IoError];

    public ReportInfo Info { get; } = new("scan-coverage", "Scan coverage and failures",
        "For every share: whether it was scanned, the gaps the scan hit, how many files were read and why the others were not.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        var shares = await (from s in ctx.Shares
                            join m in db.ManagedServers on s.ServerId equals m.Id
                            orderby m.Hostname, s.NameKey
                            select new { s.Id, m.Hostname, m.LocalGroupsState, s.UncPath, s.Kind, s.Included, s.State, s.LastScannedAt, s.LastScanJobId, s.FolderCount, s.FileCount, s.SecurityState })
            .Take(ctx.RowLimit + 1).ToListAsync(ct);
        var ids = shares.Select(s => s.Id).ToList();
        var states = await db.Files.AsNoTracking().Where(f => ids.Contains(f.ShareId) && f.RemovedAt == null)
            .GroupBy(f => new { f.ShareId, f.ContentState }).Select(g => new { g.Key.ShareId, g.Key.ContentState, Count = g.LongCount() }).ToListAsync(ct);
        var gaps = await (from g in db.ScanGaps.AsNoTracking()
                          join s in db.Shares on g.ShareId equals s.Id
                          where ids.Contains(s.Id) && g.JobId == s.LastScanJobId
                          group g by new { s.Id, g.Kind } into x
                          select new { ShareId = x.Key.Id, x.Key.Kind, Count = x.Count() }).ToListAsync(ct);
        var unreadPermissions = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.ShareId) && f.RemovedAt == null && f.SecurityState != PermissionState.Read)
            .GroupBy(f => f.ShareId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var table = shares.Select(s =>
        {
            long Files(ContentState state) => states.Where(x => x.ShareId == s.Id && x.ContentState == state).Sum(x => x.Count);
            var shareGaps = gaps.Where(g => g.ShareId == s.Id).OrderByDescending(g => g.Count).ToList();
            var row = new List<object?>
            {
                s.Hostname, s.UncPath, s.Kind.ToString(), s.Included,
                s.Kind != ShareKind.Disk ? "Never scanned (administrative or non-disk share)" : s.State.ToString(),
                s.LastScannedAt, s.FolderCount, s.FileCount, shareGaps.Sum(g => g.Count),
                string.Join("; ", shareGaps.Select(g => $"{g.Kind} {g.Count}")),
                s.SecurityState == PermissionState.Read ? "Read" : s.SecurityState.ToString(),
                unreadPermissions.GetValueOrDefault(s.Id), s.LocalGroupsState.ToString(),
                Files(ContentState.Scanned), Files(ContentState.Partial), Files(ContentState.NotRead),
            };
            row.AddRange(NotReadReasons.Select(r => (object?)Files(r)));
            return row.ToArray();
        }).ToList();

        var facts = await ctx.CommonFactsAsync(ct);
        return ctx.Document(Info, facts,
        [
            "Counts are from each share's latest scan. A gap is a folder or path the scan could not list or chose not to follow, such as access denied or a link that could loop.",
            "Files that were not read are reported as not read, never as clean. Offline and cloud-only files are never opened, so they are never recalled.",
            "Share permissions and local groups can only be read by an administrator of the file server; where they were not read, access results elsewhere are marked Possibly.",
            "Each gap's path and message are listed on the Shares screen for that share.",
        ],
        ["Server", "Share", "Kind", "Included", "Scan state", "Last scan", "Folders", "Files", "Gaps", "Gap kinds", "Share permissions",
         "Folders with permissions not read", "Local groups", "Contents read", "Partly read", "Not read yet",
         .. NotReadReasons.Select(r => $"Not read: {Reason(r)}")],
        table);
    }

    internal static string Reason(ContentState state) => state switch
    {
        ContentState.Unsupported => "unsupported type",
        ContentState.TooLarge => "too large",
        ContentState.Offline => "offline or cloud-only",
        ContentState.AccessDenied => "access denied",
        ContentState.NotFound => "gone before reading",
        ContentState.Encrypted => "encrypted",
        ContentState.Unreadable => "damaged or unsafe",
        ContentState.TimedOut => "timed out",
        _ => "read error",
    };
}
