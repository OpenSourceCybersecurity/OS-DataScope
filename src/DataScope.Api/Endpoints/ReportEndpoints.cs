using System.Security.Claims;
using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Api.Hosting;
using DataScope.Api.Cleanup;
using DataScope.Api.Compliance;
using DataScope.Api.Progress;
using DataScope.Api.Reports;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Reports (CSV, or a print-ready page people save as PDF) and the overview dashboard. Every report states when it was
/// made, its scope, how fresh and complete the data is, and what it cannot show. Each export is audited.
/// </summary>
internal static class ReportEndpoints
{
    public static readonly IReadOnlyList<IReport> All =
    [
        new SensitiveDataReport(),
        new ShareOwnerReport(),
        new GroupAccessReport(),
        new RiskyPermissionsReport(),
        new ReviewEvidenceReport(),
        new CoverageReport(),
        new FileActivityReport(),
        new ActivityAlertsReport(),
        new StaleDataReport(),
        new DuplicateFilesReport(),
        new ProgressReport(),
        new ComplianceEvidenceReport(),
    ];

    /// <param name="Period">The report covers a number of days (the <c>days</c> query value) rather than the latest state.</param>
    /// <param name="Standard">The report is about one turned-on compliance standard, named by the <c>standard</c> query value.</param>
    public sealed record ReportInfoDto(string Id, string Title, string About, bool Period, bool Standard);

    public sealed record DashboardCoverage(
        int Servers, int SharesIncluded, int SharesScanned, int SharesPartial, int SharesUnavailable, int SharesNotScanned,
        long Files, long Bytes, long FilesRead, long FilesNotRead, int Gaps, DateTimeOffset? LastScanAt);

    public sealed record DashboardSensitive(long Restricted, long Confidential, string PolicyVersion);

    public sealed record DashboardExposure(int Open, int PossiblyOpen, int Unknown, long SensitiveFilesOpen, bool Truncated, IReadOnlyList<AccessEndpoints.ExposureItemDto> Top);

    public sealed record DashboardOwners(int Owned, int Unowned, int OrphanedOwners);

    public sealed record DashboardReviews(int OpenCampaigns, int ReviewsOpen, int ReviewsOverdue);

    /// <summary>Files by how long ago they were last changed, by Windows' last-write time. Last access is never used.</summary>
    public sealed record AgeBucket(string Label, long Files, long Bytes);

    /// <summary>Totals across the included shares at the end of a day on which a scan finished, from each share's latest scan by then.</summary>
    public sealed record TrendPoint(DateOnly Day, int Shares, long Files, long Bytes, long Restricted, long Confidential, long FilesNotRead);

    public sealed record DashboardDto(
        DateTimeOffset GeneratedAt, string? Domain, DateTimeOffset? DirectorySyncedAt, DashboardCoverage Coverage, DashboardSensitive Sensitive,
        DashboardExposure Exposure, DashboardOwners Owners, DashboardReviews Reviews, IReadOnlyList<AgeBucket> Age,
        IReadOnlyList<TrendPoint> Trend);

    /// <summary>The trend covers this many days, with at most <see cref="MaxTrendPoints"/> points (thinned to one per week).</summary>
    public const int TrendDays = 365;

    public const int MaxTrendPoints = 60;

