using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DataScope.ActiveDirectory;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Isolation;
using DataScope.Scanner.Security;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>
/// Scans a large synthetic file server through the real scanner, readers, detectors and database, then times the
/// dashboard, the main pages and the reports against what it found. It checks the results are complete and correct
/// at that size, and that each step stays within a generous time budget, so a change that makes scanning or a
/// report much slower fails here. Every name, number and file is made up.
///
/// Excluded from the normal test run. Run it with:
///   dotnet test tests/DataScope.Api.Tests --filter Category=Benchmark
/// DATASCOPE_BENCH_FILES sets the number of files (default 20,000) and DATASCOPE_BENCH_OUT, when set, is where the
/// results are written as a Markdown table (docs/BENCHMARKS.md records them).
/// </summary>
[Trait("Category", "Database")]
[Trait("Category", "Benchmark")]
public sealed class ScaleBenchmark(ITestOutputHelper output) : IDisposable
{
    private const string Host = "bench-fs01";
    private const int FilesPerFolder = 50;
    private static readonly string[] ShareNames = ["Finance", "HR", "Projects", "Shared"];

    private readonly string _root = Directory.CreateTempSubdirectory("ds-bench-").FullName;
    private TestDatabase _database = null!;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record Estate(int Files, int Folders, int Sensitive, int Skipped);

