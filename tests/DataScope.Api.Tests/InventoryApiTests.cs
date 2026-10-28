using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

[Trait("Category", "Database")]
public sealed class InventoryApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public InventoryApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);
    private sealed record ShareDto(Guid Id, Guid ServerId, string Hostname, string Name, string Kind, bool Included, string State, long FileCount, int GapCount, string? ExclusionReason = null, string? ExcludedByName = null);
    private sealed record FolderDto(Guid Id, string Name, string Path, bool Listed, bool Removed);
    private sealed record FileDto(Guid Id, string Name, string Extension, long Size, bool Removed);
    private sealed record GapDto(string? ShareName, string Path, string Kind, string Message);
    private sealed record CoverageDto(int SharesTotal, int SharesIncluded, int SharesScanned, int SharesPartial, long Files, Dictionary<string, int> GapsByKind);

    /// <summary>Seeds one scanned server: HR (partial, one gap) with a folder tree, and an admin share.</summary>
    private async Task<(Guid Server, Guid Hr, Guid AdminShare, Guid Job, Guid Payroll)> Seed(string host)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
        var hr = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "HR", NameKey = "HR", UncPath = $@"\\{host}\HR", Kind = ShareKind.Disk, Included = true, State = ShareState.Partial, LastScanJobId = job.Id, FileCount = 2, GapCount = 1, CreatedAt = now, UpdatedAt = now };
        var admin = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "C$", NameKey = "C$", UncPath = $@"\\{host}\C$", Kind = ShareKind.Special, CreatedAt = now, UpdatedAt = now };
        Folder F(string path, Guid? parent, int depth, bool removed = false) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = hr.Id,
            ParentId = parent,
            Path = path,
            PathHash = InventoryStore.PathHash(path),
            Depth = depth,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            LastWalkedJobId = path == "Restricted" ? null : job.Id,
            RemovedAt = removed ? now : null,
        };
        var root = F("", null, 0);
        var payroll = F("Payroll", root.Id, 1);
        var restricted = F("Restricted", root.Id, 1);
        var old = F("Old", root.Id, 1, removed: true);
        InventoryFile File(string name, bool removed = false) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = hr.Id,
            FolderId = payroll.Id,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Extension = PathText.Extension(name),
            Size = 10,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            RemovedAt = removed ? now : null,
        };
        db.AddRange(server, job, hr, admin);
        await db.SaveChangesAsync();
        db.AddRange(root);
        await db.SaveChangesAsync();
        db.AddRange(payroll, restricted, old);
        await db.SaveChangesAsync();
        db.AddRange(File("march.xlsx"), File("april.xlsx"), File("gone.xlsx", removed: true));
        db.ScanGaps.Add(new ScanGap { JobId = job.Id, ShareId = hr.Id, Path = "Restricted", Kind = GapKind.AccessDenied, Message = "Access denied to the scan identity.", At = now });
        await db.SaveChangesAsync();
        return (server.Id, hr.Id, admin.Id, job.Id, payroll.Id);
    }

    [Fact]
    public async Task Shares_list_shows_state_and_coverage_for_a_server()
    {
        var s = await Seed("api-inv-1");
        var page = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<PageDto<ShareDto>>($"/api/v1/shares?serverId={s.Server}");
        Assert.Equal(2, page!.Total);
        var hr = page.Items.Single(x => x.Name == "HR");
        Assert.Equal("api-inv-1", hr.Hostname);
        Assert.Equal("Partial", hr.State);
        Assert.Equal(1, hr.GapCount);
        Assert.Equal("Special", page.Items.Single(x => x.Name == "C$").Kind);
    }

    [Fact]
    public async Task Folder_browsing_hides_removed_folders_unless_asked_and_shows_unlisted_ones()
    {
        var s = await Seed("api-inv-2");
        var client = _factory.ClientAs(Roles.SecurityAnalyst);

        var top = await client.GetFromJsonAsync<PageDto<FolderDto>>($"/api/v1/shares/{s.Hr}/folders");
        Assert.Equal(["Payroll", "Restricted"], top!.Items.Select(f => f.Name));
        Assert.False(top.Items.Single(f => f.Name == "Restricted").Listed);

        var all = await client.GetFromJsonAsync<PageDto<FolderDto>>($"/api/v1/shares/{s.Hr}/folders?includeRemoved=true");
        Assert.Contains(all!.Items, f => f.Name == "Old" && f.Removed);

        var files = await client.GetFromJsonAsync<PageDto<FileDto>>($"/api/v1/folders/{s.Payroll}/files");
        Assert.Equal(["april.xlsx", "march.xlsx"], files!.Items.Select(f => f.Name));
        Assert.Equal("xlsx", files.Items[0].Extension);
    }

    [Fact]
    public async Task Gaps_are_visible_per_share_and_per_job()
    {
        var s = await Seed("api-inv-3");
        var client = _factory.ClientAs(Roles.Auditor);
        var byShare = await client.GetFromJsonAsync<PageDto<GapDto>>($"/api/v1/shares/{s.Hr}/gaps");
        var gap = Assert.Single(byShare!.Items);
        Assert.Equal(("HR", "Restricted", "AccessDenied"), (gap.ShareName, gap.Path, gap.Kind));
        var byJob = await client.GetFromJsonAsync<PageDto<GapDto>>($"/api/v1/scan-jobs/{s.Job}/gaps");
        Assert.Single(byJob!.Items);
    }

    [Fact]
    public async Task Coverage_counts_partial_shares_and_gap_kinds()
    {
        await Seed("api-inv-4");
        var coverage = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<CoverageDto>("/api/v1/coverage");
        Assert.True(coverage!.SharesPartial >= 1);
        Assert.True(coverage.GapsByKind["AccessDenied"] >= 1);
        Assert.True(coverage.SharesIncluded < coverage.SharesTotal);
    }

    [Fact]
    public async Task Admins_can_exclude_a_share_with_a_reason_and_it_is_audited()
    {
        var s = await Seed("api-inv-5");
        var admin = _factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-1-2-3-1001", @"LAB\admin");

        // A share can't be left out without saying why (D124).
        var refused = await admin.PatchAsJsonAsync($"/api/v1/shares/{s.Hr}", new { included = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/v1/shares/{s.Hr}", new { included = false, reason = " x " })).StatusCode);

        var res = await admin.PatchAsJsonAsync($"/api/v1/shares/{s.Hr}", new { included = false, reason = "Copy of the HR system; scanned at the source" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var share = (await res.Content.ReadFromJsonAsync<ShareDto>())!;
        Assert.False(share.Included);
        Assert.Equal(("Copy of the HR system; scanned at the source", @"LAB\admin"), (share.ExclusionReason, share.ExcludedByName));
        await using var db = _db.CreateContext();
        var audit = await db.AppAuditLog.SingleAsync(a => a.Action == "share.exclude" && a.Target == @"\\api-inv-5\HR");
        Assert.Contains("Copy of the HR system", audit.Details, StringComparison.Ordinal);

        // Including it again needs no reason and clears the old one; the audit log keeps it.
        var back = (await (await admin.PatchAsJsonAsync($"/api/v1/shares/{s.Hr}", new { included = true })).Content.ReadFromJsonAsync<ShareDto>())!;
        Assert.True(back.Included);
        Assert.Null(back.ExclusionReason);
        var included = await db.AppAuditLog.Where(a => a.Action == "share.include" && a.Target == @"\\api-inv-5\HR").Select(a => a.Details).SingleAsync();
        Assert.Contains("Copy of the HR system", included, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrative_shares_can_never_be_included()
    {
        var s = await Seed("api-inv-6");
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PatchAsJsonAsync($"/api/v1/shares/{s.AdminShare}", new { included = true });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    public async Task Only_platform_admins_can_change_share_scope(string role)
    {
        var s = await Seed($"api-inv-7-{role.ToLowerInvariant()}");
        var res = await _factory.ClientAs(role).PatchAsJsonAsync($"/api/v1/shares/{s.Hr}", new { included = false });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/shares")]
    [InlineData("/api/v1/coverage")]
    public async Task Data_owners_cannot_read_the_platform_wide_inventory(string url)
    {
        var res = await _factory.ClientAs(Roles.DataOwner).GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_ids_are_not_found()
    {
        var client = _factory.ClientAs(Roles.PlatformAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/shares/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/shares/{Guid.NewGuid()}/folders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/folders/{Guid.NewGuid()}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/scan-jobs/{Guid.NewGuid()}/gaps")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/v1/shares/{Guid.NewGuid()}", new { included = true })).StatusCode);
    }
}