    public static void MapReports(this RouteGroupBuilder api)
    {
        // Every signed-in person may ask; each report then checks its own policy.
        var group = api.MapGroup("/reports").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IAuthorizationService authz) =>
        {
            var allowed = new List<ReportInfoDto>();
            foreach (var report in All)
            {
                if ((await authz.AuthorizeAsync(user, report.Info.Policy)).Succeeded)
                {
                    allowed.Add(new ReportInfoDto(report.Info.Id, report.Info.Title, report.Info.About, report.Info.Period, report.Info.Standard));
                }
            }
            return allowed;
        });

        group.MapGet("/{id}", async (
            string id, string? format, Guid? serverId, Guid? shareId, int? days, string? standard, HttpContext http, ClaimsPrincipal user, IAuthorizationService authz,
            DataScopeDbContext db, AccessService access, DirectoryStore directory, AuditLogWriter audit, TimeProvider clock, DataCleanup cleanup, CancellationToken ct) =>
        {
            var report = All.FirstOrDefault(r => r.Info.Id == id);
            if (report is null)
            {
                return Results.NotFound();
            }
            if (!(await authz.AuthorizeAsync(user, report.Info.Policy)).Succeeded)
            {
                return Results.Forbid();
            }
            var errors = new Dictionary<string, string[]>();
            var fmt = ReportFormat.Csv;
            if (format is not null && !Enum.TryParse(format, ignoreCase: true, out fmt) || !Enum.IsDefined(fmt))
            {
                errors["format"] = ["Use csv or html."];
            }
            if (days is not null && (!report.Info.Period || days is < 1 or > ReportContext.MaxDays))
            {
                errors["days"] = [report.Info.Period ? $"Use 1 to {ReportContext.MaxDays} days." : "This report shows the latest state, not a period."];
            }
            EffectiveStandard? std = null;
            if (report.Info.Standard)
            {
                (std, var standardProblem) = await ComplianceScope.FilterAsync(db, standard, ct);
                if (standardProblem is not null || std is null)
                {
                    errors["standard"] = [standardProblem ?? "Choose a compliance standard that is turned on."];
                }
            }
            else if (standard is not null)
            {
                errors["standard"] = ["This report is not about one compliance standard."];
            }
            var (scope, scopeError) = await ReportContext.ResolveAsync(db, serverId, shareId, ct);
            if (scopeError is not null)
            {
                errors[shareId is null ? "serverId" : "shareId"] = [scopeError];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = Actor.From(user);
            var ctx = new ReportContext(db, access, actor, scope!, clock.GetUtcNow(), ReportLimits.For(fmt), days ?? ReportContext.DefaultDays)
            {
                Cleanup = cleanup,
                Standard = std,
                Progress = report is ProgressReport ? http.RequestServices.GetRequiredService<ProgressRecorder>() : null,
            };
            var document = await report.BuildAsync(ctx, directory, ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "report.export", report.Info.Title,
                new { Report = report.Info.Id, Format = fmt.ToString(), Scope = scope!.Description, Days = report.Info.Period ? ctx.Days : (int?)null, Standard = std?.Id, Rows = document.Rows.Count, document.Truncated }, ct);
            var fileName = ReportWriters.FileName(document, fmt);
            if (fmt == ReportFormat.Html)
            {
                // The page is static: its one stylesheet, by hash, and nothing else.
                http.Response.Headers.ContentSecurityPolicy = ReportWriters.HtmlContentPolicy;
            }
            return Results.Stream(stream => ReportWriters.WriteAsync(document, fmt, stream, ct), ReportWriters.ContentType(fmt),
                // A CSV downloads; a page opens in the browser, where people print it or save it as PDF.
                fmt == ReportFormat.Csv ? fileName : null);
        }).RequireRateLimiting(RateLimits.Reports);

        api.MapGet("/dashboard", async (DataScopeDbContext db, AccessService access, DirectoryStore directory, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var shares = await db.Shares.AsNoTracking().Where(s => s.State != ShareState.Removed && s.Kind == ShareKind.Disk)
                .Select(s => new { s.Included, s.State, s.LastScannedAt, s.LastScanJobId, s.Id, s.FileCount, s.TotalBytes }).ToListAsync(ct);
            var included = shares.Where(s => s.Included).ToList();
            var jobs = shares.Where(s => s.LastScanJobId != null).Select(s => s.LastScanJobId!.Value).Distinct().ToList();
            var gaps = await (from g in db.ScanGaps.AsNoTracking()
                              join s in db.Shares on g.ShareId equals s.Id
                              where jobs.Contains(g.JobId) && g.JobId == s.LastScanJobId
                              select g.Id).CountAsync(ct);
            var files = db.Files.AsNoTracking().Where(f => f.RemovedAt == null);
            var states = await files.GroupBy(f => f.ContentState).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
            var labels = await files.Where(f => SensitiveDataReport.SensitiveLabels.Contains(f.OverrideSensitivity ?? f.Sensitivity))
                .GroupBy(f => f.OverrideSensitivity ?? f.Sensitivity).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
            var read = states.Where(s => s.Key is ContentState.Scanned or ContentState.Partial).Sum(s => s.Count);

            var exposure = await AccessEndpoints.ExposureAsync(db, access, Sensitivity.Confidential, null, null, ct);

            var owned = await db.OwnerAssignments.AsNoTracking().Where(a => a.EndedAt == null).Select(a => a.OwnerSid).ToListAsync(ct);
            var ownerStatus = await OwnerEndpoints.StatusAsync(db, directory, owned, ct);
            var unowned = await (from f in db.Folders
                                 join s in db.Shares on f.ShareId equals s.Id
                                 where f.Depth == 0 && f.RemovedAt == null && s.Kind == ShareKind.Disk && s.Included && s.State != ShareState.Removed
                                     && !db.OwnerAssignments.Any(a => a.ShareId == s.Id && a.EndedAt == null)
                                 select f.Id).CountAsync(ct);

            var campaigns = await db.ReviewCampaigns.AsNoTracking().Where(c => c.State == CampaignState.Open).Select(c => new { c.Id, c.DueOn }).ToListAsync(ct);
            var campaignIds = campaigns.Select(c => c.Id).ToList();
            var openReviews = await db.AccessReviews.AsNoTracking().Where(r => campaignIds.Contains(r.CampaignId) && r.State != ReviewState.Submitted)
                .Select(r => r.CampaignId).ToListAsync(ct);
            var overdueCampaigns = campaigns.Where(c => ReviewSchedule.IsOverdue(c.DueOn, now)).Select(c => c.Id).ToHashSet();

            var age = await AgeAsync(db, now, ct);
            var trend = await TrendAsync(db, included.Select(s => s.Id).ToList(), now, ct);
            var snapshot = await access.SnapshotAsync(ct);
            return new DashboardDto(
                now, snapshot?.Domain, snapshot?.CompletedAt,
                new DashboardCoverage(
                    await db.ManagedServers.CountAsync(s => s.State != EnrollmentState.Retired, ct),
                    included.Count, included.Count(s => s.State == ShareState.Scanned), included.Count(s => s.State == ShareState.Partial),
                    included.Count(s => s.State == ShareState.Unavailable), included.Count(s => s.State == ShareState.NotScanned),
                    shares.Sum(s => s.FileCount), shares.Sum(s => s.TotalBytes), read, states.Sum(s => s.Count) - read, gaps,
                    shares.Max(s => s.LastScannedAt)),
                new DashboardSensitive(
                    labels.Where(l => l.Key == Sensitivity.Restricted).Sum(l => l.Count),
                    labels.Where(l => l.Key == Sensitivity.Confidential).Sum(l => l.Count),
                    await CustomRuleStore.PolicyVersionAsync(db, ct)),
                new DashboardExposure(
                    exposure.Items.Count(i => i.Status == "Open"), exposure.Items.Count(i => i.Status == "PossiblyOpen"),
                    exposure.Items.Count(i => i.Status == "Unknown"), exposure.SensitiveFilesOpen, exposure.Truncated,
                    exposure.Items.Where(i => i.Status != "Unknown").Take(5).ToList()),
                new DashboardOwners(owned.Count, unowned, owned.Count(sid => ownerStatus.GetValueOrDefault(sid) is "Disabled" or "Missing")),
                new DashboardReviews(campaigns.Count, openReviews.Count, openReviews.Count(overdueCampaigns.Contains)),
                age, trend);
        }).RequireAuthorization(Policies.ReadPosture);
    }

    /// <summary>
    /// One point per day on which a scan finished, over the last year, adding up each included share's latest scan as of
    /// that day. Shares not scanned yet by a day don't count towards it.
    /// </summary>
    internal static async Task<IReadOnlyList<TrendPoint>> TrendAsync(DataScopeDbContext db, List<Guid> shareIds, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-TrendDays);
        var history = db.ShareScanHistory.AsNoTracking().Where(h => shareIds.Contains(h.ShareId));
        var baseline = await history.Where(h => h.CompletedAt < since).GroupBy(h => h.ShareId)
            .Select(g => g.OrderByDescending(h => h.CompletedAt).First()).ToListAsync(ct);
        var rows = await history.Where(h => h.CompletedAt >= since).OrderBy(h => h.CompletedAt).ToListAsync(ct);

        var latest = baseline.ToDictionary(h => h.ShareId);
        var points = new List<TrendPoint>();
        foreach (var day in rows.GroupBy(h => DateOnly.FromDateTime(h.CompletedAt.UtcDateTime)))
        {
            foreach (var row in day)
            {
                latest[row.ShareId] = row;
            }
            var v = latest.Values;
            points.Add(new TrendPoint(day.Key, v.Count, v.Sum(h => h.Files), v.Sum(h => h.Bytes), v.Sum(h => h.RestrictedFiles),
                v.Sum(h => h.ConfidentialFiles), v.Sum(h => h.FilesNotRead)));
        }
        if (points.Count <= MaxTrendPoints)
        {
            return points;
        }
        // Daily scans over a year: keep each week's last day.
        return [.. points.GroupBy(p => p.Day.DayNumber / 7).Select(g => g.Last()).TakeLast(MaxTrendPoints)];
    }

    /// <summary>Current files grouped by years since they were last written.</summary>
    private static async Task<IReadOnlyList<AgeBucket>> AgeAsync(DataScopeDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var year1 = now.AddYears(-1);
        var year3 = now.AddYears(-3);
        var year7 = now.AddYears(-7);
        var rows = await db.Files.AsNoTracking().Where(f => f.RemovedAt == null)
            .GroupBy(f => f.ModifiedAtSource == null ? 4 : f.ModifiedAtSource >= year1 ? 0 : f.ModifiedAtSource >= year3 ? 1 : f.ModifiedAtSource >= year7 ? 2 : 3)
            .Select(g => new { g.Key, Files = g.LongCount(), Bytes = g.Sum(f => f.Size) })
            .ToListAsync(ct);
        string[] labels = ["Changed in the last year", "1 to 3 years", "3 to 7 years", "Over 7 years", "Unknown"];
        return labels.Select((label, i) => new AgeBucket(label, rows.Where(r => r.Key == i).Sum(r => r.Files), rows.Where(r => r.Key == i).Sum(r => r.Bytes))).ToList();
    }
}
