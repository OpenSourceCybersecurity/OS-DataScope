using System.Net;
using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Tests.Activity;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>The file activity and alert reports over synthetic events on one collecting server and one that is off.</summary>
[Trait("Category", "Database")]
public sealed class ActivityReportTests : IClassFixture<ActivityReportTests.Lab>
{
    private const string Root = @"D:\Shares\Projects";
    private readonly Lab _lab;

    public ActivityReportTests(Lab lab) => _lab = lab;

    public sealed class Lab : IAsyncLifetime
    {
        public TestDatabase Database { get; } = new();
        public ApiFactory Factory { get; private set; } = null!;
        public Guid Server { get; private set; }
        public Guid Share { get; private set; }

        public async Task InitializeAsync()
        {
            await Database.InitializeAsync();
            Factory = new ApiFactory(Database);
            await using var db = Database.CreateContext();
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = "fs-report", CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            var off = new ManagedServer { Id = Guid.NewGuid(), Hostname = "fs-report-off", CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            var share = new Share
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                Name = "Projects",
                NameKey = "PROJECTS",
                UncPath = @"\\fs-report\Projects",
                Kind = ShareKind.Disk,
                Included = true,
                CreatedAt = now,
                UpdatedAt = now,
                LocalPath = Root,
                LocalPathSource = ShareLocalPathSource.Admin,
            };
            db.AddRange(server, off, share);
            await db.SaveChangesAsync();
            var store = new ActivityStore(db, TimeProvider.System);
            await store.SetEnabledAsync(server.Id, true, "S-1", default);
            var t = now.AddHours(-2);
            string[] events =
            [
                EventXml.ObjectAccess(1, $@"{Root}\Plans\q4.docx", 0x1, time: t),
                EventXml.ObjectAccess(2, $@"{Root}\Plans\q4.docx", 0x2, time: t.AddMinutes(1)),
                EventXml.ShareCheck(3, "Projects", Root, @"Plans\q4.docx", 0x1),
                EventXml.ShareConnect(4, "Projects", Root),
                EventXml.ObjectAccess(5, $@"{Root}\Old\notes.txt", 0x10000, handle: 0x77, time: t.AddMinutes(2)),
                EventXml.Deleted(6, handle: 0x77, time: t.AddMinutes(2)),
                EventXml.ObjectAccess(7, @"C:\Elsewhere\x.txt", 0x1, time: t.AddMinutes(3)),
                EventXml.ObjectAccess(8, $@"{Root}\Archive\2019.xlsx", 0x1, time: now.AddDays(-40)),
            ];
            await store.IngestAsync(server.Id, ActivityStore.SecurityChannel, events.Select(x => new RawSecurityEvent(SecurityEventXml.Parse(x).RecordId, x)).ToList(), default);

            db.ActivityGaps.Add(new ActivityGap
            {
                ServerId = server.Id,
                Channel = ActivityStore.SecurityChannel,
                Reason = ActivityGapReason.Overwritten,
                FirstRecordId = 100,
                LastRecordId = 349,
                DetectedAt = now.AddDays(-1),
            });
            db.ActivityAlerts.AddRange(
                new ActivityAlert
                {
                    Rule = ActivityAlertRule.MassDelete,
                    Severity = ActivityAlertSeverity.High,
                    State = ActivityAlertState.Acknowledged,
                    ServerId = server.Id,
                    PrincipalSid = EventXml.Alice,
                    PrincipalName = @"NORTHWIND\alice",
                    FirstEventAt = t,
                    LastEventAt = t.AddMinutes(4),
                    EventCount = 75,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AcknowledgedBySid = "S-1",
                    AcknowledgedByName = @"NORTHWIND\admin",
                    AcknowledgedAt = now,
                    Note = "Planned clean-up of Old",
                },
                new ActivityAlert
                {
                    Rule = ActivityAlertRule.PermissionChange,
                    Severity = ActivityAlertSeverity.Medium,
                    State = ActivityAlertState.Open,
                    ServerId = server.Id,
                    PrincipalSid = EventXml.Bob,
                    PrincipalName = @"NORTHWIND\bob",
                    FirstEventAt = now.AddDays(-45),
                    LastEventAt = now.AddDays(-45),
                    EventCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
            Server = server.Id;
            Share = share.Id;
        }

        public async Task DisposeAsync()
        {
            Factory.Dispose();
            await Database.DisposeAsync();
        }
    }

    private HttpClient Admin => _lab.Factory.ClientAs(Roles.PlatformAdmin);

    private static async Task<string> TextAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return body;
    }

