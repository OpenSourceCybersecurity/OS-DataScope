using System.Text.Json;
using DataScope.ActiveDirectory;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Scanner.Security;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>Permission reads during scans, drift between scans, and the directory sync queue, against a real database.</summary>
[Trait("Category", "Database")]
public sealed class PermissionScanTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _database;
    private readonly string _lab = Directory.CreateTempSubdirectory("ds-perm-").FullName;

    public PermissionScanTests(TestDatabase database) => _database = database;

    public void Dispose() => Directory.Delete(_lab, recursive: true);

    private void Files(string host)
    {
        foreach (var path in new[] { "Data/a.txt", "Data/Sub/b.txt" })
        {
            var full = Path.Join(_lab, host, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "synthetic");
        }
    }

    private void Permissions(string host, object security) =>
        File.WriteAllText(Path.Join(_lab, host + ".security.json"), JsonSerializer.Serialize(security));

    private static object Open(string sid) => new
    {
        localGroups = Array.Empty<object>(),
        shares = new Dictionary<string, object?> { ["Data"] = new[] { new { sid = WellKnownSid.Everyone, rights = "Read" } } },
        folders = new Dictionary<string, object> { ["Data"] = new { aces = new[] { new { sid, rights = "Read" } } } },
    };

    private ServiceProvider Services(out IConfiguration config)
    {
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "60",
            ["Directory:SyncHours"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        return services.BuildServiceProvider();
    }

    private async Task<Guid> Scan(string host, ISecurityReader? reader, Guid? serverId = null)
    {
        await using (var db = _database.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            if (serverId is null)
            {
                var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
                db.ManagedServers.Add(server);
                serverId = server.Id;
            }
            db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = serverId.Value, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await using var sp = Services(out var config);
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var executor = new InventoryScanExecutor(scopes, new LabShareEnumerator(_lab), new FileSystemDirectorySource(),
            new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero, 100), NullLogger<InventoryScanExecutor>.Instance, null, reader);
        Assert.True(await new ScanWorker(scopes, executor, config, NullLogger<ScanWorker>.Instance).RunOneAsync(CancellationToken.None));
        await using var check = _database.CreateContext();
        Assert.Equal(ScanJobState.Completed, (await check.ScanJobs.Where(j => j.ServerId == serverId).OrderByDescending(j => j.CreatedAt).FirstAsync()).State);
        return serverId.Value;
    }

    private async Task<Dictionary<string, Folder>> Folders(Guid serverId)
    {
        await using var db = _database.CreateContext();
        return await (from f in db.Folders join s in db.Shares on f.ShareId equals s.Id where s.ServerId == serverId select f)
            .AsNoTracking().ToDictionaryAsync(f => f.Path);
    }

    [Fact]
    public async Task A_changed_permission_is_recorded_as_drift_on_the_next_scan()
    {
        Files("perm-drift");
        Permissions("perm-drift", Open(WellKnownSid.Everyone));
        var server = await Scan("perm-drift", new LabSecurityReader(_lab));
        var before = await Folders(server);
        Assert.All(before.Values, f => Assert.Equal(PermissionState.Read, f.SecurityState));

        Permissions("perm-drift", Open(WellKnownSid.AuthenticatedUsers));
        await Scan("perm-drift", new LabSecurityReader(_lab), server);
        var after = await Folders(server);

        await using var db = _database.CreateContext();
        var changes = await db.FolderSecurityChanges.Where(c => c.FolderId == before[""].Id || c.FolderId == before["Sub"].Id).ToListAsync();
        Assert.Equal(2, changes.Count);
        var root = changes.Single(c => c.FolderId == before[""].Id);
        Assert.Equal(before[""].SecurityId, root.FromDescriptorId);
        Assert.Equal(after[""].SecurityId, root.ToDescriptorId);
        Assert.NotEqual(before[""].SecurityId, after[""].SecurityId);
        Assert.True(await db.SecurityAces.AnyAsync(a => a.DescriptorId == after[""].SecurityId && a.Sid == WellKnownSid.AuthenticatedUsers));

        // An unchanged rescan adds nothing.
        await Scan("perm-drift", new LabSecurityReader(_lab), server);
        await using var again = _database.CreateContext();
        Assert.Equal(2, await again.FolderSecurityChanges.CountAsync(c => c.FolderId == before[""].Id || c.FolderId == before["Sub"].Id));
    }

    [Fact]
    public async Task Refused_failed_and_damaged_reads_are_recorded_and_clear_stale_permissions()
    {
        Files("perm-fail");
        Permissions("perm-fail", Open(WellKnownSid.Everyone));
        var server = await Scan("perm-fail", new LabSecurityReader(_lab));

        await Scan("perm-fail", new BrokenReader(), server);
        var folders = await Folders(server);
        Assert.Equal(PermissionState.Unreadable, folders[""].SecurityState);
        Assert.Null(folders[""].SecurityId);
        Assert.Equal(PermissionState.AccessDenied, folders["Sub"].SecurityState);

        await using var db = _database.CreateContext();
        var share = await db.Shares.SingleAsync(s => s.ServerId == server);
        Assert.Equal(PermissionState.Unreadable, share.SecurityState);
        var managed = await db.ManagedServers.SingleAsync(s => s.Id == server);
        Assert.Equal(LocalGroupsState.Failed, managed.LocalGroupsState);
        Assert.False(await db.ServerLocalGroups.AnyAsync(g => g.ServerId == server));
    }

    [Fact]
    public async Task Without_a_permission_reader_nothing_is_read_and_local_groups_default_to_denied_when_absent()
    {
        Files("perm-none");
        var server = await Scan("perm-none", null);
        Assert.All((await Folders(server)).Values, f => Assert.Equal(PermissionState.NotRead, f.SecurityState));

        // A lab file without localGroups behaves like a server that refuses to list them (Restrict remote SAM).
        Permissions("perm-none", new { shares = new Dictionary<string, object?> { ["Data"] = null } });
        await Scan("perm-none", new LabSecurityReader(_lab), server);
        await using var db = _database.CreateContext();
        Assert.Equal(LocalGroupsState.AccessDenied, (await db.ManagedServers.SingleAsync(s => s.Id == server)).LocalGroupsState);
        Assert.Equal(PermissionState.AccessDenied, (await db.Shares.SingleAsync(s => s.ServerId == server)).SecurityState);
    }

    [Fact]
    public async Task Directory_sync_queue_retries_failures_and_never_replaces_the_last_good_snapshot()
    {
        await using var sp = Services(out var config);
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var start = DateTimeOffset.UtcNow;
        var file = Path.Join(_lab, "dir.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            domain = "queue.test",
            domainSid = "S-1-5-21-7-8-9",
            principals = new[] { new { sid = "S-1-5-21-7-8-9-1001", kind = "User", name = "zoe" } },
        }));
        var worker = new DirectorySyncWorker(scopes, config, NullLogger<DirectorySyncWorker>.Instance, new LabDirectoryReader(file));

        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<DirectoryStore>();
            var (first, created) = await store.QueueAsync("S-1-5-21-1-1-1-500", CancellationToken.None);
            Assert.True(created);
            var (same, again) = await store.QueueAsync(null, CancellationToken.None);
            Assert.False(again);
            Assert.Equal(first.Id, same.Id);
        }
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        Guid good;
        await using (var db = _database.CreateContext())
        {
            var latest = await db.DirectorySnapshots.SingleAsync(s => s.State == DirectorySyncState.Completed && s.Domain == "queue.test");
            Assert.Equal(1, latest.Users);
            good = latest.Id;
        }

        // A broken directory file fails three attempts, then gives up; the earlier snapshot stays current.
        File.WriteAllText(file, "{ not json");
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DirectoryStore>().QueueAsync(null, CancellationToken.None);
        }
        for (var i = 0; i < DirectoryStore.MaxAttempts; i++)
        {
            Assert.True(await worker.RunOneAsync(CancellationToken.None));
        }
        Assert.False(await worker.RunOneAsync(CancellationToken.None));
        await using (var db = _database.CreateContext())
        {
            var failed = await db.DirectorySnapshots.SingleAsync(s => s.State == DirectorySyncState.Failed && s.RequestedAt >= start);
            Assert.Equal(DirectoryStore.MaxAttempts, failed.Attempts);
            Assert.NotNull(failed.LastError);
            Assert.DoesNotContain("not json", failed.LastError, StringComparison.Ordinal);
        }
        await using (var scope = sp.CreateAsyncScope())
        {
            Assert.Equal(good, (await scope.ServiceProvider.GetRequiredService<DirectoryStore>().LatestCompletedAsync(CancellationToken.None))!.Id);
        }

        // Without a reader configured, a requested sync fails at once with a setup hint instead of waiting forever.
        var unconfigured = new DirectorySyncWorker(scopes, config, NullLogger<DirectorySyncWorker>.Instance);
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DirectoryStore>().QueueAsync(null, CancellationToken.None);
        }
        Assert.True(await unconfigured.RunOneAsync(CancellationToken.None));
        await using (var db = _database.CreateContext())
        {
            Assert.Equal(2, await db.DirectorySnapshots.CountAsync(s => s.State == DirectorySyncState.Failed && s.RequestedAt >= start));
            Assert.False(await db.DirectorySnapshots.AnyAsync(s => s.State == DirectorySyncState.Queued));
        }
    }

    [Fact]
    public async Task Old_snapshots_are_pruned_with_their_principals()
    {
        await using var sp = Services(out _);
        await using var scope = sp.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DirectoryStore>();
        var result = new DirectoryReadResult("prune.test", "S-1-5-21-4-5-6", "DC=prune,DC=test", "dc", [
            new DirectoryObject("S-1-5-21-4-5-6-1001", PrincipalKind.User, "u", "CN=u,DC=prune,DC=test"),
        ], [], [], 0, false);
        for (var i = 0; i < DirectoryStore.KeepSnapshots + 2; i++)
        {
            var (snapshot, _) = await store.QueueAsync(null, CancellationToken.None);
            var claimed = await store.ClaimNextAsync("w", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(snapshot.Id, claimed!.Id);
            Assert.True(await store.CompleteAsync(claimed.Id, "w", result, CancellationToken.None));
            // Someone else's lease cannot complete a snapshot.
            Assert.False(await store.CompleteAsync(claimed.Id, "other", result, CancellationToken.None));
        }
        await using var db = _database.CreateContext();
        var kept = await db.DirectorySnapshots.Where(s => s.Domain == "prune.test").Select(s => s.Id).ToListAsync();
        Assert.Equal(DirectoryStore.KeepSnapshots, kept.Count);
        Assert.Equal(DirectoryStore.KeepSnapshots, await db.DirectoryPrincipals.CountAsync(p => kept.Contains(p.SnapshotId)));
        Assert.Equal(DirectoryStore.KeepSnapshots, await db.DirectoryPrincipals.CountAsync(p => p.Sid == "S-1-5-21-4-5-6-1001"));
    }

    /// <summary>A reader that fails in every way a real server can.</summary>
    private sealed class BrokenReader : ISecurityReader
    {
        public SecurityRead ReadShare(string hostname, string shareName) => new(PermissionState.Read, [1, 2, 3]);

        public SecurityRead ReadFolder(string fullPath) => Path.GetFileName(fullPath) == "Sub"
            ? SecurityRead.Denied("The scan account may not read this folder's permissions.")
            : throw new UnauthorizedAccessException("boom");

        public LocalGroupsRead ReadLocalGroups(string hostname) => throw new InvalidOperationException("boom");
    }
}
