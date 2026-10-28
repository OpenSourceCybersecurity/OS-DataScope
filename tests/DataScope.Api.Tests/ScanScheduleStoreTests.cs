using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>Scheduled scans (D116): what the worker starts, continues or leaves alone, on synthetic servers.</summary>
[Trait("Category", "Database")]
public sealed class ScanScheduleStoreTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    /// <summary>Five hours behind UTC all year: 01:00 local is 06:00 UTC.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test -5", "Test -5");

    private static readonly DateTimeOffset Due = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);

    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<Guid> Server(string host, ScanScheduleSettings schedule, DateTimeOffset? lastScheduled = null, EnrollmentState state = EnrollmentState.Enrolled,
        FullScanSettings? full = null)
    {
        await using var db = database.CreateContext();
        var created = Due.AddDays(-30);
        var server = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Hostname = host,
            State = state,
            CreatedAt = created,
            UpdatedAt = created,
            CreatedBySid = "S-1",
            Schedule = schedule,
            LastScheduledAt = lastScheduled ?? created,
            FullScan = full ?? FullScanSettings.Off,
            LastFullScheduledAt = lastScheduled ?? created,
        };
        db.ManagedServers.Add(server);
        await db.SaveChangesAsync();
        return server.Id;
    }

    private async Task<Guid> Job(Guid server, ScanJobState state, DateTimeOffset created, bool keepToWindow = false, string? error = null, ScanKind kind = ScanKind.Quick)
    {
        await using var db = database.CreateContext();
        var job = new ScanJob
        {
            Id = Guid.NewGuid(),
            ServerId = server,
            State = state,
            Kind = kind,
            PolicyVersion = ClassificationPolicy.CurrentVersion,
            RequestedBySid = "S-1",
            Scheduled = keepToWindow,
            KeepToWindow = keepToWindow,
            LastError = error,
            CreatedAt = created,
            UpdatedAt = created,
        };
        db.ScanJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>The worker takes the oldest job from any server, so leave nothing else for it in this class's database.</summary>
    private async Task OnlyThisServer(Guid server)
    {
        await using var db = database.CreateContext();
        await db.ManagedServers.Where(s => s.Id != server).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ScanFrequency, ScanFrequency.Off)
            .SetProperty(x => x.FullScanFrequency, FullScanFrequency.Off)
            .SetProperty(x => x.FullScanDay, (DayOfWeek?)null));
        await db.ScanJobs.Where(j => j.ServerId != server && (j.State == ScanJobState.Queued || j.State == ScanJobState.Running || j.State == ScanJobState.Paused))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ScanJobState.Cancelled));
    }

    private async Task<int> Ensure(DateTimeOffset now)
    {
        await using var db = database.CreateContext();
        return await new ScanJobStore(db, new SetClock(now)).EnsureScheduledAsync(Zone);
    }

    private async Task<List<ScanJob>> Jobs(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ScanJobs.AsNoTracking().Where(j => j.ServerId == server).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    private async Task<DateTimeOffset?> LastFullScheduled(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ManagedServers.Where(s => s.Id == server).Select(s => s.LastFullScheduledAt).SingleAsync();
    }

    private async Task<DateTimeOffset?> LastScheduled(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ManagedServers.Where(s => s.Id == server).Select(s => s.LastScheduledAt).SingleAsync();
    }

    [Fact]
    public async Task A_due_scan_starts_once_as_the_scheduler()
    {
        var server = await Server("fs-sched-due", ScanScheduleSettings.NewServer);
        Assert.Equal(0, await Jobs(server).ContinueWith(t => t.Result.Count));

        await Ensure(Due.AddMinutes(-1));
        Assert.Empty(await Jobs(server)); // not yet

        await Ensure(Due.AddMinutes(2));
        await Ensure(Due.AddMinutes(3));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((ScanJobState.Queued, ScanSchedule.Requester, true, true), (job.State, job.RequestedBySid, job.Scheduled, job.KeepToWindow));
        Assert.Equal(Due, await LastScheduled(server));
    }

    [Fact]
    public async Task A_scan_missed_by_more_than_six_hours_waits_for_the_next_time()
    {
        var server = await Server("fs-sched-missed", ScanScheduleSettings.NewServer);
        await Ensure(Due + ScanSchedule.CatchUp + TimeSpan.FromMinutes(1));
        Assert.Empty(await Jobs(server));

        // Within six hours it still starts, for example after the Data Scope server restarts at 6 AM local.
        var late = await Server("fs-sched-late", ScanScheduleSettings.NewServer);
        await Ensure(Due + TimeSpan.FromHours(5));
        Assert.Single(await Jobs(late));
    }

    [Fact]
    public async Task A_scan_already_under_way_or_run_since_is_never_doubled()
    {
        var running = await Server("fs-sched-running", ScanScheduleSettings.NewServer);
        await Job(running, ScanJobState.Running, Due.AddDays(-1));
        var pausedByHand = await Server("fs-sched-paused", ScanScheduleSettings.NewServer);
        await Job(pausedByHand, ScanJobState.Paused, Due.AddDays(-1));
        var manual = await Server("fs-sched-manual", ScanScheduleSettings.NewServer);
        await Job(manual, ScanJobState.Completed, Due.AddMinutes(1));

        await Ensure(Due.AddMinutes(5));
        Assert.Single(await Jobs(running));
        var paused = Assert.Single(await Jobs(pausedByHand));
        Assert.Equal(ScanJobState.Paused, paused.State); // someone paused it by hand: the schedule leaves it alone
        Assert.Single(await Jobs(manual));
        foreach (var server in new[] { running, pausedByHand, manual })
        {
            Assert.Equal(Due, await LastScheduled(server));
        }
    }

    [Fact]
    public async Task A_scan_paused_at_its_stop_hour_continues_at_the_next_scheduled_time()
    {
        var server = await Server("fs-sched-window", ScanScheduleSettings.NewServer);
        var id = await Job(server, ScanJobState.Paused, Due.AddDays(-1), keepToWindow: true, error: ScanJobStore.WindowNote);

        await Ensure(Due.AddMinutes(1));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((id, ScanJobState.Queued, (string?)null), (job.Id, job.State, job.LastError));
    }

    [Fact]
    public async Task A_scan_every_few_hours_starts_at_each_time_but_never_doubles_one_still_running()
    {
        // Every 2 hours at 1:30 local and so on: 06:30 UTC, 08:30 UTC...
        var server = await Server("fs-sched-hourly", new ScanScheduleSettings(ScanFrequency.Hourly, 1, null, null, StartMinute: 30, EveryHours: 2), lastScheduled: Due);
        await Ensure(Due.AddMinutes(29));
        Assert.Empty(await Jobs(server));

        await Ensure(Due.AddMinutes(31));
        var first = Assert.Single(await Jobs(server));
        Assert.Equal(Due.AddMinutes(30), await LastScheduled(server));

        // Still running two hours later: the 3:30 start is passed over, not queued behind it.
        await using (var db = database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ScanJobState.Running));
        }
        await Ensure(Due.AddMinutes(151));
        Assert.Single(await Jobs(server));
        Assert.Equal(Due.AddMinutes(150), await LastScheduled(server));

        // Finished by 5:30: that one starts.
        await using (var db = database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ScanJobState.Completed));
        }
        await Ensure(Due.AddMinutes(271));
        Assert.Equal(2, (await Jobs(server)).Count);
        Assert.Equal(Due.AddMinutes(270), await LastScheduled(server));
    }

    [Fact]
    public async Task Off_and_removed_servers_are_not_scanned()
    {
        var off = await Server("fs-sched-off", ScanScheduleSettings.Off);
        var removed = await Server("fs-sched-removed", ScanScheduleSettings.NewServer, state: EnrollmentState.Retired);
        var saved = await Server("fs-sched-saved", ScanScheduleSettings.NewServer, lastScheduled: Due.AddMinutes(-30));
        var savedLater = await Server("fs-sched-saved-late", ScanScheduleSettings.NewServer, lastScheduled: Due.AddMinutes(30));
        await Ensure(Due.AddHours(1));
        Assert.Empty(await Jobs(off));
        Assert.Empty(await Jobs(removed));
        Assert.Single(await Jobs(saved));
        Assert.Empty(await Jobs(savedLater)); // a schedule saved after 1 AM starts tomorrow, not straight away
    }

    [Fact]
    public async Task The_worker_pauses_a_scheduled_scan_at_its_stop_hour_and_the_next_night_finishes_it()
    {
        // 1 AM to 2 AM local.
        var server = await Server("fs-sched-worker", new ScanScheduleSettings(ScanFrequency.Daily, 1, null, 2));
        await OnlyThisServer(server);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = database.ConnectionString,
            ["Scanner:DryRunSteps"] = "3",
            ["Scanner:DryRunStepMilliseconds"] = "1",
            ["Scanner:LeaseSeconds"] = "30",
        }).Build();
        var clock = new SetClock(Due.AddMinutes(90));
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        services.AddSingleton<TimeProvider>(clock);
        await using var sp = services.BuildServiceProvider();
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), new DryRunScanExecutor(config), config, NullLogger<ScanWorker>.Instance, Zone);

        // 2:30 AM local, past the stop hour: the 1 AM scan is late but within catch-up, so it starts and pauses at once.
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((ScanJobState.Paused, ScanJobStore.WindowNote, true), (job.State, job.LastError, job.KeepToWindow));
        Assert.Equal(1, job.ItemsDone);

        // Nothing more happens during the day.
        Assert.False(await worker.RunOneAsync(CancellationToken.None));

        // The next night at 1 AM local it continues from where it stopped and finishes.
        clock.Now = Due.AddDays(1).AddMinutes(1);
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        job = Assert.Single(await Jobs(server));
        Assert.Equal((ScanJobState.Completed, 3L), (job.State, job.ItemsDone));
    }

    [Fact]
    public async Task A_scan_started_by_hand_ignores_the_stop_hour()
    {
        var server = await Server("fs-sched-manual-run", ScanScheduleSettings.NewServer, lastScheduled: Due.AddHours(7));
        await Job(server, ScanJobState.Queued, Due.AddHours(7));
        await OnlyThisServer(server);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = database.ConnectionString,
            ["Scanner:DryRunSteps"] = "3",
            ["Scanner:DryRunStepMilliseconds"] = "1",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        services.AddSingleton<TimeProvider>(new SetClock(Due.AddHours(7)));
        await using var sp = services.BuildServiceProvider();
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), new DryRunScanExecutor(config), config, NullLogger<ScanWorker>.Instance, Zone);

        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        Assert.Equal(ScanJobState.Completed, Assert.Single(await Jobs(server)).State);
    }

    // Full scans (D130). Due is Saturday 1 AM local, so a weekly Saturday full scan is due with the nightly quick scan.

    [Fact]
    public async Task A_due_full_scan_takes_the_place_of_the_quick_scan_due_at_the_same_time()
    {
        var server = await Server("fs-full-due", ScanScheduleSettings.NewServer, full: FullScanSettings.NewServer);
        await Ensure(Due.AddMinutes(-1));
        Assert.Empty(await Jobs(server));

        Assert.Equal(1, await Ensure(Due.AddMinutes(2)));
        Assert.Equal(0, await Ensure(Due.AddMinutes(3)));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((ScanKind.Full, ScanJobState.Queued, true), (job.Kind, job.State, job.Scheduled));
        // It keeps to the nightly window: paused at 7 AM, it continues at the next night's scan time.
        Assert.True(job.KeepToWindow);
        Assert.Equal(Due, await LastScheduled(server));
        Assert.Equal(Due, await LastFullScheduled(server));
    }

    [Fact]
    public async Task On_other_nights_a_quick_scan_starts()
    {
        var server = await Server("fs-full-other-night", ScanScheduleSettings.NewServer, full: FullScanSettings.NewServer);
        await Ensure(Due.AddDays(1).AddMinutes(2));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal(ScanKind.Quick, job.Kind);
    }

    [Fact]
    public async Task A_full_scan_paused_for_the_day_continues_at_the_next_nights_scan_time()
    {
        var server = await Server("fs-full-continues", ScanScheduleSettings.NewServer, lastScheduled: Due, full: FullScanSettings.NewServer);
        var full = await Job(server, ScanJobState.Paused, Due, keepToWindow: true, error: ScanJobStore.WindowNote, kind: ScanKind.Full);

        Assert.Equal(1, await Ensure(Due.AddDays(1).AddMinutes(2)));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((full, ScanKind.Full, ScanJobState.Queued), (job.Id, job.Kind, job.State));
    }

    [Fact]
    public async Task A_quick_scan_paused_for_the_day_is_replaced_by_the_full_scan()
    {
        var server = await Server("fs-full-replaces", ScanScheduleSettings.NewServer, lastScheduled: Due.AddDays(-1), full: FullScanSettings.NewServer);
        var quick = await Job(server, ScanJobState.Paused, Due.AddDays(-1), keepToWindow: true, error: ScanJobStore.WindowNote);

        Assert.Equal(1, await Ensure(Due.AddMinutes(2)));
        var jobs = await Jobs(server);
        Assert.Equal(2, jobs.Count);
        var old = jobs.Single(j => j.Id == quick);
        Assert.Equal((ScanJobState.Cancelled, ScanJobStore.ReplacedByFullNote), (old.State, old.LastError));
        Assert.NotNull(old.FinishedAt);
        var started = jobs.Single(j => j.Id != quick);
        Assert.Equal((ScanKind.Full, ScanJobState.Queued), (started.Kind, started.State));
    }

    [Fact]
    public async Task A_full_scan_waits_for_a_running_quick_scan_within_the_catch_up_time()
    {
        var server = await Server("fs-full-waits", ScanScheduleSettings.NewServer, lastScheduled: Due.AddDays(-1), full: FullScanSettings.NewServer);
        var quick = await Job(server, ScanJobState.Running, Due.AddHours(-2));

        Assert.Equal(0, await Ensure(Due.AddMinutes(2)));
        Assert.Single(await Jobs(server));
        Assert.True(await LastFullScheduled(server) < Due);

        await using (var db = database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == quick).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Completed));
        }
        Assert.Equal(1, await Ensure(Due.AddHours(2)));
        Assert.Contains(await Jobs(server), j => j is { Kind: ScanKind.Full, State: ScanJobState.Queued });
        Assert.Equal(Due, await LastFullScheduled(server));
    }

    [Fact]
    public async Task A_full_scan_missed_by_more_than_six_hours_waits_for_the_next_one()
    {
        var server = await Server("fs-full-missed", ScanScheduleSettings.Off, full: FullScanSettings.NewServer);
        Assert.Equal(0, await Ensure(Due + ScanSchedule.CatchUp + TimeSpan.FromMinutes(1)));
        Assert.Empty(await Jobs(server));
    }

    [Fact]
    public async Task A_full_scan_already_under_way_or_started_by_hand_is_never_doubled()
    {
        var running = await Server("fs-full-running", ScanScheduleSettings.NewServer, full: FullScanSettings.NewServer);
        await Job(running, ScanJobState.Running, Due.AddDays(-1), kind: ScanKind.Full);
        Assert.Equal(0, await Ensure(Due.AddMinutes(2)));
        Assert.Single(await Jobs(running));
        Assert.Equal(Due, await LastFullScheduled(running));

        var byHand = await Server("fs-full-by-hand", ScanScheduleSettings.NewServer, full: FullScanSettings.NewServer);
        await Job(byHand, ScanJobState.Completed, Due.AddMinutes(1), kind: ScanKind.Full);
        Assert.Equal(0, await Ensure(Due.AddMinutes(2)));
        Assert.Single(await Jobs(byHand));
    }

    [Fact]
    public async Task Without_quick_scans_a_full_scan_runs_until_done()
    {
        var server = await Server("fs-full-only", ScanScheduleSettings.Off, full: FullScanSettings.NewServer);
        await Ensure(Due.AddMinutes(2));
        var job = Assert.Single(await Jobs(server));
        Assert.Equal((ScanKind.Full, false), (job.Kind, job.KeepToWindow));
    }

    [Fact]
    public async Task Full_scans_that_are_off_or_on_a_removed_server_never_start()
    {
        var off = await Server("fs-full-off", ScanScheduleSettings.Off);
        var removed = await Server("fs-full-removed", ScanScheduleSettings.Off, state: EnrollmentState.Retired, full: FullScanSettings.NewServer);
        await Ensure(Due.AddMinutes(2));
        Assert.Empty(await Jobs(off));
        Assert.Empty(await Jobs(removed));
    }
}