    [Fact]
    public async Task The_activity_report_states_the_period_collection_and_what_was_missed()
    {
        var csv = await TextAsync(await Admin.GetAsync("/api/v1/reports/file-activity?format=csv"));
        foreach (var fact in new[] { "Scope,All enrolled file servers", "Period,", "(the last 30 days)", "1 of 2 file servers collecting", "nothing is known about them: fs-report-off",
                     "Events missed,1 times Data Scope found records gone before it read them (250 log records", "Security log cleared,Not in this period", "Limitation," })
        {
            Assert.Contains(fact, csv);
        }
        Assert.DoesNotContain("Latest scan finished", csv);
    }

    [Fact]
    public async Task The_activity_report_lists_recorded_events_never_access_checks_as_reads()
    {
        var csv = await TextAsync(await Admin.GetAsync("/api/v1/reports/file-activity?format=csv&days=3660"));
        Assert.Contains(@",fs-report,Projects,Plans\q4.docx,", csv);
        Assert.Contains(",Recorded,Allowed,", csv);
        Assert.Contains(",Deleted,Recorded,Allowed,", csv);
        Assert.Contains("From record 5 on the same handle", csv);
        Assert.Contains(@"C:\Elsewhere\x.txt", csv);
        Assert.DoesNotContain(",Share access check,", csv);
        Assert.DoesNotContain(",Connected to share,", csv);
        Assert.Contains("(the last 3660 days)", csv);
        Assert.Contains(@"Archive\2019.xlsx", csv);

        // The default period leaves out the event from 40 days ago.
        csv = await TextAsync(await Admin.GetAsync("/api/v1/reports/file-activity?format=csv"));
        Assert.DoesNotContain(@"Archive\2019.xlsx", csv);
        Assert.Contains(@"Plans\q4.docx", csv);
    }

    [Fact]
    public async Task A_share_scope_leaves_out_events_in_no_share()
    {
        var csv = await TextAsync(await Admin.GetAsync($"/api/v1/reports/file-activity?format=csv&shareId={_lab.Share}"));
        Assert.Contains(@"Scope,Share \\fs-report\Projects", csv);
        Assert.Contains("1 of 1 file servers collecting", csv);
        Assert.Contains(@"Plans\q4.docx", csv);
        Assert.DoesNotContain(@"C:\Elsewhere\x.txt", csv);
    }

    [Fact]
    public async Task The_alert_report_lists_alerts_in_the_period_with_who_acknowledged_them()
    {
        var csv = await TextAsync(await Admin.GetAsync("/api/v1/reports/activity-alerts?format=csv"));
        Assert.Contains("Many files deleted,High,Acknowledged,fs-report,", csv);
        Assert.Contains(@"NORTHWIND\alice deleted 75 files or folders on fs-report", csv);
        Assert.Contains("Planned clean-up of Old", csv);
        Assert.Contains("Alerts listed,\"1 alerts, 0 not acknowledged\"", csv);
        Assert.DoesNotContain(@"NORTHWIND\bob", csv);

        csv = await TextAsync(await Admin.GetAsync($"/api/v1/reports/activity-alerts?format=csv&days=60&shareId={_lab.Share}"));
        Assert.Contains(@"NORTHWIND\bob", csv);
        Assert.Contains("every alert on the share's server", csv);

        var html = await TextAsync(await Admin.GetAsync("/api/v1/reports/activity-alerts?format=html"));
        Assert.Contains("Read this first", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Activity_reports_need_an_evidence_role_and_a_sensible_period()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/reports/file-activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.DataOwner).GetAsync("/api/v1/reports/activity-alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _lab.Factory.ClientAs(Roles.Auditor).GetAsync("/api/v1/reports/file-activity")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _lab.Factory.ClientAs(Roles.SecurityAnalyst).GetAsync("/api/v1/reports/activity-alerts")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/reports/file-activity?days=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/reports/file-activity?days=3661")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/reports/scan-coverage?days=30")).StatusCode);
    }

    [Fact]
    public async Task An_activity_export_is_audited_with_its_period()
    {
        await TextAsync(await Admin.GetAsync($"/api/v1/reports/file-activity?format=csv&days=7&serverId={_lab.Server}"));
        await using var db = _lab.Database.CreateContext();
        var entry = await db.AppAuditLog.Where(a => a.Action == "report.export" && a.Target == "File activity").OrderByDescending(a => a.Sequence).FirstAsync();
        Assert.Contains("\"Days\":7", entry.Details);
        Assert.Contains("File server fs-report", entry.Details);
    }
}