    /// <summary>
    /// Four shares of team folders, each folder holding fifty files: mostly short text documents, one in ten a
    /// video clip (opened only to check it is a video, which has no text to read) and about one in a hundred a staff list with a synthetic Social Security number.
    /// </summary>
    private Estate Build(int fileTarget)
    {
        var folders = Math.Max(1, fileTarget / FilesPerFolder);
        var random = new Random(20261010);
        var filler = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"word{i % 37}"));
        int files = 0, sensitive = 0, skipped = 0;
        for (var k = 0; k < folders; k++)
        {
            var folder = Path.Join(_root, Host, ShareNames[k % ShareNames.Length], $"Team {k / 40:D3}", $"Folder {k % 40:D2}");
            Directory.CreateDirectory(folder);
            for (var n = 0; n < FilesPerFolder; n++, files++)
            {
                if (files % 97 == 7)
                {
                    sensitive++;
                    File.WriteAllText(Path.Join(folder, $"staff {n:D2}.csv"),
                        $"Name,SSN,Start\nPat Example,512-48-{1000 + files % 9000:D4},2024-01-0{1 + n % 9}\n");
                }
                else if (files % 10 == 3)
                {
                    skipped++;
                    File.WriteAllBytes(Path.Join(folder, $"clip {n:D2}.mp4"), [0, 0, 0, 24, .. "ftypisom"u8, 0, 0, 2, 0]);
                }
                else
                {
                    File.WriteAllText(Path.Join(folder, $"notes {n:D2}.txt"), filler[..(400 + random.Next(1200))]);
                }
            }
        }
        // Permissions: the Shared share is open to everyone; each team folder elsewhere adds its team's group, and the
        // rest inherit an administrators-only default.
        object Ace(string sid, string rights) => new { sid, rights, deny = false, applies = "all" };
        var folderAcls = new Dictionary<string, object>
        {
            ["Shared"] = new { aces = new[] { Ace(WellKnownSid.Administrators, "Full"), Ace(WellKnownSid.Everyone, "Modify") } },
            ["HR"] = new { aces = new[] { Ace(WellKnownSid.Administrators, "Full"), Ace(WellKnownSid.Users, "Read") } },
        };
        foreach (var (share, team) in Enumerable.Range(0, folders).Select(k => (ShareNames[k % ShareNames.Length], k / 40)).Distinct().Where(t => t.Item1 != "Shared"))
        {
            folderAcls[$"{share}/Team {team:D3}"] = new { aces = new[] { Ace(TeamGroup(team), "Modify") } };
        }
        var security = new
        {
            localGroups = Array.Empty<object>(),
            shares = ShareNames.ToDictionary(s => s, _ => (object)new[] { Ace(WellKnownSid.Everyone, "Full") }),
            folders = folderAcls,
            denied = Array.Empty<string>(),
        };
        File.WriteAllText(Path.Join(_root, Host + ".security.json"), JsonSerializer.Serialize(security));
        // Every folder listed: the share roots, the team folders and the leaf folders.
        var teams = Enumerable.Range(0, folders).Select(k => (k % ShareNames.Length, k / 40)).Distinct().Count();
        return new Estate(files, folders + teams + ShareNames.Length, sensitive, skipped);
    }

    private const string Domain = "S-1-5-21-70-80-90";

    private static string TeamGroup(int team) => $"{Domain}-{20_000 + team}";

    private static string User(int i) => $"{Domain}-{1_000 + i}";

    /// <summary>
    /// A synthetic domain: <paramref name="users"/> users in Domain Users, each in one team group and one in ten also in
    /// a second through a nested "all teams" group, so the exposure map has real group membership to work through.
    /// </summary>
    private string WriteDirectory(int users, int teams)
    {
        object P(string sid, string kind, string name, string[]? members = null) => new { sid, kind, name, enabled = true, security = true, members = members ?? [] };
        var principals = new List<object> { P($"{Domain}-513", "Group", "Domain Users", Enumerable.Range(0, users).Select(User).ToArray()) };
        principals.AddRange(Enumerable.Range(0, users).Select(i => P(User(i), "User", $"user{i:D5}")));
        principals.AddRange(Enumerable.Range(0, teams).Select(t => P(TeamGroup(t), "Group", $"GG_Team_{t:D3}",
            Enumerable.Range(0, users).Where(i => i % teams == t).Select(User).Append(t == 0 ? $"{Domain}-19999" : null).OfType<string>().ToArray())));
        principals.Add(P($"{Domain}-19999", "Group", "GG_All_Teams", Enumerable.Range(0, users).Where(i => i % 10 == 0).Select(User).ToArray()));
        var path = Path.Join(_root, "bench.directory.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { domain = "bench.test", domainSid = Domain, principals }));
        return path;
    }

    [Fact]
    public async Task Scans_and_reports_on_a_large_estate_within_budget()
    {
        var fileTarget = int.TryParse(Environment.GetEnvironmentVariable("DATASCOPE_BENCH_FILES"), CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 20_000;
        _database = TestDatabase.Pooled();
        await _database.InitializeAsync();
        try
        {
            await RunAsync(fileTarget);
        }
        finally
        {
            await _database.DisposeAsync();
        }
    }

    private async Task RunAsync(int fileTarget)
    {
        var timer = Stopwatch.StartNew();
        var estate = Build(fileTarget);
        var buildTime = timer.Elapsed;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "300",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        Guid serverId;
        await using (var db = _database.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = Host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            db.ManagedServers.Add(server);
            await db.SaveChangesAsync();
            serverId = server.Id;
        }

        // The production defaults, so the benchmark measures what a customer gets: files read in a separate reader process.
        var limits = ScanLimits.From(new ConfigurationBuilder().Build());
        using var reader = new IsolatedContentReader(new ContentLimits(), () => ContentHostCommand.For(typeof(InventoryScanExecutor).Assembly.Location));
        async Task<TimeSpan> Scan(bool readContents)
        {
            await using (var db = _database.CreateContext())
            {
                var now = DateTimeOffset.UtcNow;
                db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = serverId, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync();
            }
            var executor = new InventoryScanExecutor(scopes, new LabShareEnumerator(_root), new FileSystemDirectorySource(), limits,
                NullLogger<InventoryScanExecutor>.Instance, readContents ? reader : null, new LabSecurityReader(_root));
            var started = Stopwatch.StartNew();
            Assert.True(await new ScanWorker(scopes, executor, config, NullLogger<ScanWorker>.Instance).RunOneAsync(CancellationToken.None));
            return started.Elapsed;
        }

        // 1. Walk only: names, sizes, dates and permissions of every folder and file.
        var walk = await Scan(readContents: false);
        // 2. A full scan: walks again (nothing changed) and reads every file the walk found.
        var full = await Scan(readContents: true);
        // 3. A rescan with nothing changed: the nightly case.
        var rescan = await Scan(readContents: true);

        // A directory to compute access through, as a nightly sync would leave it.
        var users = int.TryParse(Environment.GetEnvironmentVariable("DATASCOPE_BENCH_USERS"), CultureInfo.InvariantCulture, out var u) && u > 0 ? u : 2_000;
        var teamCount = Enumerable.Range(0, Math.Max(1, fileTarget / FilesPerFolder)).Select(k => k / 40).Distinct().Count();
        var directoryFile = WriteDirectory(users, teamCount);
        Assert.True(await new DirectorySyncWorker(scopes, config, NullLogger<DirectorySyncWorker>.Instance, new LabDirectoryReader(directoryFile)).RunOneAsync(CancellationToken.None));

        long dbBytes;
        await using (var db = _database.CreateContext())
        {
            var jobs = await db.ScanJobs.AsNoTracking().Where(j => j.ServerId == serverId).ToListAsync();
            Assert.All(jobs, j => Assert.Equal(Core.ScanJobs.ScanJobState.Completed, j.State));
            var files = await (from f in db.Files join s in db.Shares on f.ShareId equals s.Id where s.ServerId == serverId && f.RemovedAt == null select f).AsNoTracking()
                .GroupBy(f => new { f.Sensitivity, f.ContentState }).Select(g => new { g.Key.Sensitivity, g.Key.ContentState, Count = g.Count() }).ToListAsync();
            Assert.Equal(estate.Files, files.Sum(f => f.Count));
            Assert.Equal(estate.Sensitive, files.Where(f => f.Sensitivity == Sensitivity.Restricted).Sum(f => f.Count));
            Assert.Equal(estate.Skipped, files.Where(f => f.ContentState == ContentState.Unsupported).Sum(f => f.Count));
            Assert.Equal(estate.Files - estate.Skipped, files.Where(f => f.ContentState == ContentState.Scanned).Sum(f => f.Count));
            var folders = await (from d in db.Folders join s in db.Shares on d.ShareId equals s.Id where s.ServerId == serverId && d.RemovedAt == null select d).CountAsync();
            Assert.Equal(estate.Folders, folders);
            Assert.Empty(await db.ScanGaps.AsNoTracking().Where(g => jobs.Select(j => j.Id).Contains(g.JobId)).ToListAsync());
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();
            await using var size = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection);
            dbBytes = (long)(await size.ExecuteScalarAsync())!;
        }

        // Pages and reports, each asked three times; the median is reported and every call must finish in budget.
        await using var factory = new ApiFactory(_database);
        using var admin = factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-70-80-90-500", @"BENCH\admin");
        var pages = new (string Name, string Url)[]
        {
            ("Dashboard", "/api/v1/dashboard"),
            ("Coverage", "/api/v1/coverage"),
            ("Sensitive data summary", "/api/v1/classification/summary"),
            ("Sensitive files, first page", "/api/v1/classification/files?minSensitivity=Confidential&limit=50"),
            ("Exposure, first page", "/api/v1/access/exposure?limit=50"),
            ("Exposure map", "/api/v1/exposure-map"),
            ("Exposure map, one person", "/api/v1/exposure-map/people/S-1-5-21-70-80-90-1010"),
            ("Report: sensitive data (CSV)", "/api/v1/reports/sensitive-data?format=csv"),
            ("Report: risky permissions (CSV)", "/api/v1/reports/risky-permissions?format=csv"),
            ("Report: group access (CSV)", "/api/v1/reports/group-access?format=csv"),
            ("Report: shares and owners (HTML)", "/api/v1/reports/shares-owners?format=html"),
            ("Report: scan coverage (CSV)", "/api/v1/reports/scan-coverage?format=csv"),
        };
        var pageBudget = TimeSpan.FromSeconds(Math.Max(10, estate.Files / 5_000.0));
        var pageTimes = new List<(string Name, TimeSpan Median, long Bytes)>();
        foreach (var (name, url) in pages)
        {
            var runs = new List<TimeSpan>();
            long bytes = 0;
            for (var i = 0; i < 3; i++)
            {
                var started = Stopwatch.StartNew();
                using var response = await admin.GetAsync(url);
                var body = await response.Content.ReadAsByteArrayAsync();
                runs.Add(started.Elapsed);
                Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode} {Encoding.UTF8.GetString(body)}");
                bytes = body.Length;
            }
            Assert.True(runs.Max() < pageBudget, $"{name} took {runs.Max().TotalSeconds:F1} s; the budget is {pageBudget.TotalSeconds:F0} s.");
            pageTimes.Add((name, runs.Order().ElementAt(1), bytes));
        }

        // The Shared share is open to everyone, so its sensitive files must show as exposed; the rest are not.
        using (var dashboard = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/dashboard")))
        {
            var exposure = dashboard.RootElement.GetProperty("exposure");
            Assert.True(exposure.GetProperty("open").GetInt32() > 0, exposure.ToString());
            Assert.Equal(0, exposure.GetProperty("unknown").GetInt32());
        }

        // Everyone can read the Shared share's sensitive files, so every synthetic user is on the map.
        using (var map = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/exposure-map")))
        {
            var summary = map.RootElement.GetProperty("summary");
            Assert.Equal(users, summary.GetProperty("people").GetInt32());
            Assert.True(summary.GetProperty("groups").GetInt32() > 1, summary.ToString());
        }

        var classify = full - walk;
        var results = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        results.AppendLine(c, $"Synthetic estate: {estate.Files:N0} files in {estate.Folders:N0} folders across {ShareNames.Length} shares " +
            $"({estate.Sensitive:N0} with a synthetic SSN, {estate.Skipped:N0} of a type that is not read); written to disk in {buildTime.TotalSeconds:F1} s. " +
            $"Directory: {users:N0} users in {teamCount:N0} team groups.");
        results.AppendLine(c, $"Machine: {Environment.ProcessorCount} logical processors, {RuntimeInformation.OSDescription}, .NET {Environment.Version}; " +
            $"scanner, API and PostgreSQL on the same machine; files on local disk.");
        results.AppendLine();
        results.AppendLine("| Step | Time | Rate |");
        results.AppendLine("|---|---:|---:|");
        results.AppendLine(c, $"| Walk (inventory and permissions) | {walk.TotalSeconds:F1} s | {estate.Files / walk.TotalSeconds:N0} files/s, {estate.Folders / walk.TotalSeconds:N0} folders/s |");
        results.AppendLine(c, $"| Content read and classify | {classify.TotalSeconds:F1} s | {(estate.Files - estate.Skipped) / Math.Max(0.001, classify.TotalSeconds):N0} files read/s |");
        results.AppendLine(c, $"| First full scan (walk and classify) | {full.TotalSeconds:F1} s | {estate.Files / full.TotalSeconds:N0} files/s |");
        results.AppendLine(c, $"| Rescan, nothing changed | {rescan.TotalSeconds:F1} s | {estate.Files / rescan.TotalSeconds:N0} files/s |");
        results.AppendLine();
        results.AppendLine(c, $"Database size after three scans: {dbBytes / 1024.0 / 1024.0:N0} MB ({dbBytes / (double)estate.Files / 1024:N1} KB per file, including history).");
        results.AppendLine();
        results.AppendLine("| Page or report | Median of 3 | Response size |");
        results.AppendLine("|---|---:|---:|");
        foreach (var (name, median, bytes) in pageTimes)
        {
            results.AppendLine(c, $"| {name} | {median.TotalMilliseconds:N0} ms | {bytes / 1024.0:N0} KB |");
        }
        output.WriteLine(results.ToString());
        if (Environment.GetEnvironmentVariable("DATASCOPE_BENCH_OUT") is { Length: > 0 } outPath)
        {
            await File.WriteAllTextAsync(outPath, results.ToString());
        }

        // Generous floors, about a tenth of what a 4-processor build machine manages, so only a real regression trips them.
        Assert.True(estate.Files / walk.TotalSeconds > 500, $"The walk managed only {estate.Files / walk.TotalSeconds:N0} files a second.");
        Assert.True(estate.Files / full.TotalSeconds > 300, $"The full scan managed only {estate.Files / full.TotalSeconds:N0} files a second.");
        Assert.True(rescan < full * 1.5, "A rescan with nothing changed took much longer than the first full scan.");
    }
}
