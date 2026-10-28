using System.Globalization;
using DataScope.Core.Activity;
using DataScope.Core.Governance;
using DataScope.Core.Privacy;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>
/// Runs the alert rules over a server's newly stored events (D105). Each rule looks for bursts by one person; a burst
/// that continues an alert raised earlier extends it instead of raising another. Missed events (gaps) raise one alert
/// each. New alerts can be emailed through the existing outbox. Nothing here changes an event.
/// </summary>
public sealed class ActivityAlertStore(DataScopeDbContext db, NotificationStore outbox, TimeProvider clock)
{
    /// <summary>Events looked at per step, so a large backlog is worked through in bounded memory.</summary>
    public const int Step = 50_000;

    public const string AlertRecipientSid = "activity-alerts";

    public async Task<IReadOnlyList<ActivityAlert>> DetectAsync(Guid serverId, string channel, ActivityAlertOptions options, string? publicUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var raised = new List<ActivityAlert>();
        if (!options.Enabled)
        {
            return raised;
        }
        var hostname = await db.ManagedServers.Where(s => s.Id == serverId).Select(s => s.Hostname).SingleAsync(ct);
        raised.AddRange(await RaiseForGapsAsync(serverId, ct));

        var checkedId = await db.ActivitySources.Where(s => s.ServerId == serverId && s.Channel == channel).Select(s => s.AlertsCheckedEventId).SingleAsync(ct);
        var maxId = await db.FileAuditEvents.Where(e => e.ServerId == serverId && e.Channel == channel).MaxAsync(e => (long?)e.Id, ct) ?? 0;
        while (checkedId < maxId)
        {
            var to = Math.Min(checkedId + Step, maxId);
            foreach (var rule in options.BurstRules())
            {
                raised.AddRange(await DetectAsync(serverId, channel, rule, checkedId, to, ct));
            }
            await db.ActivitySources.Where(s => s.ServerId == serverId && s.Channel == channel)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AlertsCheckedEventId, to), ct);
            checkedId = to;
        }

        foreach (var alert in raised.Where(a => a.Severity == ActivityAlertSeverity.High || options.EmailMedium))
        {
            await EmailAsync(outbox, alert, hostname, options, publicUrl, ct);
        }
        return raised;
    }

    private static IQueryable<FileAuditEvent> Matching(IQueryable<FileAuditEvent> events, ActivityAlertRule rule) => rule switch
    {
        ActivityAlertRule.MassDelete => events.Where(e => e.Operation == ActivityOperation.Deleted && e.Outcome == ActivityOutcome.Success),
        ActivityAlertRule.ManyDenied => events.Where(e => e.Outcome == ActivityOutcome.Failure),
        ActivityAlertRule.PermissionChange => events.Where(e => (e.Operation == ActivityOperation.PermissionChange || e.Operation == ActivityOperation.OwnerChange)
            && e.Outcome == ActivityOutcome.Success && e.Certainty == ActivityCertainty.Performed),
        ActivityAlertRule.LogCleared => events.Where(e => e.Operation == ActivityOperation.LogCleared),
        _ => events.Where(e => false),
    };

    /// <summary>New alerts from one rule over events (from, to]; extended alerts are saved but not returned.</summary>
    private async Task<List<ActivityAlert>> DetectAsync(Guid serverId, string channel, BurstRule rule, long from, long to, CancellationToken ct)
    {
        var scope = db.FileAuditEvents.AsNoTracking().Where(e => e.ServerId == serverId && e.Channel == channel && e.EventId != 0);
        var fresh = await Matching(scope, rule.Rule).Where(e => e.Id > from && e.Id <= to)
            .Select(e => new AlertEvent(e.Id, e.EventTime, e.PrincipalSid, e.PrincipalName)).ToListAsync(ct);
        if (fresh.Count == 0)
        {
            return [];
        }
        var since = fresh.Min(e => e.Time) - rule.Window;
        var people = fresh.Select(e => e.PrincipalSid).Distinct().ToList();
        var anyone = people.Contains(null);
        var earlier = await Matching(scope, rule.Rule).Where(e => e.Id <= from && e.EventTime >= since
                && (people.Contains(e.PrincipalSid) || (anyone && e.PrincipalSid == null)))
            .Select(e => new AlertEvent(e.Id, e.EventTime, e.PrincipalSid, e.PrincipalName)).ToListAsync(ct);

        return await RaiseAsync(serverId, rule, BurstDetector.Find(earlier, fresh, rule), ct);
    }

    /// <summary>Saves each burst as a new alert, or adds it to the open one it continues; returns only the new ones.</summary>
    private async Task<List<ActivityAlert>> RaiseAsync(Guid serverId, BurstRule rule, IReadOnlyList<Burst> bursts, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var created = new List<ActivityAlert>();
        foreach (var burst in bursts)
        {
            var reach = burst.First - rule.Window;
            var current = await db.ActivityAlerts
                .Where(a => a.Rule == rule.Rule && a.ServerId == serverId && a.PrincipalSid == burst.PrincipalSid && a.LastEventAt >= reach)
                .OrderByDescending(a => a.LastEventAt).FirstOrDefaultAsync(ct);
            if (current is not null)
            {
                current.EventCount += burst.NewEvents;
                // Late records can reach back before the alert began.
                current.FirstEventAt = burst.First < current.FirstEventAt ? burst.First : current.FirstEventAt;
                current.LastEventAt = burst.Last > current.LastEventAt ? burst.Last : current.LastEventAt;
                current.LastEventId = Math.Max(current.LastEventId ?? 0, burst.LastEventId);
                current.PrincipalName = burst.PrincipalName ?? current.PrincipalName;
                // More happened after someone looked at it: it needs looking at again.
                current.State = ActivityAlertState.Open;
                current.UpdatedAt = now;
                continue;
            }
            var alert = new ActivityAlert
            {
                Rule = rule.Rule,
                Severity = rule.Severity,
                State = ActivityAlertState.Open,
                ServerId = serverId,
                PrincipalSid = burst.PrincipalSid,
                PrincipalName = burst.PrincipalName,
                FirstEventAt = burst.First,
                LastEventAt = burst.Last,
                EventCount = burst.Count,
                FirstEventId = burst.FirstEventId,
                LastEventId = burst.LastEventId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ActivityAlerts.Add(alert);
            created.Add(alert);
        }
        await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<List<ActivityAlert>> RaiseForGapsAsync(Guid serverId, CancellationToken ct)
    {
        var gaps = await db.ActivityGaps.AsNoTracking()
            .Where(g => g.ServerId == serverId && !db.ActivityAlerts.Any(a => a.GapId == g.Id)).OrderBy(g => g.Id).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var created = gaps.Select(g => new ActivityAlert
        {
            Rule = ActivityAlertRule.EventsMissed,
            Severity = ActivityAlertText.Severity(ActivityAlertRule.EventsMissed),
            State = ActivityAlertState.Open,
            ServerId = serverId,
            FirstEventAt = g.LogOldestEventAt ?? g.DetectedAt,
            LastEventAt = g.LogOldestEventAt ?? g.DetectedAt,
            EventCount = g.LastRecordId is { } last ? (int)Math.Min(int.MaxValue, last - g.FirstRecordId + 1) : 0,
            GapId = g.Id,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        if (created.Count > 0)
        {
            db.ActivityAlerts.AddRange(created);
            await db.SaveChangesAsync(ct);
        }
        return created;
    }

    /// <summary>Queues one email per alert address, once per alert; unusual behaviour (D166) sends its alerts here too.</summary>
    internal static async Task EmailAsync(NotificationStore outbox, ActivityAlert alert, string hostname, ActivityAlertOptions options, string? publicUrl, CancellationToken ct)
    {
        var title = ActivityAlertText.Title(alert.Rule);
        var text = ActivityAlertText.Describe(alert.Rule, alert.PrincipalName, alert.EventCount, hostname, alert.FirstEventAt, alert.LastEventAt, alert.Usual, alert.Detail);
        var when = string.Create(CultureInfo.InvariantCulture, $"{alert.FirstEventAt.UtcDateTime:yyyy-MM-dd HH:mm} to {alert.LastEventAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        var link = string.IsNullOrWhiteSpace(publicUrl) ? "" : $"\n\nSee the events in Data Scope: {publicUrl.TrimEnd('/')}/#activity";
        const string from = "what Windows recorded in the Security log";
        var why = ActivityAlertText.IsUnusual(alert.Rule)
            ? " It compares the person with their own activity over the weeks before, so it is a lead to check, not proof of wrongdoing."
            : "";
        var body = $"{text}\n\nWhen: {when}{link}\n\nData Scope raised this from {from}. It shows what happened, not why.{why}";
        var subject = $"Data Scope alert: {title} on {hostname}";
        foreach (var address in options.EmailTo)
        {
            await outbox.EnqueueAsync(new OutgoingNotice(
                $"activity-alert:{alert.Id}:{address.Trim().ToUpperInvariant()}", NotificationKind.ActivityAlert, null, AlertRecipientSid,
                "Activity alerts", address, subject, body), ct);
        }
    }

    /// <summary>Marks an alert as looked at, or opens it again, with an optional note. Returns null when it doesn't exist.</summary>
    public async Task<ActivityAlert?> SetStateAsync(long id, ActivityAlertState state, string? note, string actorSid, string actorName, CancellationToken ct)
    {
        var alert = await db.ActivityAlerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return null;
        }
        var now = clock.GetUtcNow();
        alert.State = state;
        alert.Note = string.IsNullOrWhiteSpace(note) ? alert.Note : note.Trim();
        if (state == ActivityAlertState.Acknowledged)
        {
            alert.AcknowledgedBySid = actorSid;
            alert.AcknowledgedByName = actorName;
            alert.AcknowledgedAt = now;
        }
        alert.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return alert;
    }
}
