using DataScope.Core.Classification;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Scanner.Content;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

public sealed class PacerTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Steps_are_spaced_to_the_rate_with_no_allowance_at_the_start()
    {
        var pacer = new Pacer(10, TimeProvider.System);
        var start = TimeSpan.FromSeconds(100);
        Assert.Equal(TimeSpan.Zero, pacer.Next(start));
        Assert.Equal(Ms(100), pacer.Next(start));
        Assert.Equal(Ms(200), pacer.Next(start));
        // Work that took longer than the spacing needs no wait.
        Assert.Equal(TimeSpan.Zero, pacer.Next(start + Ms(500)));
    }

    [Fact]
    public void A_long_pause_earns_at_most_one_second_of_catching_up()
    {
        var pacer = new Pacer(10, TimeProvider.System);
        pacer.Next(TimeSpan.Zero);
        var later = TimeSpan.FromMinutes(5);
        var free = 0;
        while (pacer.Next(later) == TimeSpan.Zero)
        {
            free++;
        }
        Assert.Equal(11, free); // one second at ten per second, plus the step due now
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Zero_or_less_means_no_limit(int rate)
    {
        var pacer = new Pacer(rate, TimeProvider.System);
        Assert.False(pacer.Enabled);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(TimeSpan.Zero, pacer.Next(TimeSpan.Zero));
        }
        Assert.True(pacer.WaitAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_wait_stops_when_the_scan_is_cancelled()
    {
        // A clock that never moves: the second step always has to wait, however slow the test machine is.
        var pacer = new Pacer(1, new StoppedClock());
        await pacer.WaitAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.WaitAsync(cancel.Token));
    }

    /// <summary>Time stands still and timers never fire, so only cancellation can end a wait.</summary>
    private sealed class StoppedClock : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NeverFires();

        private sealed class NeverFires : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Limits_come_from_settings_and_refuse_negative_rates()
    {
        static ScanLimits Load(params (string, string)[] pairs) => ScanLimits.From(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create(p.Item1, (string?)p.Item2))).Build());

        var defaults = Load();
        Assert.Equal(0, defaults.MaxFoldersPerSecond);
        Assert.Equal(0, defaults.MaxFilesReadPerSecond);
        var gentle = Load(("Scanner:MaxFoldersPerSecond", "50"), ("Scanner:MaxFilesReadPerSecond", "20"));
        Assert.Equal(50, gentle.MaxFoldersPerSecond);
        Assert.Equal(20, gentle.MaxFilesReadPerSecond);
        Assert.Throws<InvalidOperationException>(() => Load(("Scanner:MaxFilesReadPerSecond", "-1")));
    }
}

/// <summary>A throttled scan waits between folders and between file reads, and still covers everything.</summary>
[Trait("Category", "Database")]
public sealed class ScanThrottleTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _database;
    private readonly string _lab = Directory.CreateTempSubdirectory("ds-throttle-").FullName;

    public ScanThrottleTests(TestDatabase database) => _database = database;

    public void Dispose() => Directory.Delete(_lab, recursive: true);

    [Fact]
    public async Task Waits_add_up_to_the_configured_rates_and_skipped_files_cost_nothing()
    {
        const string host = "throttle-fs01";
        for (var f = 0; f < 9; f++)
        {
            var folder = Path.Join(_lab, host, "Data", $"Folder {f}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Join(folder, "notes.txt"), "Nothing sensitive.");
            File.WriteAllBytes(Path.Join(folder, "setup.exe"), [0x4D, 0x5A, 0x90, 0x00]); // skipped by type, never opened
        }
        await using (var db = _database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.State == Core.ScanJobs.ScanJobState.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, Core.ScanJobs.ScanJobState.Cancelled));
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            db.ManagedServers.Add(server);
            db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "60",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var clock = new InstantClock();
        var limits = new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero, 100, MaxFoldersPerSecond: 5, MaxFilesReadPerSecond: 2);
        var executor = new InventoryScanExecutor(sp.GetRequiredService<IServiceScopeFactory>(), new LabShareEnumerator(_lab),
            new FileSystemDirectorySource(), limits, NullLogger<InventoryScanExecutor>.Instance, new FileContentReader(new ContentLimits()), time: clock);
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);
        Assert.True(await worker.RunOneAsync(CancellationToken.None));

        await using var check = _database.CreateContext();
        var job = await check.ScanJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync();
        Assert.Equal(Core.ScanJobs.ScanJobState.Completed, job.State);
        var files = await (from f in check.Files
                           join s in check.Shares on f.ShareId equals s.Id
                           join m in check.ManagedServers on s.ServerId equals m.Id
                           where m.Hostname == host
                           select f).AsNoTracking().ToListAsync();
        Assert.Equal(18, files.Count);
        Assert.All(files.Where(f => f.Extension == ".txt"), f => Assert.Equal(ContentState.Scanned, f.ContentState));
        Assert.All(files.Where(f => f.Extension == ".exe"), f => Assert.Equal(ContentState.Unsupported, f.ContentState));

        // Ten folders (the share root and nine below it) at five a second: nine waits of 200 ms.
        // Nine text files read at two a second: eight waits of 500 ms. The programs are never opened.
        Assert.Equal(TimeSpan.FromMilliseconds(9 * 200 + 8 * 500), clock.Waited);
    }

    /// <summary>A clock that only moves when someone waits on it, so throttled scans run instantly in tests.</summary>
    private sealed class InstantClock : TimeProvider
    {
        private readonly Lock _lock = new();
        private long _ticks;

        public TimeSpan Waited { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_lock)
            {
                return _ticks;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                _ticks += dueTime.Ticks;
                Waited += dueTime;
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Fired();
        }

        private sealed class Fired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
