using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
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

/// <summary>
/// Scan scope and exclusions (D124) end to end: synthetic shares on disk, exclusions saved through the API, and real
/// scans that must leave the right things out and say so. Every name and number here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class ScanScopeTests : IAsyncLifetime, IDisposable
{
    private const string Ssn = "512-48-3307";
    private const string Host = "fs-scope.lab.test";
    private static readonly string[] SensitiveNames = ["staff.csv", "old.csv", "notes.txt", "payroll.bak"];

    private readonly TestDatabase _db = new();
    private readonly string _lab = Directory.CreateTempSubdirectory("ds-scope-").FullName;
    private ApiFactory _factory = null!;
    private Guid _serverId;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _factory = new ApiFactory(_db);
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        _serverId = Guid.NewGuid();
        db.ManagedServers.Add(new ManagedServer { Id = _serverId, Hostname = Host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        Directory.Delete(_lab, recursive: true);
    }

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-70-80-90-1300", @"LAB\scope-admin");

    private sealed record ImpactDto(long Files, long Restricted, long Confidential, long Folders, bool Large, List<string> Warnings, List<string> Notes);

    private sealed record ExclusionDto(long Id, string Kind, string Effect, string Target, string Reason, bool Enabled, ImpactDto Impact);

    private sealed record CoverageDto(int SharesIncluded, int SharesExcluded, int SpecialShares, int FolderExclusions, int ContentExclusions, int Disabled, long FilesNotRead, int FoldersNotScanned);

    private sealed record ChangeDto(string ActorName, string Action, string Target, string? Reason);

    private sealed record ShareScopeDto(Guid Id, string Name, string Kind, bool Included);

    private sealed record ScopeDto(List<ShareScopeDto> Shares, List<ExclusionDto> Exclusions, CoverageDto Coverage, List<ChangeDto> History);

    private string Write(string relative, string content)
    {
        var path = Path.Join(_lab, Host, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>HR with payroll, a personal folder and a backup; Finance with a plain note; an admin share.</summary>
    private void Lab()
    {
        Write(@"HR\Payroll\staff.csv", $"Name,SSN\nAvery Okafor,{Ssn}\n");
        Write(@"HR\Payroll\2019\old.csv", $"Name,SSN\nJordan Reyes,{Ssn}\n");
        Write(@"HR\Personal\notes.txt", $"SSN {Ssn}");
        Write(@"HR\Archive\payroll.bak", $"Name,SSN\nSam Lee,{Ssn}\n");
        Write(@"HR\welcome.txt", "Welcome to the team.");
        Write(@"Finance\budget.txt", "Budget for next year.");
        Write(@"ADMIN$\readme.txt", "admin");
    }

    private async Task<Guid> Scan()
    {
        await using (var db = _db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = _serverId, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _db.ConnectionString,
            ["Scanner:LeaseSeconds"] = "60",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var executor = new InventoryScanExecutor(sp.GetRequiredService<IServiceScopeFactory>(), new LabShareEnumerator(_lab), new FileSystemDirectorySource(),
            new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero), NullLogger<InventoryScanExecutor>.Instance, new FileContentReader(new ContentLimits()));
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        await using var check = _db.CreateContext();
        var job = await check.ScanJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).FirstAsync(j => j.ServerId == _serverId);
        Assert.Equal(ScanJobState.Completed, job.State);
        return job.Id;
    }

    private async Task<Dictionary<string, InventoryFile>> Files()
    {
        await using var db = _db.CreateContext();
        return await (from f in db.Files join s in db.Shares on f.ShareId equals s.Id where s.ServerId == _serverId select f)
            .AsNoTracking().ToDictionaryAsync(f => f.Name);
    }

    private async Task<Share> ShareNamed(string name)
    {
        await using var db = _db.CreateContext();
        return await db.Shares.AsNoTracking().SingleAsync(s => s.ServerId == _serverId && s.Name == name);
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> Add(object body) => Admin.PostAsJsonAsync("/api/v1/scan-scope/exclusions", body);

    [Fact]
    public async Task Excluded_folders_and_file_types_are_left_out_and_shown_as_not_covered()
    {
        Lab();
        await Scan();
        var before = await Files();
        Assert.All(SensitiveNames, n => Assert.Equal(Sensitivity.Restricted, before[n].Sensitivity));
        var hr = await ShareNamed("HR");
        Assert.Equal(ShareState.Scanned, hr.State);

        // Leaving out a folder with Restricted files needs confirming; the preview says why.
        var unconfirmed = await Add(new { kind = "Folder", effect = "Skip", shareId = hr.Id, path = "/personal/", reason = "Staff personal files; HR asked us not to scan" });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
        var problem = await unconfirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("needsConfirm").GetBoolean());
        Assert.Equal(1, problem.GetProperty("impact").GetProperty("restricted").GetInt64());
        Assert.Contains(problem.GetProperty("impact").GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("1 Restricted file was found here.", StringComparison.Ordinal));
        var personal = await Ok<ExclusionDto>(await Add(new { kind = "Folder", effect = "Skip", shareId = hr.Id, path = "/personal/", reason = "Staff personal files; HR asked us not to scan", confirm = true }));
        Assert.Equal(@"\\fs-scope.lab.test\HR\personal", personal.Target);
        var listOnly = await Ok<ExclusionDto>(await Add(new { kind = "Folder", effect = "ListOnly", shareId = hr.Id, path = @"Payroll\2019", reason = "Closed year, kept for audit", confirm = true }));
        var bak = await Ok<ExclusionDto>(await Add(new { kind = "FileType", extension = "*.BAK", reason = "Nightly copies of the payroll system", confirm = true }));
        Assert.Equal(".bak files on every share", bak.Target);

        // A file added to the excluded folder after the exclusion is never seen.
        Write(@"HR\Personal\later.txt", $"SSN {Ssn}");
        var job = await Scan();
        var after = await Files();
        Assert.False(after.ContainsKey("later.txt"));
        Assert.Equal(Sensitivity.Restricted, after["staff.csv"].Sensitivity);
        foreach (var name in new[] { "old.csv", "payroll.bak" })
        {
            // Listed, never opened, and never counted as clean: no label and no findings from before.
            Assert.Equal((ContentState.Excluded, Sensitivity.Unknown), (after[name].ContentState, after[name].Sensitivity));
            Assert.Contains("excluded by an administrator", after[name].ContentNote, StringComparison.Ordinal);
            await using var db = _db.CreateContext();
            Assert.False(await db.Findings.AnyAsync(f => f.FileId == after[name].Id));
        }
        // Files already found under the skipped folder stay as last seen; the folder itself is not taken for removed.
        Assert.Null(after["notes.txt"].RemovedAt);
        Assert.NotEqual(job, after["notes.txt"].LastSeenJobId);
        hr = await ShareNamed("HR");
        Assert.Equal(ShareState.Partial, hr.State);
        await using (var db = _db.CreateContext())
        {
            var gap = await db.ScanGaps.SingleAsync(g => g.JobId == job && g.Kind == GapKind.Excluded);
            Assert.Equal("Personal", gap.Path);
            Assert.Contains("HR asked us not to scan", gap.Message, StringComparison.Ordinal);
            Assert.False(await db.Folders.AnyAsync(f => f.ShareId == hr.Id && f.Path == "Personal" && f.RemovedAt != null));
            var history = await db.ShareScanHistory.SingleAsync(h => h.JobId == job && h.ShareId == hr.Id);
            Assert.True(history.FilesNotRead >= 2);
        }

        // The screen's numbers and history.
        var scope = await Ok<ScopeDto>(await Admin.GetAsync("/api/v1/scan-scope"));
        Assert.Equal((1, 2, 0), (scope.Coverage.FolderExclusions, scope.Coverage.ContentExclusions, scope.Coverage.Disabled));
        Assert.Equal((2L, 1), (scope.Coverage.FilesNotRead, scope.Coverage.FoldersNotScanned));
        Assert.Equal(1, scope.Coverage.SpecialShares);
        Assert.Equal(3, scope.History.Count(h => h.Action == "scan-exclusion.add"));
        Assert.Contains(scope.History, h => h.Reason == "Closed year, kept for audit" && h.ActorName == @"LAB\scope-admin");

        // Turned off, the folder is scanned again and the share is complete.
        await Ok<ExclusionDto>(await Admin.PutAsJsonAsync($"/api/v1/scan-scope/exclusions/{personal.Id}", new { enabled = false }));
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/scan-scope/exclusions/{listOnly.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/scan-scope/exclusions/{bak.Id}")).StatusCode);
        await Scan();
        var back = await Files();
        Assert.Equal(Sensitivity.Restricted, back["later.txt"].Sensitivity);
        Assert.Equal((ContentState.Scanned, Sensitivity.Restricted), (back["old.csv"].ContentState, back["old.csv"].Sensitivity));
        Assert.Equal(Sensitivity.Restricted, back["payroll.bak"].Sensitivity);
        Assert.Equal(ShareState.Scanned, (await ShareNamed("HR")).State);

        await using (var db = _db.CreateContext())
        {
            var actions = await db.AppAuditLog.Where(a => a.Action.StartsWith("scan-exclusion.")).OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync();
            Assert.Equal(["scan-exclusion.add", "scan-exclusion.add", "scan-exclusion.add", "scan-exclusion.disable", "scan-exclusion.remove", "scan-exclusion.remove"], actions);
        }
    }

    [Fact]
    public async Task Turning_an_exclusion_back_on_that_hides_sensitive_files_needs_confirming_again()
    {
        Lab();
        await Scan();
        var hr = await ShareNamed("HR");
        var x = await Ok<ExclusionDto>(await Add(new { kind = "Folder", effect = "ListOnly", shareId = hr.Id, path = "Payroll", reason = "Payroll is scanned by the payroll vendor", confirm = true }));
        await Ok<ExclusionDto>(await Admin.PutAsJsonAsync($"/api/v1/scan-scope/exclusions/{x.Id}", new { enabled = false }));
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PutAsJsonAsync($"/api/v1/scan-scope/exclusions/{x.Id}", new { enabled = true })).StatusCode);
        var on = await Ok<ExclusionDto>(await Admin.PutAsJsonAsync($"/api/v1/scan-scope/exclusions/{x.Id}", new { enabled = true, confirm = true, reason = "Payroll vendor scans it monthly" }));
        Assert.True(on.Enabled);
        Assert.Equal("Payroll vendor scans it monthly", on.Reason);
        await using var db = _db.CreateContext();
        var change = await db.AppAuditLog.Where(a => a.Action == "scan-exclusion.change").Select(a => a.Details).SingleAsync();
        Assert.Contains("Payroll vendor scans it monthly", change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_harmless_exclusion_needs_no_confirmation_and_says_when_the_folder_was_not_found()
    {
        Lab();
        await Scan();
        var finance = await ShareNamed("Finance");
        var impact = await Ok<ImpactDto>(await Admin.GetAsync($"/api/v1/scan-scope/impact?kind=Folder&effect=Skip&shareId={finance.Id}&path=Temp"));
        Assert.False(impact.Large);
        Assert.Contains(impact.Notes, n => n.Contains("No folder at this path", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Created, (await Add(new { kind = "Folder", effect = "Skip", shareId = finance.Id, path = "Temp", reason = "Scratch space" })).StatusCode);
        // The same folder again, in any case, is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Add(new { kind = "Folder", effect = "Skip", shareId = finance.Id, path = "TEMP", reason = "Again" })).StatusCode);
        // Common document types always need confirming, even with nothing found yet.
        var docx = await Ok<ImpactDto>(await Admin.GetAsync($"/api/v1/scan-scope/impact?kind=FileType&extension=.docx"));
        Assert.True(docx.Large);
        Assert.Equal(HttpStatusCode.Conflict, (await Add(new { kind = "FileType", extension = "docx", reason = "Too many Word files" })).StatusCode);
    }

    [Theory]
    [InlineData("""{"kind":"Folder","effect":"Skip","path":"Temp","reason":"Scratch"}""", "shareId")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{hr}","path":"/","reason":"Everything"}""", "path")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{hr}","path":"Temp\\*","reason":"Wildcards"}""", "path")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{hr}","path":"..\\Finance","reason":"Escape"}""", "path")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{hr}","path":"\\\\fs01\\HR\\Temp","reason":"UNC"}""", "path")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{hr}","path":"Temp","reason":""}""", "reason")]
    [InlineData("""{"kind":"Folder","effect":"Hide","shareId":"{hr}","path":"Temp","reason":"Scratch"}""", "effect")]
    [InlineData("""{"kind":"FileType","effect":"Skip","extension":"tmp","reason":"Temp files"}""", "effect")]
    [InlineData("""{"kind":"FileType","extension":"*.*","reason":"Everything"}""", "extension")]
    [InlineData("""{"kind":"Folder","effect":"Skip","shareId":"{admin}","path":"Temp","reason":"Admin share"}""", "shareId")]
    [InlineData("""{"kind":"Everything","reason":"Nope"}""", "kind")]
    public async Task Bad_exclusions_are_refused(string body, string field)
    {
        Lab();
        await Scan();
        var json = body.Replace("{hr}", (await ShareNamed("HR")).Id.ToString(), StringComparison.Ordinal)
            .Replace("{admin}", (await ShareNamed("ADMIN$")).Id.ToString(), StringComparison.Ordinal);
        var response = await Admin.PostAsync("/api/v1/scan-scope/exclusions", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), await response.Content.ReadAsStringAsync());
        await using var db = _db.CreateContext();
        Assert.False(await db.ScanExclusions.AnyAsync());
    }

    [Theory]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Auditor)]
    public async Task Only_platform_admins_can_change_exclusions(string role)
    {
        Lab();
        await Scan();
        var client = _factory.ClientAs(role);
        var hr = await ShareNamed("HR");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/scan-scope/exclusions", new { kind = "Folder", effect = "Skip", shareId = hr.Id, path = "Temp", reason = "Scratch" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/scan-scope/impact?kind=Folder&effect=Skip&shareId={hr.Id}&path=Temp")).StatusCode);
        var x = await Ok<ExclusionDto>(await Add(new { kind = "Folder", effect = "Skip", shareId = hr.Id, path = "Temp", reason = "Scratch" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/v1/scan-scope/exclusions/{x.Id}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/scan-scope/exclusions/{x.Id}")).StatusCode);
        // Auditors can read the scope and its history; analysts and viewers can't.
        Assert.Equal(role == Roles.Auditor ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/scan-scope")).StatusCode);
        await using var db = _db.CreateContext();
        Assert.True(await db.ScanExclusions.AnyAsync(e => e.Id == x.Id && e.Enabled));
    }

    [Fact]
    public async Task Changes_need_the_anti_forgery_header()
    {
        Lab();
        await Scan();
        var client = _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false);
        var hr = await ShareNamed("HR");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/scan-scope/exclusions", new { kind = "Folder", effect = "Skip", shareId = hr.Id, path = "Temp", reason = "Scratch" })).StatusCode);
    }

    [Fact]
    public async Task An_excluded_share_shows_with_its_reason_and_in_the_history()
    {
        Lab();
        await Scan();
        var finance = await ShareNamed("Finance");
        await Ok<JsonElement>(await Admin.PatchAsJsonAsync($"/api/v1/shares/{finance.Id}", new { included = false, reason = "Moved to the new file server" }));
        var scope = await Ok<ScopeDto>(await Admin.GetAsync("/api/v1/scan-scope"));
        Assert.Equal((1, 1), (scope.Coverage.SharesIncluded, scope.Coverage.SharesExcluded));
        Assert.False(scope.Shares.Single(s => s.Name == "Finance").Included);
        Assert.Contains(scope.History, h => h.Action == "share.exclude" && h.Reason == "Moved to the new file server");
    }
}
