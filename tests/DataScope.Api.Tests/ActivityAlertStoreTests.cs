using DataScope.Core.Activity;
using DataScope.Core.Governance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Tests.Activity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Api.Tests;

/// <summary>Alert rules over stored events, alert emails, and purging old activity (D105, D106).</summary>
[Trait("Category", "Database")]
public sealed class ActivityAlertStoreTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly ActivityAlertOptions Options = new() { MassDeleteCount = 50, MassDeleteMinutes = 10, DeniedCount = 20, DeniedMinutes = 10, EmailTo = ["soc@northwind.test"] };

    private async Task<Guid> Server(string host)
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        db.Add(server);
        await db.SaveChangesAsync();
        await new ActivityStore(db, TimeProvider.System).SetEnabledAsync(server.Id, true, "S-1", default);
        return server.Id;
    }

    private async Task Ingest(Guid server, IEnumerable<(long Id, string Xml)> events)
    {
        await using var db = database.CreateContext();
        await new ActivityStore(db, TimeProvider.System).IngestAsync(server, ActivityStore.SecurityChannel,
            events.Select(e => new RawSecurityEvent(e.Id, e.Xml)).ToList(), default);
    }

    private static IEnumerable<(long, string)> Deletes(long from, int count, string sid = EventXml.Alice, string user = "alice") =>
        Enumerable.Range(0, count).Select(i => (from + i, EventXml.Deleted(from + i, sid: sid, user: user, handle: 0x1000 + from + i)));

    private async Task<IReadOnlyList<ActivityAlert>> Detect(Guid server, ActivityAlertOptions? options = null)
    {
        await using var db = database.CreateContext();
        return await new ActivityAlertStore(db, new NotificationStore(db, TimeProvider.System), TimeProvider.System)
            .DetectAsync(server, ActivityStore.SecurityChannel, options ?? Options, "https://ds01.northwind.test/", default);
    }

    private async Task<List<ActivityAlert>> Alerts(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ActivityAlerts.AsNoTracking().Where(a => a.ServerId == server).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task Many_deletes_by_one_person_raise_one_high_alert_and_an_email()
    {
        var server = await Server("fs-alert-1");
        await Ingest(server, Deletes(1, 60));

        var raised = Assert.Single(await Detect(server));
        Assert.Equal((ActivityAlertRule.MassDelete, ActivityAlertSeverity.High, ActivityAlertState.Open, 60, EventXml.Alice),
            (raised.Rule, raised.Severity, raised.State, raised.EventCount, raised.PrincipalSid));
        Assert.Equal((EventXml.T0.AddSeconds(1), EventXml.T0.AddSeconds(60)), (raised.FirstEventAt, raised.LastEventAt));

        await using var db = database.CreateContext();
        var mail = await db.Outbox.AsNoTracking().SingleAsync(m => m.IdempotencyKey == $"activity-alert:{raised.Id}:SOC@NORTHWIND.TEST");
        Assert.Equal((NotificationKind.ActivityAlert.ToString(), "soc@northwind.test"), (mail.Kind.ToString(), mail.RecipientEmail));
        Assert.Equal("Data Scope alert: Many files deleted on fs-alert-1", mail.Subject);
        Assert.Contains("alice deleted 60 files or folders on fs-alert-1", mail.Body, StringComparison.Ordinal);
        Assert.Contains("https://ds01.northwind.test/#activity", mail.Body, StringComparison.Ordinal);

        // Nothing new: nothing raised, nothing sent again.
        Assert.Empty(await Detect(server));
        Assert.Single(await Alerts(server));
    }

    [Fact]
    public async Task A_burst_split_across_passes_is_one_alert_and_more_deletes_extend_and_reopen_it()
    {
        var server = await Server("fs-alert-2");
        await Ingest(server, Deletes(1, 30));
        Assert.Empty(await Detect(server));
        await Ingest(server, Deletes(31, 30));
        var alert = Assert.Single(await Detect(server));
        Assert.Equal(60, alert.EventCount);

        await using (var db = database.CreateContext())
        {
            var store = new ActivityAlertStore(db, new NotificationStore(db, TimeProvider.System), TimeProvider.System);
            var acked = await store.SetStateAsync(alert.Id, ActivityAlertState.Acknowledged, "Planned clean-up of P-109", "S-1-5-21-1-2-3-500", "NORTHWIND\\admin", default);
            Assert.Equal(("NORTHWIND\\admin", "Planned clean-up of P-109"), (acked!.AcknowledgedByName, acked.Note));
        }
        await Ingest(server, Deletes(61, 5));
        Assert.Empty(await Detect(server));
        var extended = Assert.Single(await Alerts(server));
        Assert.Equal((65, ActivityAlertState.Open, 65L), (extended.EventCount, extended.State, extended.LastEventId - extended.FirstEventId + 1));
    }

    [Fact]
    public async Task Below_the_threshold_or_spread_across_people_raises_nothing()
    {
        var server = await Server("fs-alert-3");
        await Ingest(server, Deletes(1, 49));
        await Ingest(server, Deletes(100, 49, sid: EventXml.Bob, user: "bob"));
        Assert.Empty(await Detect(server));

        // Fifty deletes, but the fiftieth comes long after the window: still nothing.
        await Ingest(server, [(5000, EventXml.Deleted(5000, handle: 0x9999, time: EventXml.T0.AddHours(3)))]);
        Assert.Empty(await Detect(server));
    }

    [Fact]
    public async Task Permission_changes_log_clears_refusals_and_missed_events_each_raise_their_alert()
    {
        var server = await Server("fs-alert-4");
        await Ingest(server, [
            (1, EventXml.PermissionsChanged(1, @"D:\Shares\HR\Payroll", "D:(A;;FA;;;BA)", "D:(A;;FA;;;BA)(A;;FA;;;WD)")),
            (2, EventXml.PermissionsChanged(2, @"D:\Shares\HR\Payroll\2026", "D:(A;;FA;;;BA)", "D:(A;;FA;;;WD)")),
            (3, EventXml.LogCleared(3, sid: EventXml.Bob)),
            .. Enumerable.Range(10, 25).Select(i => ((long)i, EventXml.HandleRequest(i, $@"D:\Shares\HR\Payroll\f{i}.xlsx", 0x1, denied: true, sid: EventXml.Bob, user: "bob"))),
        ]);
        await using (var db = database.CreateContext())
        {
            db.ActivityGaps.Add(new ActivityGap { ServerId = server, Channel = ActivityStore.SecurityChannel, Reason = ActivityGapReason.Overwritten, FirstRecordId = 40, LastRecordId = 89, DetectedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var raised = await Detect(server);
        var byRule = raised.ToDictionary(a => a.Rule);
        Assert.Equal(2, byRule[ActivityAlertRule.PermissionChange].EventCount);
        Assert.Equal((ActivityAlertSeverity.High, EventXml.Bob), (byRule[ActivityAlertRule.LogCleared].Severity, byRule[ActivityAlertRule.LogCleared].PrincipalSid));
        Assert.Equal(25, byRule[ActivityAlertRule.ManyDenied].EventCount);
        Assert.Equal((50, ActivityAlertSeverity.Medium), (byRule[ActivityAlertRule.EventsMissed].EventCount, byRule[ActivityAlertRule.EventsMissed].Severity));
        Assert.NotNull(byRule[ActivityAlertRule.EventsMissed].GapId);

        // Only High alerts are emailed unless asked for.
        await using var check = database.CreateContext();
        var keys = await check.Outbox.Where(m => m.IdempotencyKey.StartsWith("activity-alert:")).Select(m => m.IdempotencyKey).ToListAsync();
        Assert.Contains($"activity-alert:{byRule[ActivityAlertRule.LogCleared].Id}:SOC@NORTHWIND.TEST", keys);
        Assert.DoesNotContain($"activity-alert:{byRule[ActivityAlertRule.PermissionChange].Id}:SOC@NORTHWIND.TEST", keys);
        Assert.Empty(await Detect(server));
    }

    [Fact]
    public async Task Turning_alerts_off_raises_nothing()
    {
        var server = await Server("fs-alert-5");
        await Ingest(server, Deletes(1, 60));
        Assert.Empty(await Detect(server, new ActivityAlertOptions { Enabled = false }));
    }

    [Fact]
    public async Task Old_activity_is_purged_only_through_the_owner_function_and_never_under_30_days()
    {
        var server = await Server("fs-alert-6");
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        await Ingest(server, [
            (1, EventXml.ObjectAccess(1, @"D:\Shares\HR\a.txt", 0x1, time: old)),
            (2, EventXml.ObjectAccess(2, @"D:\Shares\HR\b.txt", 0x1, time: old.AddMinutes(1))),
            (3, EventXml.ObjectAccess(3, @"D:\Shares\HR\c.txt", 0x1, time: DateTimeOffset.UtcNow.AddDays(-2))),
        ]);
        await using (var db = database.CreateContext())
        {
            db.ActivityGaps.Add(new ActivityGap { ServerId = server, Channel = ActivityStore.SecurityChannel, Reason = ActivityGapReason.LogReset, FirstRecordId = 1, DetectedAt = old });
            await db.SaveChangesAsync();
        }
        await Detect(server); // the gap's alert must survive the gap being purged

        await using (var runtime = database.CreateContext())
        {
            var tooSoon = await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.Database.SqlQuery<long>($"SELECT events AS value FROM datascope_purge_activity({DateTimeOffset.UtcNow.AddDays(-10)}, 100)").ToListAsync());
            Assert.Contains("at least 30 days", tooSoon.MessageText, StringComparison.Ordinal);

            var result = await new RetentionStore(runtime, new AuditLogWriter(runtime, TimeProvider.System), TimeProvider.System)
                .PurgeAsync(new RetentionOptions(), default);
            Assert.True(result.ActivityEvents >= 2);
            Assert.True(result.ActivityGaps >= 1);
        }
        await using var check = database.CreateContext();
        Assert.Equal([3L], await check.FileAuditEvents.Where(e => e.ServerId == server).Select(e => e.RecordId).ToListAsync());
        var alert = await check.ActivityAlerts.SingleAsync(a => a.ServerId == server && a.Rule == ActivityAlertRule.EventsMissed);
        Assert.Null(alert.GapId);
        Assert.Contains("\"ActivityEvents\":", (await check.AppAuditLog.Where(a => a.Action == "retention.purge").OrderBy(a => a.Sequence).LastAsync()).Details, StringComparison.Ordinal);
    }
}
