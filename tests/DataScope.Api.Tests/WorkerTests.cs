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

[Trait("Category", "Database")]
public sealed class WorkerTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private IConfiguration Config(int steps = 3) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Scanner:DryRunSteps"] = steps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Scanner:DryRunStepMilliseconds"] = "1",
        ["Scanner:LeaseSeconds"] = "30",
        ["ConnectionStrings:DataScope"] = database.ConnectionString,
    }).Build();

    private static ServiceProvider Services(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        return services.BuildServiceProvider();
    }

    private async Task<Guid> QueueJob(string host)
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now };
        db.AddRange(server, job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<ScanJob> Load(Guid id)
    {
        await using var db = database.CreateContext();
        return await db.ScanJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Worker_runs_a_queued_job_to_completion_with_checkpoints()
    {
        var id = await QueueJob("fs-worker-1");
        var config = Config();
        await using var sp = Services(config);
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), new DryRunScanExecutor(config), config, NullLogger<ScanWorker>.Instance);

        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        var job = await Load(id);
        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Equal(3, job.ItemsDone);
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.LeaseOwner);
    }

    [Fact]
    public async Task Worker_cancels_a_job_whose_server_was_removed_without_scanning()
    {
        var id = await QueueJob("fs-worker-removed");
        await using (var db = database.CreateContext())
        {
            var job = await db.ScanJobs.SingleAsync(j => j.Id == id);
            await db.ManagedServers.Where(s => s.Id == job.ServerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, EnrollmentState.Retired));
        }
        var config = Config();
        await using var sp = Services(config);
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), new DryRunScanExecutor(config), config, NullLogger<ScanWorker>.Instance);

        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        var stored = await Load(id);
        Assert.Equal(ScanJobState.Cancelled, stored.State);
        Assert.Equal(0, stored.ItemsDone);
        Assert.Equal("The server was removed from Data Scope.", stored.LastError);
    }

    [Fact]
    public async Task Worker_honours_a_cancel_request_at_the_next_checkpoint()
    {
        var id = await QueueJob("fs-worker-2");
        var config = Config(steps: 50);
        await using var sp = Services(config);
        var executor = new CancelAfterFirstStep(database, id);
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);

        await worker.RunOneAsync(CancellationToken.None);
        var job = await Load(id);
        Assert.Equal(ScanJobState.Cancelled, job.State);
        Assert.Equal(1, job.ItemsDone);
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_resumes_from_checkpoint()
    {
        var id = await QueueJob("fs-worker-3");
        await using var sp = Services(Config());
        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ScanJobStore>();
            var claimed = await store.ClaimNextAsync("crashed-worker", TimeSpan.FromSeconds(30));
            Assert.Equal(id, claimed!.Id);
            Assert.True(await store.CheckpointAsync(id, "crashed-worker", 2, 10, """{"Step":2}""", TimeSpan.FromSeconds(-1)));
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ScanJobStore>();
            var reclaimed = await store.ClaimNextAsync("new-worker", TimeSpan.FromSeconds(30));
            Assert.Equal(id, reclaimed!.Id);
            Assert.Equal(2, reclaimed.Attempts);
            Assert.Equal(2, reclaimed.ItemsDone);
            Assert.Contains("\"Step\": 2", reclaimed.Checkpoint, StringComparison.Ordinal);
            // The crashed worker can no longer write progress.
            Assert.False(await store.CheckpointAsync(id, "crashed-worker", 3, 10, null, TimeSpan.FromSeconds(30)));
        }
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_out_the_same_job_twice()
    {
        await using var cleanup = database.CreateContext();
        await cleanup.ScanJobs.Where(j => j.State == ScanJobState.Queued).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Cancelled));
        var ids = new[] { await QueueJob("fs-c1"), await QueueJob("fs-c2"), await QueueJob("fs-c3") };
        await using var sp = Services(Config());
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var scope = sp.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ScanJobStore>().ClaimNextAsync($"w{i}", TimeSpan.FromSeconds(30));
        }));
        var got = claims.Where(c => c is not null).Select(c => c!.Id).ToList();
        Assert.Equal(3, got.Count);
        Assert.Equal(ids.Order(), got.Order());
    }

    private sealed class CancelAfterFirstStep(TestDatabase db, Guid jobId) : IScanExecutor
    {
        public async Task<ScanControl> RunAsync(ScanContext context, CancellationToken ct)
        {
            for (var i = 1; i <= 50; i++)
            {
                if (i == 1)
                {
                    await using var ctx = db.CreateContext();
                    await ctx.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.RequestedState, ScanJobState.Cancelled), ct);
                }
                var control = await context.Report(i, 50, null, ct);
                if (control != ScanControl.Continue)
                {
                    return control;
                }
            }
            return ScanControl.Continue;
        }
    }
}
