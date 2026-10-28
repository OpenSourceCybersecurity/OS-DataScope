using DataScope.Api.Access;
using DataScope.Api.Compliance;
using DataScope.Api.Endpoints;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>
/// Evidence for one compliance standard (D135): where the data it covers was found, who can read it and through which
/// groups, the folder owners' access reviews, and what Data Scope could not check. Built from the same scope, access
/// engine and exposure map as the Compliance standards and Exposure map screens, so the numbers agree. It is evidence
/// for an assessment; it never says anything is compliant.
/// </summary>
internal sealed class ComplianceEvidenceReport : IReport
{
    /// <summary>Groups and accounts named per folder before the rest are counted.</summary>
    internal const int MaxTrusteesPerFolder = 6;

    public ReportInfo Info { get; } = new("compliance-evidence", "Compliance evidence",
        "For one compliance standard you turned on: where the data it covers was found, who can read it and through which groups, the owners' access reviews, and what Data Scope could not check. For your own assessment or an assessor's; it never says you are compliant.",
        Policies.ReadReviews, Standard: true);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var std = ctx.Standard ?? throw new InvalidOperationException("The compliance evidence report needs a standard.");
        var db = ctx.Db;
        var today = DateOnly.FromDateTime(ctx.Now.UtcDateTime);

        // The files under the standard, counted as the Compliance standards screen counts them.
        var covered = ComplianceScope.Covered(db, ctx.Files, std);
        var labels = await covered.GroupBy(f => f.OverrideSensitivity ?? f.Sensitivity).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var total = labels.Sum(l => l.Count);
        var restricted = labels.Where(l => l.Key == Sensitivity.Restricted).Sum(l => l.Count);
        var confidential = labels.Where(l => l.Key == Sensitivity.Confidential).Sum(l => l.Count);
        var folderCount = await covered.Select(f => f.FolderId).Distinct().LongCountAsync(ct);
        var shareCount = await covered.Select(f => f.ShareId).Distinct().LongCountAsync(ct);
        var yearAgo = ctx.Now.AddYears(-1);
        var threeYearsAgo = ctx.Now.AddYears(-3);
        var olderThanYear = await covered.LongCountAsync(f => f.ModifiedAtSource < yearAgo, ct);
        var olderThanThree = await covered.LongCountAsync(f => f.ModifiedAtSource < threeYearsAgo, ct);

