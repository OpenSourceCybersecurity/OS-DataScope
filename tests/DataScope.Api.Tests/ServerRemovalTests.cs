using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>Removing a server from Data Scope, and enrolling it again, over synthetic inventory.</summary>
[Trait("Category", "Database")]
public sealed class ServerRemovalTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ServerRemovalTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin);

    private sealed record PageDto<T>(List<T> Items, int Total);
    private sealed record ServerDto(Guid Id, string Hostname, string State);
    private sealed record ShareDto(Guid Id, Guid ServerId, string State);
    private sealed record RemovalDto(int ScansCancelled, int Shares, long Folders, long Files, bool ActivityStopped);

    /// <summary>One enrolled server with a scanned share (two folders, two files), a queued scan and activity collection on.</summary>
    private async Task<(Guid Server, Guid Share, Guid QueuedJob)> Seed(string host, ScanJobState jobState = ScanJobState.Queued)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var done = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = ScanJobState.Completed };
        var next = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = jobState };
        var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "HR", NameKey = "HR", UncPath = $@"\\{host}\HR", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, LastScanJobId = done.Id, FileCount = 2, CreatedAt = now, UpdatedAt = now };
        Folder F(string path, Guid? parent, int depth) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = share.Id,
            ParentId = parent,
            Path = path,
            PathHash = InventoryStore.PathHash(path),
            Depth = depth,
            FirstSeenJobId = done.Id,
            LastSeenJobId = done.Id,
            LastWalkedJobId = done.Id,
        };
        var root = F("", null, 0);
        var payroll = F("Payroll", root.Id, 1);
        InventoryFile File(string name) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = share.Id,
            FolderId = payroll.Id,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Extension = PathText.Extension(name),
            Size = 10,
            FirstSeenJobId = done.Id,
            LastSeenJobId = done.Id,
            Sensitivity = Sensitivity.Restricted,
        };
        db.AddRange(server, done, next, share);
        await db.SaveChangesAsync();
        db.Add(root);
        await db.SaveChangesAsync();
        db.Add(payroll);
        await db.SaveChangesAsync();
        db.AddRange(File("march.xlsx"), File("april.xlsx"));
        await db.SaveChangesAsync();
        await new ActivityStore(db, TimeProvider.System).SetEnabledAsync(server.Id, true, "S-1", default);
        return (server.Id, share.Id, next.Id);
    }

    private async Task<List<T>> Items<T>(string url) => (await Admin.GetFromJsonAsync<PageDto<T>>(url))!.Items;

    [Fact]
    public async Task Removing_a_server_takes_it_out_of_scans_and_screens_and_keeps_its_history()
    {
        var (server, share, queued) = await Seed("fs-remove-1");

        var res = await Admin.DeleteAsync($"/api/v1/servers/{server}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new RemovalDto(1, 1, 2, 2, true), await res.Content.ReadFromJsonAsync<RemovalDto>());

        await using var db = _db.CreateContext();
        Assert.Equal(EnrollmentState.Retired, (await db.ManagedServers.SingleAsync(s => s.Id == server)).State);
        var job = await db.ScanJobs.SingleAsync(j => j.Id == queued);
        Assert.Equal(ScanJobState.Cancelled, job.State);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal(ShareState.Removed, (await db.Shares.SingleAsync(s => s.Id == share)).State);
        Assert.Equal(0, await db.Folders.CountAsync(f => f.ShareId == share && f.RemovedAt == null));
        Assert.Equal(2, await db.Files.CountAsync(f => f.ShareId == share && f.RemovedAt != null));
        Assert.False((await db.ActivitySources.SingleAsync(a => a.ServerId == server)).Enabled);
        var audit = await db.AppAuditLog.Where(a => a.Action == "server.remove").OrderByDescending(a => a.Sequence).FirstAsync();
        Assert.Equal("fs-remove-1", audit.Target);
        Assert.Contains("\"Files\":2", audit.Details, StringComparison.Ordinal);

        Assert.DoesNotContain(await Items<ServerDto>("/api/v1/servers?limit=500"), s => s.Id == server);
        Assert.Contains(await Items<ServerDto>("/api/v1/servers?limit=500&includeRemoved=true"), s => s.Id == server && s.State == "Retired");
        Assert.DoesNotContain(await Items<ShareDto>("/api/v1/shares?limit=500"), s => s.Id == share);
        Assert.Single(await Items<ShareDto>($"/api/v1/shares?serverId={server}"));

        var scan = await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server });
        Assert.Equal(HttpStatusCode.Conflict, scan.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.DeleteAsync($"/api/v1/servers/{server}")).StatusCode);
    }

    [Fact]
    public async Task A_running_scan_must_be_cancelled_first_and_nothing_changes()
    {
        var (server, share, running) = await Seed("fs-remove-2", ScanJobState.Running);

        var res = await Admin.DeleteAsync($"/api/v1/servers/{server}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("Cancel it, then remove the server", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var db = _db.CreateContext();
        Assert.Equal(EnrollmentState.Enrolled, (await db.ManagedServers.SingleAsync(s => s.Id == server)).State);
        Assert.Equal(ScanJobState.Running, (await db.ScanJobs.SingleAsync(j => j.Id == running)).State);
        Assert.Equal(ShareState.Scanned, (await db.Shares.SingleAsync(s => s.Id == share)).State);
        Assert.Equal(0, await db.Files.CountAsync(f => f.ShareId == share && f.RemovedAt != null));
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.DataOwner)]
    public async Task Only_platform_admins_can_remove_a_server(string role)
    {
        var (server, _, _) = await Seed($"fs-remove-{role.ToLowerInvariant()}");
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(role).DeleteAsync($"/api/v1/servers/{server}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false).DeleteAsync($"/api/v1/servers/{server}")).StatusCode);
        await using var db = _db.CreateContext();
        Assert.Equal(EnrollmentState.Enrolled, (await db.ManagedServers.SingleAsync(s => s.Id == server)).State);
    }

    [Fact]
    public async Task An_unknown_server_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/api/v1/servers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Enrolling_a_removed_server_again_brings_it_back_with_its_history()
    {
        var (server, share, _) = await Seed("fs-remove-3");
        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync($"/api/v1/servers/{server}")).StatusCode);

        var res = await Admin.PostAsJsonAsync("/api/v1/servers", new { hostname = "FS-REMOVE-3" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var back = await res.Content.ReadFromJsonAsync<ServerDto>();
        Assert.Equal(server, back!.Id);
        Assert.Equal("Pending", back.State);
        Assert.Contains(await Items<ServerDto>("/api/v1/servers?limit=500"), s => s.Id == server);
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server })).StatusCode);

        // Enrolling it once more is a duplicate again.
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-remove-3" })).StatusCode);

        await using var db = _db.CreateContext();
        Assert.Equal(2, await db.Files.CountAsync(f => f.ShareId == share));
        Assert.True(await db.AppAuditLog.AnyAsync(a => a.Action == "server.restore" && a.Target == "fs-remove-3"));
        // Activity collection stays off until an admin turns it on again.
        Assert.False((await db.ActivitySources.SingleAsync(a => a.ServerId == server)).Enabled);
    }

    [Fact]
    public async Task The_next_scan_of_a_restored_server_brings_its_shares_back()
    {
        var (server, share, _) = await Seed("fs-remove-4");
        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync($"/api/v1/servers/{server}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-remove-4" })).StatusCode);

        await using var db = _db.CreateContext();
        await new InventoryStore(db, TimeProvider.System).SyncSharesAsync(server, "fs-remove-4", [new DiscoveredShare("HR", null, ShareKind.Disk)], default);
        Assert.Equal(ShareState.NotScanned, (await db.Shares.AsNoTracking().SingleAsync(s => s.Id == share)).State);
        Assert.Equal(EnrollmentState.Enrolled, (await db.ManagedServers.AsNoTracking().SingleAsync(s => s.Id == server)).State);
        Assert.Contains(await Items<ShareDto>("/api/v1/shares?limit=500"), s => s.Id == share);
    }
}
