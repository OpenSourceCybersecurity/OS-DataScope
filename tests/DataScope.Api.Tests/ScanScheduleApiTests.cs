using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>Setting a server's scan schedule through the API (D116), on synthetic servers.</summary>
[Trait("Category", "Database")]
public sealed class ScanScheduleApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ScanScheduleApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-70-80-90-1110", @"LAB\admin");

    private sealed record ScheduleDto(string Frequency, int StartHour, string? Day, int? StopHour, DateTimeOffset? NextScanAt, DateTimeOffset? MissedAt, string TimeZone, int StartMinute, int? EveryHours,
        string FullScan, string? FullScanDay, DateTimeOffset? NextFullScanAt);

    private sealed record ServerDto(Guid Id, string Hostname, string State, ScheduleDto Schedule);

    private sealed record JobDto(Guid Id, string State, string? LastError, bool Scheduled, bool KeepToWindow, string Kind);

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<ServerDto> Enroll(string host) => await Ok<ServerDto>(await Admin.PostAsJsonAsync("/api/v1/servers", new { hostname = host + ".lab.test" }));

    private static Task<HttpResponseMessage> Put(HttpClient client, Guid id, object body) => client.PutAsJsonAsync($"/api/v1/servers/{id}/schedule", body);

    [Fact]
    public async Task A_new_server_is_scanned_every_night_and_the_schedule_can_be_changed()
    {
        var server = await Enroll("fs-schedule-api");
        Assert.Equal(("Daily", 1, (string?)null, (int?)7), (server.Schedule.Frequency, server.Schedule.StartHour, server.Schedule.Day, server.Schedule.StopHour));
        Assert.NotNull(server.Schedule.NextScanAt);
        Assert.True(server.Schedule.NextScanAt > DateTimeOffset.UtcNow);
        Assert.Null(server.Schedule.MissedAt);
        Assert.Equal(TimeZoneInfo.Local.DisplayName, server.Schedule.TimeZone);

        var weekly = await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "weekly", startHour = 22, day = "saturday", stopHour = 6 }));
        Assert.Equal(("Weekly", 22, "Saturday", (int?)6), (weekly.Schedule.Frequency, weekly.Schedule.StartHour, weekly.Schedule.Day, weekly.Schedule.StopHour));
        Assert.Equal(DayOfWeek.Saturday, TimeZoneInfo.ConvertTime(weekly.Schedule.NextScanAt!.Value, TimeZoneInfo.Local).DayOfWeek);

        var off = await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Off" }));
        Assert.Equal("Off", off.Schedule.Frequency);
        Assert.Null(off.Schedule.NextScanAt);
        Assert.Equal("Off", (await Ok<ServerDto>(await Admin.GetAsync($"/api/v1/servers/{server.Id}"))).Schedule.Frequency);

        await using var db = _db.CreateContext();
        var audit = await db.AppAuditLog.Where(a => a.Action == "server.schedule" && a.Target == "fs-schedule-api.lab.test").OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.Contains("\"Weekly\"", audit[0].Details, StringComparison.Ordinal);
        Assert.Equal(@"LAB\admin", audit[0].ActorName);
    }

    [Fact]
    public async Task A_new_server_gets_a_full_scan_every_Saturday_and_it_can_be_changed()
    {
        var server = await Enroll("fs-schedule-full");
        Assert.Equal(("Weekly", "Saturday"), (server.Schedule.FullScan, server.Schedule.FullScanDay));
        var next = TimeZoneInfo.ConvertTime(server.Schedule.NextFullScanAt!.Value, TimeZoneInfo.Local);
        Assert.Equal((DayOfWeek.Saturday, 1, 0), (next.DayOfWeek, next.Hour, next.Minute));

        var monthly = (await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Daily", startHour = 2, stopHour = 6, fullScan = "monthly", fullScanDay = "sunday" }))).Schedule;
        Assert.Equal(("Monthly", "Sunday"), (monthly.FullScan, monthly.FullScanDay));
        next = TimeZoneInfo.ConvertTime(monthly.NextFullScanAt!.Value, TimeZoneInfo.Local);
        Assert.Equal((DayOfWeek.Sunday, 2, true), (next.DayOfWeek, next.Hour, next.Day <= 7));

        // Left out, full scans stay as they are.
        var kept = (await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Daily", startHour = 3 }))).Schedule;
        Assert.Equal(("Monthly", "Sunday"), (kept.FullScan, kept.FullScanDay));

        var off = (await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Daily", startHour = 3, fullScan = "Off", fullScanDay = "Sunday" }))).Schedule;
        Assert.Equal(("Off", (string?)null, (DateTimeOffset?)null), (off.FullScan, off.FullScanDay, off.NextFullScanAt));

        foreach (var body in new object[]
        {
            new { frequency = "Daily", startHour = 1, fullScan = "Weekly" },
            new { frequency = "Daily", startHour = 1, fullScan = "Fortnightly", fullScanDay = "Saturday" },
            new { frequency = "Daily", startHour = 1, fullScan = "1", fullScanDay = "Saturday" },
            new { frequency = "Daily", startHour = 1, fullScan = "Weekly", fullScanDay = "Funday" },
            new { frequency = "Daily", startHour = 1, fullScan = "Weekly", fullScanDay = "6" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(Admin, server.Id, body)).StatusCode);
        }
        Assert.Equal("Off", (await Ok<ServerDto>(await Admin.GetAsync($"/api/v1/servers/{server.Id}"))).Schedule.FullScan);

        await using var db = _db.CreateContext();
        var audit = await db.AppAuditLog.Where(a => a.Action == "server.schedule" && a.Target == "fs-schedule-full.lab.test").OrderBy(a => a.Sequence).ToListAsync();
        Assert.Contains("\"FullScan\":\"Monthly\"", audit[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quick_or_full_scan_can_be_started_by_hand()
    {
        var server = await Enroll("fs-scan-kinds");
        var quick = await Ok<JobDto>(await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id }));
        Assert.Equal("Quick", quick.Kind);
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsync($"/api/v1/scan-jobs/{quick.Id}/cancel", null)).StatusCode);

        foreach (var kind in new[] { "Deep", "2", "" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id, kind })).StatusCode);
        }

        var full = await Ok<JobDto>(await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id, kind = "full" }));
        Assert.Equal(("Full", false), (full.Kind, full.Scheduled));
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id, kind = "Full" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Viewer).PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id, kind = "Full" })).StatusCode);

        await using var db = _db.CreateContext();
        var started = await db.AppAuditLog.Where(a => a.Action == "scan.start" && a.Target == "fs-scan-kinds.lab.test").OrderBy(a => a.Sequence).Select(a => a.Details).ToListAsync();
        Assert.Contains("\"Kind\":\"Quick\"", started[0], StringComparison.Ordinal);
        Assert.Contains("\"Kind\":\"Full\"", started[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_can_be_scanned_every_few_hours_at_a_chosen_minute()
    {
        var server = await Enroll("fs-schedule-hourly");
        Assert.Equal((0, (int?)null), (server.Schedule.StartMinute, server.Schedule.EveryHours));

        var hourly = (await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "hourly", startHour = 0, startMinute = 15, everyHours = 4 }))).Schedule;
        Assert.Equal(("Hourly", 0, 15, (int?)4, (int?)null), (hourly.Frequency, hourly.StartHour, hourly.StartMinute, hourly.EveryHours, hourly.StopHour));
        var next = TimeZoneInfo.ConvertTime(hourly.NextScanAt!.Value, TimeZoneInfo.Local);
        Assert.Equal((15, 0), (next.Minute, next.Hour % 4));
        Assert.True(hourly.NextScanAt - DateTimeOffset.UtcNow <= TimeSpan.FromHours(4));

        // Back to once a day at 2:30: the every-few-hours setting is dropped.
        var daily = (await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Daily", startHour = 2, startMinute = 30, everyHours = 4 }))).Schedule;
        Assert.Equal(("Daily", 2, 30, (int?)null), (daily.Frequency, daily.StartHour, daily.StartMinute, daily.EveryHours));
        await using var db = _db.CreateContext();
        Assert.Null(await db.ManagedServers.Where(s => s.Id == server.Id).Select(s => s.ScanEveryHours).SingleAsync());
    }

    [Fact]
    public async Task Schedules_that_cannot_work_are_refused()
    {
        var server = await Enroll("fs-schedule-bad");
        foreach (var body in new object[]
        {
            new { frequency = "Weekly", startHour = 1 },
            new { frequency = "Weekly", startHour = 1, day = "Someday" },
            new { frequency = "Weekly", startHour = 1, day = "3" },
            new { frequency = "Daily", startHour = 24 },
            new { frequency = "Daily", startHour = 1, stopHour = 1 },
            new { frequency = "Daily", startHour = 1, stopHour = -1 },
            new { frequency = "Hourly", startHour = 1 },
            new { frequency = "Hourly", startHour = 1, everyHours = 5 },
            new { frequency = "Hourly", startHour = 1, everyHours = 0 },
            new { frequency = "Hourly", startHour = 1, everyHours = 24 },
            new { frequency = "Daily", startHour = 1, startMinute = 60 },
            new { frequency = "Daily", startHour = 1, startMinute = -5 },
            new { frequency = "1", startHour = 1 },
            new { startHour = 1 },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(Admin, server.Id, body)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Put(Admin, Guid.NewGuid(), new { frequency = "Daily", startHour = 1 })).StatusCode);
        Assert.Equal("Daily", (await Ok<ServerDto>(await Admin.GetAsync($"/api/v1/servers/{server.Id}"))).Schedule.Frequency);

        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync($"/api/v1/servers/{server.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(Admin, server.Id, new { frequency = "Daily", startHour = 2 })).StatusCode);
    }

    [Theory]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.Viewer)]
    public async Task Only_administrators_change_a_schedule(string role)
    {
        var server = await Enroll($"fs-schedule-{role.ToLowerInvariant()}");
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(_factory.ClientAs(role), server.Id, new { frequency = "Off" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(_factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false), server.Id, new { frequency = "Off" })).StatusCode);
        Assert.Equal("Daily", (await Ok<ServerDto>(await _factory.ClientAs(role).GetAsync($"/api/v1/servers/{server.Id}"))).Schedule.Frequency);
    }

    [Fact]
    public async Task A_scheduled_time_the_worker_never_reached_shows_as_missed()
    {
        var server = await Enroll("fs-schedule-missed");
        // A start hour about twelve hours ago, so the last scheduled time is well past catch-up.
        var hour = (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).Hour + 12) % 24;
        await Ok<ServerDto>(await Put(Admin, server.Id, new { frequency = "Daily", startHour = hour }));
        await using (var db = _db.CreateContext())
        {
            await db.ManagedServers.Where(s => s.Id == server.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastScheduledAt, DateTimeOffset.UtcNow.AddDays(-3)));
        }
        var missed = (await Ok<ServerDto>(await Admin.GetAsync($"/api/v1/servers/{server.Id}"))).Schedule;
        Assert.NotNull(missed.MissedAt);
        Assert.True(DateTimeOffset.UtcNow - missed.MissedAt > ScanSchedule.CatchUp);
        Assert.True(missed.NextScanAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Pausing_or_resuming_a_scheduled_scan_by_hand_takes_it_off_the_schedule()
    {
        var server = await Enroll("fs-schedule-hand");
        Guid id;
        await using (var db = _db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            var job = new ScanJob
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                State = ScanJobState.Paused,
                PolicyVersion = ClassificationPolicy.CurrentVersion,
                RequestedBySid = ScanSchedule.Requester,
                Scheduled = true,
                KeepToWindow = true,
                LastError = ScanJobStore.WindowNote,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ScanJobs.Add(job);
            await db.SaveChangesAsync();
            id = job.Id;
        }
        var listed = await Ok<JobDto>(await Admin.GetAsync($"/api/v1/scan-jobs/{id}"));
        Assert.Equal((true, true), (listed.Scheduled, listed.KeepToWindow));

        var resumed = await Ok<JobDto>(await Admin.PostAsync($"/api/v1/scan-jobs/{id}/resume", null));
        Assert.Equal(("Queued", (string?)null, true, false), (resumed.State, resumed.LastError, resumed.Scheduled, resumed.KeepToWindow));
    }
}