        // Who can read each folder holding that data, from the exposure map narrowed to the standard.
        var map = await ExposureMap.BuildAsync(db, ctx.Access, Sensitivity.Confidential, ctx.Scope.ServerId, ctx.Scope.ShareId, std, ct);
        var folderIds = map.Folders.Select(f => f.Place.FolderId).ToList();
        var categories = std.Categories.ToList();
        var confidences = Enum.GetValues<Confidence>().Where(c => c >= std.MinConfidence).ToList();
        var kinds = (await (from f in covered
                            from x in db.Findings
                            where x.FileId == f.Id && folderIds.Contains(f.FolderId) && categories.Contains(x.Category) && confidences.Contains(x.Confidence)
                            select new { f.FolderId, x.Category }).Distinct().ToListAsync(ct))
            .GroupBy(k => k.FolderId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(k => k.Category).Order().Select(c => c.Label())));
        var changed = await covered.Where(f => folderIds.Contains(f.FolderId)).GroupBy(f => f.FolderId)
            .Select(g => new { g.Key, Last = g.Max(f => f.ModifiedAtSource) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);

        // Each folder's owner is the owner of the nearest folder at or above it that has one; reviews are of that folder.
        var shareIds = map.Folders.Select(f => f.Place.ShareId).Distinct().ToList();
        var owners = await (from a in db.OwnerAssignments.AsNoTracking()
                            join f in db.Folders on a.FolderId equals f.Id
                            where a.EndedAt == null && shareIds.Contains(a.ShareId)
                            select new Owned(a.ShareId, a.FolderId, f.Path, a.OwnerSid, a.OwnerName, a.ReviewFrequency)).ToListAsync(ct);
        var reviewed = await OwnerEndpoints.LastReviewedAsync(db, owners.Select(o => o.FolderId).ToList(), ct);
        var ownerStatus = await OwnerEndpoints.StatusAsync(db, directory, owners.Select(o => o.OwnerSid), ct);

        var review = new Dictionary<string, int> { ["current"] = 0, ["overdue"] = 0, ["never"] = 0, ["none"] = 0 };
        var rows = new List<object?[]>();
        foreach (var f in map.Folders
            .OrderBy(f => StatusOrder(f.Status)).ThenByDescending(f => f.Restricted).ThenByDescending(f => f.Files)
            .ThenBy(f => f.Unc, StringComparer.OrdinalIgnoreCase))
        {
            var owner = owners
                .Where(o => o.ShareId == f.Place.ShareId && IsAtOrAbove(o.Path, f.Place.Path))
                .MaxBy(o => o.Path.Length);
            var (reviewText, reviewKind) = ReviewStatus(owner, reviewed, today);
            review[reviewKind]++;
            var ownerText = owner is null ? null : owner.OwnerName + ownerStatus.GetValueOrDefault(owner.OwnerSid) switch
            {
                "Disabled" => " (account disabled)",
                "Missing" => " (no longer in Active Directory)",
                _ => "",
            };
            var unknown = f.Exposure.Unknown;
            rows.Add(
            [
                f.Place.Hostname, f.Place.UncPath, f.Unc, f.Files, f.Restricted, f.Confidential, kinds.GetValueOrDefault(f.Place.FolderId),
                changed.GetValueOrDefault(f.Place.FolderId), ShareOwnerReport.Exposure(f.Exposure.AnyDomainUser),
                unknown ? null : f.PeopleCertain, unknown ? null : f.PeoplePossible,
                unknown ? "Unknown (permissions not read)" : ReadThrough(f, map.GraphOf(f.Place.ServerId)),
                ownerText, reviewText,
            ]);
        }

        var facts = new List<ReportFact>
        {
            new("Standard", $"{std.Name}. {std.Definition.Summary}"),
            new("Turned on", std.UpdatedAt is { } at
                ? $"{ReportWriters.Text(at)} by {std.UpdatedByName}. Reason given: {std.Reason}"
                : "Yes"),
            new("What counts", $"{string.Join(", ", std.Categories.Select(c => c.Label()))}, found at {std.MinConfidence} confidence or higher. A file a reviewer labelled Internal or Public does not count."),
        };
        facts.AddRange(await ctx.CommonFactsAsync(ct));
        facts.Add(new("Data found", total == 0
            ? "No file in scope holds data this standard covers, among the files whose contents were read."
            : $"{total:N0} {Plural(total, "file", "files")} ({restricted:N0} Restricted, {confidential:N0} Confidential) in {folderCount:N0} {Plural(folderCount, "folder", "folders")} on {shareCount:N0} {Plural(shareCount, "share", "shares")}."));
        if (total > 0)
        {
            facts.Add(new("Age of the data",
                $"{olderThanYear:N0} of these files were last changed over a year ago, {olderThanThree:N0} over three years ago, by Windows' last-write time."));
        }
        var open = map.Folders.Where(f => f.Status == "Open").ToList();
        facts.Add(new("Who can read it",
            $"Folders any domain user can read: {open.Count:N0} ({open.Sum(f => (long)f.Files):N0} files). " +
            $"Possibly: {map.Folders.Count(f => f.Status == "PossiblyOpen"):N0}. Only some people: {map.Folders.Count(f => f.Status == "Limited"):N0}. " +
            $"Unknown, because their permissions were not read: {map.Folders.Count(f => f.Status == "Unknown"):N0}."));
        facts.Add(new("Access reviews",
            $"Folders whose owner reviewed access within their review frequency: {review["current"]:N0} of {map.Folders.Count:N0}. " +
            $"Overdue: {review["overdue"]:N0}. Never reviewed: {review["never"]:N0}. No owner: {review["none"]:N0}."));
        facts.Add(new("Files not read", await NotReadAsync(ctx, ct)));
        facts.AddRange(std.Definition.Requirements.Select(r => new ReportFact($"Requirement {r.Ref}", $"{r.Text} Relevant here: {r.Helps}")));

        var limitations = new List<string>
        {
            "Data Scope does not assess compliance and never says that data or an organisation is compliant. This report is evidence for your own assessment or an assessor's: where data the standard covers was found, who can read it, the access reviews done, and what could not be checked.",
            $"Matches are possible matches with a confidence, found by patterns and the words around them. They are not confirmed {std.Name} data: check a sample before relying on the counts.",
            "Only files whose contents were read are counted. Files not read may hold data this standard covers and are never counted as clean.",
        };
        limitations.AddRange(std.Definition.NotDetected.Select(n => $"Not detected: {n}"));
        limitations.Add("Who can read is computed from the permissions the latest scan read and the latest directory sync. It is not a Windows access check and not a record of anyone opening a file. Possibly and Unknown mean Data Scope could not be sure.");
        limitations.Add("Access reviews record what the folder's owner decided; Data Scope does not track whether IT made the changes. A folder counts as reviewed through the nearest folder at or above it that has an owner.");
        if (map.Truncated)
        {
            limitations.Add($"Only the {AccessEndpoints.MaxExposureFolders:N0} folders with the most Restricted files are listed and counted under who can read it and access reviews. Narrow the scope to see the rest.");
        }
        limitations.Add("Changes to this standard's settings, with who made them and why, are in the audit log.");

        return ctx.Document(Info with { Title = $"Compliance evidence: {std.Name}" }, facts, limitations,
            ["Server", "Share", "Folder", "Files under the standard", "Restricted", "Confidential", "Kinds of data", "Last changed",
             "Any domain user can read", "People who can read", "People who might read", "Read access through", "Owner", "Access review"],
            rows, subject: std.Id);
    }

    private sealed record Owned(Guid ShareId, Guid FolderId, string Path, string OwnerSid, string OwnerName, ReviewFrequency Frequency);

    /// <summary>Folders any domain user can read first, then those it possibly can, then unknown, then limited.</summary>
    private static int StatusOrder(string status) => status switch
    {
        "Open" => 0,
        "PossiblyOpen" => 1,
        "Unknown" => 2,
        _ => 3,
    };

    /// <summary>Whether the folder at <paramref name="owned"/> is <paramref name="path"/> or one of its parents, in the same share.</summary>
    internal static bool IsAtOrAbove(string owned, string path) =>
        owned.Length == 0
        || string.Equals(owned, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(owned.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static (string Text, string Kind) ReviewStatus(Owned? owner, Dictionary<Guid, DateOnly> reviewed, DateOnly today)
    {
        if (owner is null)
        {
            return ("No owner, so not reviewed", "none");
        }
        if (!reviewed.TryGetValue(owner.FolderId, out var last))
        {
            return ("Never reviewed", "never");
        }
        var due = last.AddMonths(owner.Frequency.Months());
        var on = ReportWriters.Text(last);
        return due <= today
            ? ($"Overdue: last reviewed {on}, due {ReportWriters.Text(due)}", "overdue")
            : ($"Reviewed {on}, next due {ReportWriters.Text(due)}", "current");
    }

    /// <summary>
    /// The groups and accounts whose permission entries let people read the folder: everyone-in-the-domain groups
    /// first, then by name, the first few named and the rest counted.
    /// </summary>
    private static string ReadThrough(SensitiveFolder folder, DirectoryGraph graph)
    {
        var trustees = folder.Exposure.Readers.SelectMany(r => r.Grants.Select(g => g.Trustee))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(sid => (Broad: WellKnownSid.IsBroad(sid), Name: AccessViews.Trustee(graph, sid)?.Name ?? sid))
            .OrderByDescending(t => t.Broad).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var named = string.Join("; ", trustees.Take(MaxTrusteesPerFolder).Select(t => t.Broad ? $"{t.Name} (everyone in the domain)" : t.Name));
        return trustees.Count > MaxTrusteesPerFolder ? $"{named}; and {trustees.Count - MaxTrusteesPerFolder:N0} more" : named;
    }

    /// <summary>Files in scope whose contents were not read, by reason: any of them may hold data the standard covers.</summary>
    private static async Task<string> NotReadAsync(ReportContext ctx, CancellationToken ct)
    {
        var states = await ctx.Files.Where(f => f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial)
            .GroupBy(f => f.ContentState).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var count = states.Sum(s => s.Count);
        if (count == 0)
        {
            return "None: the contents of every file in scope were read.";
        }
        var reasons = states.OrderByDescending(s => s.Count).Select(s => $"{Why(s.Key)} {s.Count:N0}");
        return $"{count:N0} {Plural(count, "file", "files")} whose contents were not read may hold data this standard covers: {string.Join(", ", reasons)}.";
    }

    private static string Why(ContentState state) => state switch
    {
        ContentState.NotRead => "not read yet",
        ContentState.Excluded => "excluded by an administrator",
        _ when CoverageReport.NotReadReasons.Contains(state) => CoverageReport.Reason(state),
        _ => state.ToString(),
    };

    private static string Plural(long n, string one, string many) => n == 1 ? one : many;
}
