using DataScope.Core.Activity;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>
/// The statement file activity reports open with (A15): the period, which servers were collecting, and what is missing
/// from it (events lost before Data Scope read them, cleared logs). Scan and directory facts do not apply here.
/// </summary>
internal static class ActivityFacts
{
    public static async Task<List<ReportFact>> ForAsync(ReportContext ctx, DateTimeOffset since, CancellationToken ct)
    {
        var db = ctx.Db;
        var facts = new List<ReportFact>
        {
            new("Scope", ctx.Scope.Description),
            new("Period", $"{ReportWriters.Text(since)} to {ReportWriters.Text(ctx.Now)} (the last {ctx.Days} days)"),
        };

        var servers = await (from m in db.ManagedServers.AsNoTracking()
                             where ctx.Scope.ServerId == null ? m.State != EnrollmentState.Retired : m.Id == ctx.Scope.ServerId
                             join s in db.ActivitySources.AsNoTracking() on m.Id equals s.ServerId into sources
                             from s in sources.DefaultIfEmpty()
                             orderby m.Hostname
                             select new { m.Id, m.Hostname, Enabled = s != null && s.Enabled, State = s == null ? (ActivitySourceState?)null : s.State, LastReadAt = s == null ? null : s.LastReadAt, LastError = s == null ? null : s.LastError })
            .ToListAsync(ct);
        var collecting = servers.Where(s => s.Enabled).ToList();
        var off = servers.Where(s => !s.Enabled).Select(s => s.Hostname).ToList();
        var failing = collecting.Where(s => s.State == ActivitySourceState.Error).Select(s => $"{s.Hostname} ({s.LastError})").ToList();
        var lastRead = collecting.Min(s => s.LastReadAt);
        facts.Add(new("Collection",
            $"{collecting.Count} of {servers.Count} file servers collecting" +
            (off.Count > 0 ? $". Off, so nothing is known about them: {string.Join(", ", off)}" : "") +
            (failing.Count > 0 ? $". Last read failed: {string.Join("; ", failing)}" : "") +
            (lastRead is { } r ? $". Every collecting server was last read at or after {ReportWriters.Text(r)}" : "")));

        var ids = servers.Select(s => s.Id).ToList();
        var gaps = await db.ActivityGaps.AsNoTracking().Where(g => ids.Contains(g.ServerId) && g.DetectedAt >= since).ToListAsync(ct);
        var lost = gaps.Where(g => g.LastRecordId != null).Sum(g => g.LastRecordId!.Value - g.FirstRecordId + 1);
        var resets = gaps.Count(g => g.Reason == ActivityGapReason.LogReset);
        facts.Add(new("Events missed", gaps.Count == 0
            ? "None recorded: Data Scope read every Security log record before Windows overwrote it."
            : $"{gaps.Count} times Data Scope found records gone before it read them ({lost:N0} log records overwritten or cleared" +
              (resets > 0 ? $", and {resets} logs that started again with an unknown number lost" : "") +
              "). Some of them may not have been file events. Totals in this report can be low."));

        var cleared = await db.FileAuditEvents.AsNoTracking()
            .CountAsync(e => ids.Contains(e.ServerId) && e.EventTime >= since && e.Operation == ActivityOperation.LogCleared, ct);
        facts.Add(new("Security log cleared", cleared == 0 ? "Not in this period" : $"{cleared} times in this period. Events before each clear may be missing."));
        return facts;
    }

    public static readonly string[] Limitations =
    [
        "Only what each file server's audit policy and folder audit settings ask Windows to log. A folder without auditing has no events, and no events never means nobody opened it.",
        "Windows logs a right once per open handle, so Read means a file was opened and read from, not how much or how often.",
        "A computer that already has a file open or cached through the share can use it again without asking the file server, so repeat use can leave no new event.",
        "The person is who Windows recorded. Through a service or an application running as a service account, that is the account, not the person behind it.",
    ];
}

/// <summary>Recorded file operations and requests in a period, newest first. Access checks and share connections are left out.</summary>
internal sealed class FileActivityReport : IReport
{
    public ReportInfo Info { get; } = new("file-activity", "File activity",
        "What Windows recorded people doing to files and folders in a period: reads, writes, deletes and permission changes, who did them, and how much each event proves.",
        Policies.ReadActivity, Period: true);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        var since = ctx.Now.AddDays(-ctx.Days);
        var events = await (from e in db.FileAuditEvents.AsNoTracking()
                            where e.EventTime >= since
                                  && (e.Certainty == ActivityCertainty.Performed || e.Certainty == ActivityCertainty.Requested)
                                  && (ctx.Scope.ServerId == null || e.ServerId == ctx.Scope.ServerId)
                                  && (ctx.Scope.ShareId == null || e.ShareId == ctx.Scope.ShareId)
                            join server in db.ManagedServers on e.ServerId equals server.Id
                            join share in db.Shares on e.ShareId equals share.Id into shares
                            from share in shares.DefaultIfEmpty()
                            orderby e.EventTime descending, e.Id descending
                            select new { e, server.Hostname, ShareName = share == null ? null : share.Name })
            .Take(ctx.RowLimit + 1).ToListAsync(ct);

