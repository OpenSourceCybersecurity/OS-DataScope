using System.Security.Cryptography;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>End-to-end scans of synthetic folder trees through the real worker, executor and database.</summary>
[Trait("Category", "Database")]
public sealed class InventoryScanTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _database;
    private readonly string _lab = Directory.CreateTempSubdirectory("ds-lab-").FullName;

    public InventoryScanTests(TestDatabase database) => _database = database;

    public void Dispose() => Directory.Delete(_lab, recursive: true);

    private static readonly ScanLimits Defaults = new(MaxDepth: 64, MaxEntriesPerFolder: 10_000, MaxEntriesPerJob: 1_000_000, ReportInterval: TimeSpan.Zero);

    // ---- lab helpers ----

    private string Share(string host, string share)
    {
        var path = Path.Join(_lab, host, share);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void FileAt(string root, string relative, string content = "synthetic")
    {
        var path = Path.Join(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A small Northwind-style server: HR (nested), Finance, Public (empty) and an admin share.</summary>
    private void Northwind(string host)
    {
        var hr = Share(host, "HR");
        FileAt(hr, @"Policies\handbook.docx");
        FileAt(hr, @"Payroll\2024\march.xlsx", "0123456789");
        FileAt(hr, @"Payroll\2024\april.xlsx");
        FileAt(hr, "org-chart.pdf");
        var finance = Share(host, "Finance");
        FileAt(finance, @"Invoices\inv-001.csv");
        Share(host, "Public");
        FileAt(Share(host, "C$"), @"Windows\system.ini");
    }

    // ---- job helpers ----

    private async Task<(Guid ServerId, Guid JobId)> Queue(string host, Guid? serverId = null)
    {
        await using var db = _database.CreateContext();
        // Leftover queued jobs from other tests would be claimed first.
        await db.ScanJobs.Where(j => j.State == ScanJobState.Queued).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Cancelled));
        var now = DateTimeOffset.UtcNow;
        if (serverId is null)
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            db.ManagedServers.Add(server);
            serverId = server.Id;
        }
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = serverId.Value, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now };
        db.ScanJobs.Add(job);
        await db.SaveChangesAsync();
        return (serverId.Value, job.Id);
    }

    private async Task<ScanJob> Run(Guid jobId, IDirectorySource? source = null, ScanLimits? limits = null, IShareEnumerator? shares = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "60",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var executor = new InventoryScanExecutor(sp.GetRequiredService<IServiceScopeFactory>(), shares ?? new LabShareEnumerator(_lab),
            source ?? new FileSystemDirectorySource(), limits ?? Defaults, NullLogger<InventoryScanExecutor>.Instance);
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        await using var db = _database.CreateContext();
        return await db.ScanJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<Dictionary<string, Share>> Shares(Guid serverId)
    {
        await using var db = _database.CreateContext();
        return await db.Shares.AsNoTracking().Where(s => s.ServerId == serverId).ToDictionaryAsync(s => s.Name);
    }

    private async Task<List<ScanGap>> Gaps(Guid jobId)
    {
        await using var db = _database.CreateContext();
        return await db.ScanGaps.AsNoTracking().Where(g => g.JobId == jobId).OrderBy(g => g.Id).ToListAsync();
    }

    private async Task<List<string>> LiveFiles(Guid shareId)
    {
        await using var db = _database.CreateContext();
        // Sort in .NET with ordinal comparison: database ordering depends on the server's collation.
        var paths = await (from f in db.Files
                           join d in db.Folders on f.FolderId equals d.Id
                           where f.ShareId == shareId && f.RemovedAt == null
                           select d.Path == "" ? f.Name : d.Path + "\\" + f.Name).ToListAsync();
        return paths.Order(StringComparer.Ordinal).ToList();
    }

    // ---- tests ----

    [Fact]
    public async Task Full_scan_inventories_disk_shares_and_skips_admin_shares()
    {
        Northwind("inv-full");
        var (serverId, jobId) = await Queue("inv-full");

        var job = await Run(jobId);

        Assert.Equal(ScanJobState.Completed, job.State);
        var shares = await Shares(serverId);
        Assert.Equal(["C$", "Finance", "HR", "Public"], shares.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ShareKind.Special, shares["C$"].Kind);
        Assert.False(shares["C$"].Included);
        Assert.Equal(ShareState.NotScanned, shares["C$"].State);

        var hr = shares["HR"];
        Assert.Equal(ShareState.Scanned, hr.State);
        Assert.Equal(4, hr.FileCount);
        Assert.Equal(4, hr.FolderCount); // the root, Policies, Payroll and Payroll\2024
        Assert.Equal(@"\\inv-full\HR", hr.UncPath);
        Assert.Equal(0, hr.GapCount);
        Assert.Equal(["Payroll\\2024\\april.xlsx", "Payroll\\2024\\march.xlsx", "Policies\\handbook.docx", "org-chart.pdf"], await LiveFiles(hr.Id));
        Assert.Equal(ShareState.Scanned, shares["Public"].State);
        Assert.Equal(0, shares["Public"].FileCount);
        Assert.Empty(await Gaps(jobId));

        await using var db = _database.CreateContext();
        var server = await db.ManagedServers.SingleAsync(s => s.Id == serverId);
        Assert.Equal(EnrollmentState.Enrolled, server.State);
        Assert.NotNull(server.LastSeenAt);
        Assert.Equal(0, await db.ScanFrontier.CountAsync(f => f.JobId == jobId));
        Assert.Equal(job.ItemsTotal, job.ItemsDone);
    }

    [Fact]
    public async Task Scan_does_not_modify_source_files()
    {
        Northwind("inv-readonly");
        var hr = Path.Join(_lab, "inv-readonly", "HR");
        var before = Snapshot(hr);
        var (_, jobId) = await Queue("inv-readonly");

        await Run(jobId);

        Assert.Equal(before, Snapshot(hr));
    }

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => $"{p}|{File.GetLastWriteTimeUtc(p):O}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}")
            .ToList();

    [Fact]
    public async Task Symbolic_link_loops_are_not_followed_and_are_reported()
    {
        var hr = Share("inv-loop", "HR");
        FileAt(hr, @"Team\notes.txt");
        Directory.CreateSymbolicLink(Path.Join(hr, "Team", "back-to-root"), hr);
        var (serverId, jobId) = await Queue("inv-loop");

        var job = await Run(jobId);

        Assert.Equal(ScanJobState.Completed, job.State);
        var gap = Assert.Single(await Gaps(jobId));
        Assert.Equal(GapKind.ReparsePointSkipped, gap.Kind);
        Assert.Equal(@"Team\back-to-root", gap.Path);
        Assert.Equal(ShareState.Partial, (await Shares(serverId))["HR"].State);
    }

    [Fact]
    public async Task Access_denied_folders_are_gaps_and_the_rest_is_still_scanned()
    {
        var hr = Share("inv-denied", "HR");
        FileAt(hr, @"Open\a.txt");
        FileAt(hr, @"Restricted\secret.txt");
        var (serverId, jobId) = await Queue("inv-denied");

        var job = await Run(jobId, new DenyingSource(@"Restricted"));

        Assert.Equal(ScanJobState.Completed, job.State);
        var gap = Assert.Single(await Gaps(jobId));
        Assert.Equal(GapKind.AccessDenied, gap.Kind);
        Assert.Equal("Restricted", gap.Path);
        var share = (await Shares(serverId))["HR"];
        Assert.Equal(ShareState.Partial, share.State);
        Assert.Equal(["Open\\a.txt"], await LiveFiles(share.Id));

        await using var db = _database.CreateContext();
        var restricted = await db.Folders.SingleAsync(f => f.ShareId == share.Id && f.Path == "Restricted");
        Assert.Null(restricted.LastWalkedJobId);
    }

    [Fact]
    public async Task Unreadable_share_root_marks_the_share_unavailable()
    {
        FileAt(Share("inv-noroot", "HR"), "a.txt");
        FileAt(Share("inv-noroot", "Finance"), "b.txt");
        var (serverId, jobId) = await Queue("inv-noroot");

        await Run(jobId, new DenyingSource("", shareSuffix: "HR"));

        var shares = await Shares(serverId);
        Assert.Equal(ShareState.Unavailable, shares["HR"].State);
        Assert.Equal(ShareState.Scanned, shares["Finance"].State);
        Assert.Equal(GapKind.ShareUnavailable, Assert.Single(await Gaps(jobId)).Kind);
    }

    [Fact]
    public async Task Rescan_marks_deleted_items_removed_and_picks_up_new_ones()
    {
        var hr = Share("inv-rescan", "HR");
        FileAt(hr, "keep.txt");
        FileAt(hr, "delete-me.txt");
        FileAt(hr, @"OldProject\plan.docx");
        var (serverId, first) = await Queue("inv-rescan");
        await Run(first);

        File.Delete(Path.Join(hr, "delete-me.txt"));
        Directory.Delete(Path.Join(hr, "OldProject"), recursive: true);
        FileAt(hr, "new.txt");
        var (_, second) = await Queue("inv-rescan", serverId);
        await Run(second);

        var share = (await Shares(serverId))["HR"];
        Assert.Equal(["keep.txt", "new.txt"], await LiveFiles(share.Id));
        Assert.Equal(2, share.FileCount);
        await using var db = _database.CreateContext();
        Assert.NotNull((await db.Folders.SingleAsync(f => f.ShareId == share.Id && f.Path == "OldProject")).RemovedAt);
        Assert.NotNull((await db.Files.SingleAsync(f => f.ShareId == share.Id && f.Name == "plan.docx")).RemovedAt);
    }

    [Fact]
    public async Task Files_behind_a_new_access_denial_are_not_reported_as_deleted()
    {
        var hr = Share("inv-nofalse", "HR");
        FileAt(hr, @"Payroll\march.xlsx");
        var (serverId, first) = await Queue("inv-nofalse");
        await Run(first);

        var (_, second) = await Queue("inv-nofalse", serverId);
        await Run(second, new DenyingSource("Payroll"));

        var share = (await Shares(serverId))["HR"];
        Assert.Equal(ShareState.Partial, share.State);
        Assert.Equal(["Payroll\\march.xlsx"], await LiveFiles(share.Id));
    }

    [Fact]
    public async Task Depth_limit_stops_descending_and_reports_it()
    {
        FileAt(Share("inv-deep", "HR"), @"a\b\c\d\deep.txt");
        var (serverId, jobId) = await Queue("inv-deep");

        await Run(jobId, limits: Defaults with { MaxDepth = 2 });

        var gap = Assert.Single(await Gaps(jobId));
        Assert.Equal(GapKind.DepthLimit, gap.Kind);
        Assert.Equal(@"a\b", gap.Path);
        Assert.Empty(await LiveFiles((await Shares(serverId))["HR"].Id));
    }

    [Fact]
    public async Task Job_entry_limit_ends_the_scan_with_a_gap_instead_of_running_forever()
    {
        var hr = Share("inv-limit", "HR");
        for (var i = 0; i < 6; i++)
        {
            FileAt(hr, $@"folder{i}\file.txt");
        }
        var (serverId, jobId) = await Queue("inv-limit");

        var job = await Run(jobId, limits: Defaults with { MaxEntriesPerJob = 5 });

        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Contains(await Gaps(jobId), g => g.Kind == GapKind.JobEntryLimit);
        Assert.Equal(ShareState.Partial, (await Shares(serverId))["HR"].State);
        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.ScanFrontier.CountAsync(f => f.JobId == jobId));
    }

    [Fact]
    public async Task Pause_keeps_the_frontier_and_resume_finishes_without_losing_or_duplicating_work()
    {
        var hr = Share("inv-pause", "HR");
        for (var i = 0; i < 5; i++)
        {
            FileAt(hr, $@"dept{i}\sub\file{i}.txt");
        }
        var (serverId, jobId) = await Queue("inv-pause");

        var paused = await Run(jobId, new PauseAfter(_database, jobId, listings: 3));
        Assert.Equal(ScanJobState.Paused, paused.State);
        await using (var db = _database.CreateContext())
        {
            Assert.True(await db.ScanFrontier.CountAsync(f => f.JobId == jobId) > 0);
            await db.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Queued));
        }

        var resumed = await Run(jobId);

        Assert.Equal(ScanJobState.Completed, resumed.State);
        var share = (await Shares(serverId))["HR"];
        Assert.Equal(ShareState.Scanned, share.State);
        Assert.Equal(5, share.FileCount);
        Assert.Equal(11, share.FolderCount);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"dept{i}\\sub\\file{i}.txt"), await LiveFiles(share.Id));
    }

    [Fact]
    public async Task Unreachable_server_is_marked_in_error_and_the_job_is_retried()
    {
        var (serverId, jobId) = await Queue("inv-offline");

        var job = await Run(jobId);

        Assert.Equal(ScanJobState.Queued, job.State);
        Assert.Contains("could not be reached", job.LastError, StringComparison.Ordinal);
        await using var db = _database.CreateContext();
        var server = await db.ManagedServers.SingleAsync(s => s.Id == serverId);
        Assert.Equal(EnrollmentState.Error, server.State);
        await db.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Cancelled));
    }

    [Fact]
    public async Task Database_outage_mid_scan_keeps_the_last_good_results_and_the_retried_job_finishes_the_scan()
    {
        // A16: PostgreSQL goes away in the middle of a rescan, then comes back.
        var hr = Share("inv-outage", "HR");
        for (var i = 0; i < 6; i++)
        {
            FileAt(hr, $@"dept{i}\sub\file{i}.txt");
        }
        var (serverId, first) = await Queue("inv-outage");
        Assert.Equal(ScanJobState.Completed, (await Run(first)).State);
        var before = (await Shares(serverId))["HR"];
        var filesBefore = await LiveFiles(before.Id);
        Assert.Equal(6, filesBefore.Count);

        // Between scans one file is deleted and one is added.
        File.Delete(Path.Join(hr, "dept0", "sub", "file0.txt"));
        FileAt(hr, @"dept5\sub\added.txt");
        var (_, second) = await Queue("inv-outage", serverId);

        var outage = new OutageAfter(_database, listings: 4);
        Exception? failure;
        try
        {
            failure = await Record.ExceptionAsync(() => Run(second, outage));
        }
        finally
        {
            _database.BringOnline();
        }

        // The worker sees a database outage, which its service loop backs off from and retries, and records nothing as done.
        Assert.True(outage.Happened);
        Assert.NotNull(failure);
        Assert.True(DatabaseOutage.Is(failure), $"Expected a database outage, got {failure}");
        await using (var db = _database.CreateContext())
        {
            var job = await db.ScanJobs.AsNoTracking().SingleAsync(j => j.Id == second);
            Assert.Equal(ScanJobState.Running, job.State);
            Assert.Null(job.FinishedAt);
            Assert.NotNull(job.LeaseOwner);
        }
        var during = (await Shares(serverId))["HR"];
        Assert.Equal(ShareState.Scanned, during.State);
        Assert.Equal(first, during.LastScanJobId);
        Assert.Equal(before.FileCount, during.FileCount);
        Assert.Equal(before.FolderCount, during.FolderCount);
        // The half-finished scan never saw the end of the share, so nothing it didn't reach is marked deleted.
        Assert.Contains(@"dept0\sub\file0.txt", await LiveFiles(during.Id));

        // The lease runs out (the worker that held it is gone) and the job is claimed again from its checkpoint.
        await using (var db = _database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == second)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var resumed = await Run(second);

        Assert.Equal(ScanJobState.Completed, resumed.State);
        Assert.Equal(2, resumed.Attempts);
        var after = (await Shares(serverId))["HR"];
        Assert.Equal(ShareState.Scanned, after.State);
        Assert.Equal(second, after.LastScanJobId);
        Assert.Equal(6, after.FileCount);
        var expected = Enumerable.Range(1, 5).Select(i => $@"dept{i}\sub\file{i}.txt").Append(@"dept5\sub\added.txt").Order(StringComparer.Ordinal);
        Assert.Equal(expected, await LiveFiles(after.Id));
        await using (var db = _database.CreateContext())
        {
            // Exactly one live row per file and folder: the retry neither lost nor duplicated work.
            Assert.Equal(6, await db.Files.CountAsync(f => f.ShareId == after.Id && f.RemovedAt == null));
            Assert.Equal(after.FolderCount, await db.Folders.CountAsync(f => f.ShareId == after.Id && f.RemovedAt == null));
        }
    }

    [Fact]
    public async Task The_scanner_service_keeps_running_through_a_database_outage_and_finishes_the_scan_by_itself()
    {
        var hr = Share("inv-outage-service", "HR");
        for (var i = 0; i < 4; i++)
        {
            FileAt(hr, $@"dept{i}\file{i}.txt");
        }
        var (serverId, jobId) = await Queue("inv-outage-service");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "2",
            ["Scanner:PollSeconds"] = "1",
            ["Scanner:Schedules"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var outage = new OutageAfter(_database, listings: 2);
        var executor = new InventoryScanExecutor(sp.GetRequiredService<IServiceScopeFactory>(), new LabShareEnumerator(_lab), outage, Defaults,
            NullLogger<InventoryScanExecutor>.Instance);
        using var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);

        ScanJob? job = null;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!outage.Happened && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.True(outage.Happened);
            // PostgreSQL stays away for a few of the service's retries.
            await Task.Delay(TimeSpan.FromSeconds(3));
            _database.BringOnline();
            while (DateTime.UtcNow < deadline)
            {
                await using var db = _database.CreateContext();
                job = await db.ScanJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
                if (job.State is ScanJobState.Completed or ScanJobState.Failed)
                {
                    break;
                }
                await Task.Delay(250);
            }
            Assert.False(worker.ExecuteTask!.IsCompleted, $"The scanner service stopped: {worker.ExecuteTask.Exception}");
        }
        finally
        {
            _database.BringOnline();
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(ScanJobState.Completed, job?.State);
        var share = (await Shares(serverId))["HR"];
        Assert.Equal(jobId, share.LastScanJobId);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => $@"dept{i}\file{i}.txt"), await LiveFiles(share.Id));
    }

    [Fact]
    public async Task Names_differing_only_by_case_are_recorded_once_with_a_gap()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // NTFS folders are case-insensitive by default, so this cannot be set up there.
        }
        var hr = Share("inv-case", "HR");
        FileAt(hr, "Report.txt");
        FileAt(hr, "report.txt");
        var (serverId, jobId) = await Queue("inv-case");

        await Run(jobId);

        Assert.Single(await LiveFiles((await Shares(serverId))["HR"].Id));
        Assert.Equal(GapKind.UnrepresentableName, Assert.Single(await Gaps(jobId)).Kind);
    }

    [Fact]
    public async Task Excluded_shares_are_not_scanned()
    {
        Northwind("inv-exclude");
        var (serverId, first) = await Queue("inv-exclude");
        await Run(first);
        await using (var db = _database.CreateContext())
        {
            await db.Shares.Where(s => s.ServerId == serverId && s.Name == "Finance").ExecuteUpdateAsync(s => s.SetProperty(x => x.Included, false));
        }

        var (_, second) = await Queue("inv-exclude", serverId);
        await Run(second);

        var shares = await Shares(serverId);
        Assert.Equal(second, shares["HR"].LastScanJobId);
        Assert.Equal(first, shares["Finance"].LastScanJobId);
    }

    /// <summary>Simulates NTFS refusing the scan identity on one folder.</summary>
    private sealed class DenyingSource(string deniedRelativePath, string? shareSuffix = null) : IDirectorySource
    {
        private readonly FileSystemDirectorySource _inner = new();

        public DirectoryListing List(string fullPath, int maxEntries, CancellationToken ct)
        {
            var denied = deniedRelativePath.Length == 0
                ? fullPath.EndsWith(Path.DirectorySeparatorChar + (shareSuffix ?? ""), StringComparison.Ordinal)
                : fullPath.EndsWith(Path.DirectorySeparatorChar + deniedRelativePath.Replace('\\', Path.DirectorySeparatorChar), StringComparison.Ordinal);
            return denied ? DirectoryListing.Failed(GapKind.AccessDenied, "Access denied to the scan identity.") : _inner.List(fullPath, maxEntries, ct);
        }
    }

    /// <summary>Asks for a pause through the database, like the API does, after a number of folder listings.</summary>
    private sealed class PauseAfter(TestDatabase database, Guid jobId, int listings) : IDirectorySource
    {
        private readonly FileSystemDirectorySource _inner = new();
        private int _count;

        public DirectoryListing List(string fullPath, int maxEntries, CancellationToken ct)
        {
            if (++_count == listings)
            {
                using var db = database.CreateContext();
                db.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdate(s => s.SetProperty(j => j.RequestedState, ScanJobState.Paused));
            }
            return _inner.List(fullPath, maxEntries, ct);
        }
    }

    /// <summary>Takes the database offline, like a stopped PostgreSQL service, after a number of folder listings.</summary>
    private sealed class OutageAfter(TestDatabase database, int listings) : IDirectorySource
    {
        private readonly FileSystemDirectorySource _inner = new();
        private int _count;

        public bool Happened { get; private set; }

        public DirectoryListing List(string fullPath, int maxEntries, CancellationToken ct)
        {
            if (++_count == listings)
            {
                database.TakeOffline();
                Happened = true;
            }
            return _inner.List(fullPath, maxEntries, ct);
        }
    }
}
