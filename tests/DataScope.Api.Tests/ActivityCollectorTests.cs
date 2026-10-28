using DataScope.Core.Activity;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner.Activity;
using DataScope.Tests.Activity;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>The collector end to end: a lab Security log on disk, read in batches into the database.</summary>
[Trait("Category", "Database")]
public sealed class ActivityCollectorTests(TestDatabase database) : IClassFixture<TestDatabase>, IDisposable
{
    private readonly string _lab = Directory.CreateTempSubdirectory("datascope-activity-").FullName;

    public void Dispose() => Directory.Delete(_lab, recursive: true);

    private IConfiguration Config(int batch = 2, int maxPerPass = 100) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DataScope"] = database.ConnectionString,
        ["Activity:BatchSize"] = batch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Activity:MaxEventsPerPass"] = maxPerPass.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Activity:PollSeconds"] = "60",
    }).Build();

    private static ServiceProvider Services(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        return services.BuildServiceProvider();
    }

    private static ActivityCollector Collector(ServiceProvider sp, IConfiguration config, ISecurityEventSource? source) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), config, TimeProvider.System, NullLogger<ActivityCollector>.Instance, source);

    /// <summary>Creates a server with collection on or off; every other server in this class's database stops being due.</summary>
    private async Task<Guid> Server(string host, bool enabled = true)
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        db.Add(server);
        await db.SaveChangesAsync();
        await db.ActivitySources.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextReadAt, DateTimeOffset.UtcNow.AddHours(1)));
        await new ActivityStore(db, TimeProvider.System).SetEnabledAsync(server.Id, enabled, "S-1", default);
        return server.Id;
    }

    private void WriteLog(string host, params string[] events) =>
        File.WriteAllText(Path.Combine(_lab, $"{host}.security-events.xml"), "<Events>" + string.Concat(events) + "</Events>");

    private async Task<ActivitySource> Source(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ActivitySources.AsNoTracking().SingleAsync(s => s.ServerId == server);
    }

    private async Task<int> Count(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.FileAuditEvents.CountAsync(e => e.ServerId == server);
    }

    /// <summary>Makes a source due again without waiting for the poll interval.</summary>
    private async Task MakeDue(Guid server)
    {
        await using var db = database.CreateContext();
        await db.ActivitySources.Where(s => s.ServerId == server).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextReadAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public async Task Reads_a_log_in_batches_and_resumes_where_it_stopped()
    {
        var server = await Server("fs-collect-1");
        WriteLog("fs-collect-1",
            EventXml.ObjectAccess(101, @"D:\Shares\Finance\a.xlsx", 0x1),
            EventXml.ObjectAccess(102, @"D:\Shares\Finance\b.xlsx", 0x2),
            EventXml.Event(4624, 103, []),
            EventXml.ObjectAccess(104, @"D:\Shares\Finance\c.xlsx", 0x1));
        var config = Config(batch: 2, maxPerPass: 2);
        await using var sp = Services(config);
        var collector = Collector(sp, config, new LabSecurityEventSource(_lab));

        // The pass limit stops after two events and asks to come back straight away.
        Assert.True(await collector.RunOneAsync(default));
        var source = await Source(server);
        Assert.Equal((102L, ActivitySourceState.Ok, 2), (source.LastRecordId!.Value, source.State, await Count(server)));
        Assert.True(source.NextReadAt <= DateTimeOffset.UtcNow);
        Assert.Null(source.LeaseOwner);

        Assert.True(await collector.RunOneAsync(default));
        source = await Source(server);
        Assert.Equal((104L, 3, 1L), (source.LastRecordId!.Value, await Count(server), source.EventsIgnored));

        // A pass that finds nothing more waits the poll interval.
        Assert.True(await collector.RunOneAsync(default));
        source = await Source(server);
        Assert.Equal(104L, source.LastRecordId);
        Assert.True(source.NextReadAt > DateTimeOffset.UtcNow.AddSeconds(30));

        // Nothing is due until the interval passes; then nothing new is stored.
        Assert.False(await collector.RunOneAsync(default));
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));
        Assert.Equal(3, await Count(server));
    }

    [Fact]
    public async Task A_failed_read_keeps_the_position_and_says_why()
    {
        var server = await Server("fs-collect-2");
        var config = Config();
        await using var sp = Services(config);
        var collector = Collector(sp, config, new LabSecurityEventSource(_lab));

        Assert.True(await collector.RunOneAsync(default));
        var source = await Source(server);
        Assert.Equal(ActivitySourceState.Error, source.State);
        Assert.Contains("no Security log for fs-collect-2", source.LastError);
        Assert.Null(source.LastRecordId);
        Assert.Null(source.LastReadAt);

        WriteLog("fs-collect-2", EventXml.ObjectAccess(7, @"D:\x.txt", 0x1));
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));
        source = await Source(server);
        Assert.Equal((ActivitySourceState.Ok, 7L), (source.State, source.LastRecordId!.Value));
        Assert.NotNull(source.LastReadAt);
    }

    [Fact]
    public async Task A_damaged_lab_log_is_an_error_not_a_crash()
    {
        var server = await Server("fs-collect-3");
        File.WriteAllText(Path.Combine(_lab, "fs-collect-3.security-events.xml"), "<Events><Event><System>");
        var config = Config();
        await using var sp = Services(config);

        Assert.True(await Collector(sp, config, new LabSecurityEventSource(_lab)).RunOneAsync(default));
        Assert.Contains("not well-formed", (await Source(server)).LastError);
    }

    [Fact]
    public async Task Servers_with_collection_off_are_not_read_and_without_a_source_it_says_so()
    {
        var off = await Server("fs-collect-4", enabled: false);
        WriteLog("fs-collect-4", EventXml.ObjectAccess(1, @"D:\x.txt", 0x1));
        var config = Config();
        await using var sp = Services(config);
        Assert.False(await Collector(sp, config, new LabSecurityEventSource(_lab)).RunOneAsync(default));
        Assert.Equal(0, await Count(off));

        var on = await Server("fs-collect-5");
        Assert.True(await Collector(sp, config, source: null).RunOneAsync(default));
        var source = await Source(on);
        Assert.Equal((ActivitySourceState.Error, ActivityCollector.NotAvailable), (source.State, source.LastError));
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData(@"a\b")]
    [InlineData("c:x")]
    public async Task The_lab_source_refuses_names_that_could_leave_its_folder(string host) =>
        await Assert.ThrowsAsync<SecurityEventReadException>(() => new LabSecurityEventSource(_lab).ReadAsync(host, null, 10, default));

    [Fact]
    public async Task The_lab_source_returns_events_after_a_position_oldest_first()
    {
        WriteLog("fs-order", EventXml.ObjectAccess(30, @"D:\c", 1), EventXml.ObjectAccess(10, @"D:\a", 1), EventXml.ObjectAccess(20, @"D:\b", 1));
        var source = new LabSecurityEventSource(_lab);
        Assert.Equal([10L, 20], (await source.ReadAsync("fs-order", null, 2, default)).Select(e => e.RecordId));
        Assert.Equal([30L], (await source.ReadAsync("fs-order", 20, 5, default)).Select(e => e.RecordId));
        var xml = (await source.ReadAsync("fs-order", 20, 5, default))[0].Xml;
        Assert.Equal(30, SecurityEventXml.Parse(xml).RecordId);
    }

    /// <summary>A Security log in memory: the collected events, and the log's full range of record ids.</summary>
    private sealed class FakeLog : ISecurityEventSource
    {
        public List<RawSecurityEvent> Events { get; } = [];
        public long? Oldest { get; set; }
        public long? Newest { get; set; }
        public int Reads { get; private set; }

        public void Hold(long oldest, long newest, params long[] fileEvents)
        {
            (Oldest, Newest) = (oldest, newest);
            Events.Clear();
            Events.AddRange(fileEvents.Select(id => new RawSecurityEvent(id, EventXml.ObjectAccess(id, $@"D:\Shares\x{id}.txt", 0x1))));
        }

        public Task<IReadOnlyList<RawSecurityEvent>> ReadAsync(string hostname, long? afterRecordId, int max, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<RawSecurityEvent>>(Events.Where(e => afterRecordId is null || e.RecordId > afterRecordId).OrderBy(e => e.RecordId).Take(max).ToList());
        }

        public Task<SecurityLogInfo> GetLogInfoAsync(string hostname, CancellationToken ct) =>
            Task.FromResult(new SecurityLogInfo(Oldest, Newest, Newest - Oldest + 1, DateTimeOffset.UtcNow.AddDays(-2), 20_971_520, 134_217_728, false));
    }

    private async Task<List<ActivityGap>> Gaps(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ActivityGaps.AsNoTracking().Where(g => g.ServerId == server).OrderBy(g => g.Id).ToListAsync();
    }

    private async Task<List<(long RecordId, int Generation)>> Stored(Guid server)
    {
        await using var db = database.CreateContext();
        return (await db.FileAuditEvents.AsNoTracking().Where(e => e.ServerId == server).OrderBy(e => e.Id)
            .Select(e => new { e.RecordId, e.LogGeneration }).ToListAsync()).Select(e => (e.RecordId, e.LogGeneration)).ToList();
    }

    [Fact]
    public async Task A_read_that_catches_up_moves_past_records_that_were_not_file_events()
    {
        var server = await Server("fs-gap-1");
        var log = new FakeLog();
        log.Hold(1, 100, 3, 7);
        var config = Config(batch: 10);
        await using var sp = Services(config);
        var collector = Collector(sp, config, log);

        Assert.True(await collector.RunOneAsync(default));
        var source = await Source(server);
        Assert.Equal(100L, source.LastRecordId);
        Assert.Equal((1L, 100L, 100L, 134_217_728L, false), (source.LogOldestRecordId!.Value, source.LogNewestRecordId!.Value, source.LogRecordCount!.Value, source.LogMaxSizeBytes!.Value, source.LogFull!.Value));
        Assert.NotNull(source.LogCheckedAt);

        // Records 8 to 100 were never file events; the log moving on past them is not a gap.
        log.Hold(90, 150, 120);
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));
        Assert.Empty(await Gaps(server));
        Assert.Equal([3L, 7, 120], (await Stored(server)).Select(e => e.RecordId));
    }

    [Fact]
    public async Task Records_overwritten_before_they_were_read_are_recorded_as_a_gap()
    {
        var server = await Server("fs-gap-2");
        var log = new FakeLog();
        log.Hold(1, 10, 2, 4, 6, 8, 10);
        var config = Config(batch: 2, maxPerPass: 2);
        await using var sp = Services(config);
        var collector = Collector(sp, config, log);

        // The pass limit stops after records 2 and 4: not caught up, so the position stays at 4.
        Assert.True(await collector.RunOneAsync(default));
        Assert.Equal(4L, (await Source(server)).LastRecordId);

        // Before the next pass the log wraps: 5 to 19 are gone.
        log.Hold(20, 30, 22, 24);
        Assert.True(await collector.RunOneAsync(default));
        var gap = Assert.Single(await Gaps(server));
        Assert.Equal((ActivityGapReason.Overwritten, 5L, 19L, 0), (gap.Reason, gap.FirstRecordId, gap.LastRecordId!.Value, gap.LogGeneration));
        Assert.NotNull(gap.LogOldestEventAt);
        Assert.Equal([2L, 4, 22, 24], (await Stored(server)).Select(e => e.RecordId));

        // The same gap is not recorded again.
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));
        Assert.Single(await Gaps(server));
    }

    [Fact]
    public async Task A_log_that_starts_again_gets_a_new_generation_and_its_records_are_kept_apart()
    {
        var server = await Server("fs-gap-3");
        var log = new FakeLog();
        log.Hold(1, 50, 10, 20);
        var config = Config(batch: 10);
        await using var sp = Services(config);
        var collector = Collector(sp, config, log);
        Assert.True(await collector.RunOneAsync(default));

        // Cleared and renumbered: record 10 is a different event now.
        log.Hold(1, 12, 1, 10);
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));

        var gap = Assert.Single(await Gaps(server));
        Assert.Equal((ActivityGapReason.LogReset, 51L, (long?)null, 0), (gap.Reason, gap.FirstRecordId, gap.LastRecordId, gap.LogGeneration));
        var source = await Source(server);
        Assert.Equal((1, 12L), (source.LogGeneration, source.LastRecordId!.Value));
        Assert.Equal([(10L, 0), (20L, 0), (1L, 1), (10L, 1)], await Stored(server));
    }

    [Fact]
    public async Task A_failed_log_check_keeps_the_position_and_reads_nothing()
    {
        var server = await Server("fs-gap-4");
        var config = Config();
        await using var sp = Services(config);
        var log = new FailingLog();
        Assert.True(await Collector(sp, config, log).RunOneAsync(default));
        var source = await Source(server);
        Assert.Equal((ActivitySourceState.Error, (long?)null), (source.State, source.LastRecordId));
        Assert.Contains("Event Log Readers", source.LastError);
        Assert.Equal(0, log.Reads);
    }

    private sealed class FailingLog : ISecurityEventSource
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<RawSecurityEvent>> ReadAsync(string hostname, long? afterRecordId, int max, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<RawSecurityEvent>>([]);
        }

        public Task<SecurityLogInfo> GetLogInfoAsync(string hostname, CancellationToken ct) =>
            throw new SecurityEventReadException($"Access denied reading the Security log on {hostname}. Add Data Scope's scan account to the Event Log Readers group on {hostname}.");
    }

    [Fact]
    public async Task Gaps_cannot_be_changed_or_removed_by_the_services()
    {
        var server = await Server("fs-gap-5");
        var log = new FakeLog();
        log.Hold(1, 5, 2);
        var config = Config();
        await using var sp = Services(config);
        var collector = Collector(sp, config, log);
        Assert.True(await collector.RunOneAsync(default));
        log.Hold(10, 12, 11);
        await MakeDue(server);
        Assert.True(await collector.RunOneAsync(default));
        var id = Assert.Single(await Gaps(server)).Id;

        await using var runtime = database.CreateContext();
        var update = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => runtime.Database.ExecuteSqlAsync($"UPDATE activity_gaps SET first_record_id = 6 WHERE id = {id}"));
        var delete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => runtime.Database.ExecuteSqlAsync($"DELETE FROM activity_gaps WHERE id = {id}"));
        Assert.Equal((Npgsql.PostgresErrorCodes.InsufficientPrivilege, Npgsql.PostgresErrorCodes.InsufficientPrivilege), (update.SqlState, delete.SqlState));
    }

    [Fact]
    public async Task The_lab_log_reports_its_range_from_the_file()
    {
        WriteLog("fs-range", EventXml.ObjectAccess(30, @"D:\c", 1), EventXml.ObjectAccess(10, @"D:\a", 1));
        var info = await new LabSecurityEventSource(_lab).GetLogInfoAsync("fs-range", default);
        Assert.Equal((10L, 30L, 2L), (info.OldestRecordId!.Value, info.NewestRecordId!.Value, info.RecordCount!.Value));
        Assert.NotNull(info.OldestEventAt);
    }
}