        var rows = events.Select(x =>
        {
            var e = x.e;
            return new object?[]
            {
                e.EventTime, x.Hostname, x.ShareName, x.ShareName is null ? e.ObjectPath : e.RelativePath,
                e.ObjectKind == ActivityObjectKind.Unknown ? null : e.ObjectKind.ToString(), ActivityText.Operation(e.Operation, e.ObjectKind),
                e.Certainty == ActivityCertainty.Performed ? "Recorded" : "Request only", e.Outcome == ActivityOutcome.Failure ? "Denied" : "Allowed",
                e.PrincipalName, e.PrincipalSid, e.EventId, e.RecordId,
                e.PathSource == ActivityPathSource.Correlated ? $"From record {e.CorrelatedRecordId} on the same handle" : e.ObjectPath is null ? "No path in the event" : "In the event",
                e.ClientAddress,
            };
        }).ToList();

        var facts = await ActivityFacts.ForAsync(ctx, since, ct);
        facts.Add(new("Events listed", "Recorded operations (4663, 4660, 4670, 1102) and open requests (4656). Share access checks (5145) and share connections (5140) are left out and are never counted as reads."));
        return ctx.Document(Info, facts,
        [
            .. ActivityFacts.Limitations,
            "\"Request only\" means someone asked Windows to open the item with those rights. It does not show what they did with it.",
            "A delete event (4660) names no file; its path comes from the earlier event on the same handle, or is blank when that was not collected.",
            "With a share in scope, events in folders that match no share are left out. Choose the file server to see them.",
        ],
        ["Time", "File server", "Share", "Path", "File or folder", "Operation", "What it proves", "Result", "Who", "Account SID", "Event ID", "Log record",
         "Where the path came from", "Client address"],
        rows);
    }
}

/// <summary>Alerts raised in a period with who acknowledged them and why.</summary>
internal sealed class ActivityAlertsReport : IReport
{
    public ReportInfo Info { get; } = new("activity-alerts", "File activity alerts",
        "Mass deletes, many refused attempts, permission changes, cleared logs and missed events in a period, with who acknowledged each and their note.",
        Policies.ReadActivity, Period: true);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        var since = ctx.Now.AddDays(-ctx.Days);
        var alerts = await (from a in db.ActivityAlerts.AsNoTracking()
                            where a.LastEventAt >= since && (ctx.Scope.ServerId == null || a.ServerId == ctx.Scope.ServerId)
                            join server in db.ManagedServers on a.ServerId equals server.Id
                            orderby a.LastEventAt descending, a.Id descending
                            select new { a, server.Hostname })
            .Take(ctx.RowLimit + 1).ToListAsync(ct);
        var rows = alerts.Select(x =>
        {
            var a = x.a;
            return new object?[]
            {
                ActivityAlertText.Title(a.Rule), a.Severity.ToString(), a.State.ToString(), x.Hostname, a.PrincipalName,
                a.PrincipalSid, a.FirstEventAt,
                a.LastEventAt, a.EventCount, ActivityAlertText.Describe(a.Rule, a.PrincipalName, a.EventCount, x.Hostname, a.FirstEventAt, a.LastEventAt),
                a.AcknowledgedByName, a.AcknowledgedAt, a.Note,
            };
        }).ToList();

        var facts = await ActivityFacts.ForAsync(ctx, since, ct);
        if (ctx.Scope.ShareId is not null)
        {
            facts.Add(new("Alerts", "Alerts are raised per file server, so this lists every alert on the share's server."));
        }
        var open = alerts.Count(x => x.a.State == ActivityAlertState.Open);
        facts.Add(new("Alerts listed", $"{alerts.Count:N0} alerts, {open:N0} not acknowledged"));
        return ctx.Document(Info, facts,
        [
            .. ActivityFacts.Limitations,
            "An alert is a pattern worth a look, not proof of wrongdoing: a planned clean-up or a permissions project raises the same alerts as an attack.",
            "Alerts come from the events Data Scope collected. Activity on a server that was off, or in events that were missed, raises no alert; missed events raise their own alert.",
            "Acknowledging an alert records who looked at it and why. It changes nothing on the file server.",
        ],
        ["Alert", "Severity", "State", "File server", "Who", "Account SID", "First event", "Last event", "Events", "Description", "Acknowledged by", "Acknowledged", "Note"],
        rows);
    }
}
