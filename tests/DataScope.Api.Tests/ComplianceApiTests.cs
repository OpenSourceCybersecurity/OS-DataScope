using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// Compliance standards (D126). Standards are settings for the whole installation, so each test sets the ones it relies
/// on and seeds its own server; counts for the whole installation are checked in <see cref="ComplianceCountTests"/>.
/// </summary>
[Trait("Category", "Database")]
public sealed class ComplianceApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ComplianceApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    internal sealed record Requirement(string Ref, string Text, string Helps);
    internal sealed record Counts(long Files, long Restricted, long Confidential, long Folders, long Shares);
    internal sealed record Standard(string Id, string Name, string Summary, bool Enabled, List<string> Categories, List<string> DefaultCategories, string MinConfidence,
        string? Reason, DateTimeOffset? UpdatedAt, string? UpdatedByName, List<string> NotDetected, List<Requirement> Requirements, Counts? Counts);
    internal sealed record StandardsDto(List<Standard> Standards, List<string> Categories, long FilesNotRead);
    private sealed record Change(DateTimeOffset At, string ActorName, string Action, string Target, string? Reason);
    private sealed record FileRow(Guid Id, string Name, string Sensitivity, List<string> Standards);
    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);
    private sealed record Summary(long Files, Dictionary<string, long> BySensitivity);
    private sealed record Problem(string? Title, Dictionary<string, string[]>? Errors);

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin, sid: "S-1-5-21-1-2-3-500", name: @"NW\admin");

    internal static async Task<StandardsDto> GetStandards(HttpClient client) =>
        (await client.GetFromJsonAsync<StandardsDto>("/api/v1/compliance/standards"))!;

    internal static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    internal static Task<HttpResponseMessage> Set(HttpClient client, string id, object body) =>
        client.PutAsJsonAsync($"/api/v1/compliance/standards/{id}", body);

    private sealed record Seeded(Guid Server, Guid Payroll, Guid Card, Guid Chart, Guid Birthday, Guid Welcome, Guid FalseCard);

    /// <summary>
    /// One server: payroll.csv (SSN, high), card.txt (card, medium), chart.txt (health, medium), birthday.txt (date of
    /// birth only), welcome.txt (nothing) and test-card.txt (a card match a reviewer marked Public).
    /// </summary>
    private async Task<Seeded> Seed(string host)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
        var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "Data", NameKey = "DATA", UncPath = $@"\\{host}\Data", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, CreatedAt = now, UpdatedAt = now };
        var root = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, Path = "", PathHash = InventoryStore.PathHash(""), FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
        InventoryFile File(string name, Sensitivity sensitivity, Sensitivity? overridden = null) => new()
        {
            Id = Guid.CreateVersion7(),
            ShareId = share.Id,
            FolderId = root.Id,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Extension = PathText.Extension(name),
            Size = 100,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            Sensitivity = sensitivity,
            OverrideSensitivity = overridden,
            ContentState = ContentState.Scanned,
            ClassifiedAt = now,
            ClassifiedJobId = job.Id,
            ClassifiedPolicy = ClassificationPolicy.CurrentVersion,
        };
        var payroll = File("payroll.csv", Sensitivity.Restricted);
        var card = File("card.txt", Sensitivity.Confidential);
        var chart = File("chart.txt", Sensitivity.Restricted);
        var birthday = File("birthday.txt", Sensitivity.Confidential);
        var welcome = File("welcome.txt", Sensitivity.Internal);
        var falseCard = File("test-card.txt", Sensitivity.Confidential, Sensitivity.Public);
        db.AddRange(server, job, share);
        await db.SaveChangesAsync();
        db.Add(root);
        await db.SaveChangesAsync();
        db.AddRange(payroll, card, chart, birthday, welcome, falseCard);
        ClassificationFinding Finding(Guid file, string rule, FindingCategory category, Confidence confidence) => new()
        {
            FileId = file,
            JobId = job.Id,
            PolicyVersion = ClassificationPolicy.CurrentVersion,
            RuleId = rule,
            Category = category,
            Confidence = confidence,
            MatchCount = 1,
            Evidence = ["***"],
            DiscoveredAt = now,
        };
        db.Findings.AddRange(
            Finding(payroll.Id, "DS-SSN-01", FindingCategory.UsSsn, Confidence.High),
            Finding(card.Id, "DS-PCI-01", FindingCategory.PaymentCard, Confidence.Medium),
            Finding(chart.Id, "DS-PHI-01", FindingCategory.HealthInformation, Confidence.Medium),
            Finding(birthday.Id, "DS-DOB-01", FindingCategory.DateOfBirth, Confidence.Medium),
            Finding(falseCard.Id, "DS-PCI-01", FindingCategory.PaymentCard, Confidence.Medium));
        await db.SaveChangesAsync();
        return new Seeded(server.Id, payroll.Id, card.Id, chart.Id, birthday.Id, welcome.Id, falseCard.Id);
    }

    private async Task TurnOn(string id, string[]? categories = null, string? minConfidence = null)
    {
        var response = await Set(Admin, id, new { enabled = true, categories, minConfidence, reason = "Test: this standard applies." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task TurnOff(string id) =>
        Assert.Equal(HttpStatusCode.OK, (await Set(Admin, id, new { enabled = false, reason = "Test: not needed here." })).StatusCode);

    private async Task<PageDto<FileRow>> Files(Guid server, string query = "") =>
        (await Admin.GetFromJsonAsync<PageDto<FileRow>>($"/api/v1/classification/files?serverId={server}&limit=50{query}"))!;

    [Fact]
    public async Task Files_are_tagged_with_the_standards_they_fall_under()
    {
        var s = await Seed("cmp-tags");
        await TurnOn("pci-dss");
        await TurnOn("glba");
        await TurnOn("hipaa");
        await TurnOff("state-breach");
        await TurnOff("secrets");

        var files = (await Files(s.Server)).Items.ToDictionary(f => f.Name);
        Assert.Equal(["glba"], files["payroll.csv"].Standards);
        Assert.Equal(["pci-dss", "glba"], files["card.txt"].Standards);
        Assert.Equal(["hipaa"], files["chart.txt"].Standards);
        Assert.Empty(files["birthday.txt"].Standards);
        Assert.Empty(files["welcome.txt"].Standards);
        // A reviewer said the card match was not real data: it falls under no standard.
        Assert.Empty(files["test-card.txt"].Standards);

        var detail = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/files/{s.Card}/classification");
        Assert.Equal(["pci-dss", "glba"], detail.GetProperty("file").GetProperty("standards").Deserialize<List<string>>(ApiJson));
    }

    [Fact]
    public async Task Sensitive_data_can_be_narrowed_to_a_standard()
    {
        var s = await Seed("cmp-filter");
        await TurnOn("pci-dss");
        await TurnOn("glba");

        Assert.Equal(["card.txt"], (await Files(s.Server, "&standard=pci-dss")).Items.Select(f => f.Name));
        Assert.Equal(["payroll.csv", "card.txt"], (await Files(s.Server, "&standard=GLBA")).Items.Select(f => f.Name));
        var summary = await Admin.GetFromJsonAsync<Summary>($"/api/v1/classification/summary?serverId={s.Server}&standard=glba");
        Assert.Equal(2, summary!.Files);
        Assert.Equal(1, summary.BySensitivity["Restricted"]);
        Assert.Equal(1, summary.BySensitivity["Confidential"]);
    }

    [Fact]
    public async Task A_standard_that_is_off_or_unknown_cannot_be_used_as_a_filter()
    {
        var s = await Seed("cmp-off");
        await TurnOff("secrets");

        var off = await Admin.GetAsync($"/api/v1/classification/files?serverId={s.Server}&standard=secrets");
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        var problem = await off.Content.ReadFromJsonAsync<Problem>();
        Assert.Contains("turned off", problem!.Errors!["standard"][0], StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/classification/summary?standard=sox")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/exposure-map?standard=sox")).StatusCode);
    }

    [Fact]
    public async Task Changing_what_counts_changes_which_files_fall_under_a_standard()
    {
        var s = await Seed("cmp-change");
        await TurnOn("hipaa", ["Health information", "Date of birth"]);
        Assert.Equal(["chart.txt", "birthday.txt"], (await Files(s.Server, "&standard=hipaa")).Items.Select(f => f.Name));

        // Only high-confidence matches: the medium health match no longer counts.
        await TurnOn("hipaa", ["Health information"], "High");
        Assert.Empty((await Files(s.Server, "&standard=hipaa")).Items);

        await TurnOn("hipaa", ["HealthInformation"], "Medium");
        var hipaa = (await GetStandards(Admin)).Standards.Single(x => x.Id == "hipaa");
        Assert.Equal(["Health information"], hipaa.Categories);
        Assert.Equal("Medium", hipaa.MinConfidence);
    }

    [Fact]
    public async Task Custom_rule_matches_count_towards_a_standard_that_includes_them()
    {
        var s = await Seed("cmp-custom");
        await using (var db = _db.CreateContext())
        {
            var job = await db.Findings.Where(f => f.FileId == s.Payroll).Select(f => f.JobId).SingleAsync();
            ClassificationFinding Custom(Guid file, Confidence confidence) => new()
            {
                FileId = file,
                JobId = job,
                PolicyVersion = ClassificationPolicy.CurrentVersion,
                RuleId = CustomRules.RuleId(9001),
                Category = FindingCategory.CustomRule,
                Confidence = confidence,
                MatchCount = 1,
                Evidence = ["***"],
                DiscoveredAt = DateTimeOffset.UtcNow,
            };
            // A "label Confidential" rule matched welcome.txt; a "just list" rule matched birthday.txt.
            db.Findings.AddRange(Custom(s.Welcome, Confidence.Medium), Custom(s.Birthday, Confidence.Low));
            await db.SaveChangesAsync();
        }

        Assert.Contains("Custom rule", (await GetStandards(Admin)).Categories);
        await TurnOn("state-breach", ["Custom rule"]);
        Assert.Equal(["welcome.txt"], (await Files(s.Server, "&standard=state-breach")).Items.Select(f => f.Name));
        await TurnOff("state-breach");
    }

    [Fact]
    public async Task Turning_a_standard_on_or_off_needs_a_reason_and_is_audited()
    {
        var noReason = await Set(Admin, "secrets", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.True((await noReason.Content.ReadFromJsonAsync<Problem>())!.Errors!.ContainsKey("reason"));

        var on = await Set(Admin, "secrets", new { enabled = true, reason = "Auditors asked us to find stored passwords." });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var standard = (await on.Content.ReadFromJsonAsync<Standard>(ApiJson))!;
        Assert.True(standard.Enabled);
        Assert.Equal(("Auditors asked us to find stored passwords.", @"NW\admin"), (standard.Reason, standard.UpdatedByName));
        Assert.NotNull(standard.Counts);

        var off = await Set(Admin, "secrets", new { enabled = false, reason = "Covered by the password manager project." });
        Assert.False((await off.Content.ReadFromJsonAsync<Standard>(ApiJson))!.Enabled);

        await using var db = _db.CreateContext();
        var audit = await db.AppAuditLog.Where(a => a.Action.StartsWith("compliance.")).OrderByDescending(a => a.Sequence).Take(2).ToListAsync();
        Assert.Equal(["compliance.disable", "compliance.enable"], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal("Passwords and keys", a.Target));
        Assert.Contains("Auditors asked us", audit[1].Details, StringComparison.Ordinal);

        var history = await Admin.GetFromJsonAsync<List<Change>>("/api/v1/compliance/history");
        Assert.Equal(("compliance.disable", "Covered by the password manager project."), (history![0].Action, history[0].Reason));
        var row = await db.ComplianceStandards.SingleAsync(x => x.StandardId == "secrets");
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task Saving_without_a_change_records_nothing()
    {
        await TurnOn("glba");
        await using var db = _db.CreateContext();
        var before = await db.AppAuditLog.CountAsync();
        var same = await Set(Admin, "glba", new { enabled = true, reason = "Test: this standard applies." });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(before, await db.AppAuditLog.CountAsync());
    }

    [Theory]
    [InlineData("pci-dss", """{"enabled":true,"categories":["Payment card","Shoe size"],"reason":"Card data"}""", "categories")]
    [InlineData("pci-dss", """{"enabled":true,"categories":[],"reason":"Card data"}""", "categories")]
    [InlineData("pci-dss", """{"minConfidence":"Low","reason":"Card data"}""", "minConfidence")]
    [InlineData("pci-dss", """{"minConfidence":"2","reason":"Card data"}""", "minConfidence")]
    [InlineData("pci-dss", """{"reason":"Card data"}""", "enabled")]
    [InlineData("pci-dss", """{"enabled":true,"reason":"  "}""", "reason")]
    public async Task Bad_changes_are_refused(string id, string body, string field)
    {
        var response = await Admin.PutAsync($"/api/v1/compliance/standards/{id}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<Problem>())!.Errors!.ContainsKey(field));
    }

    [Fact]
    public async Task An_unknown_standard_is_not_found() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Set(Admin, "sox", new { enabled = true, reason = "We are public." })).StatusCode);

    [Theory]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.Viewer)]
    public async Task Posture_readers_see_standards_but_only_admins_change_them(string role)
    {
        var client = _factory.ClientAs(role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/compliance/standards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Set(client, "hipaa", new { enabled = true, reason = "I think it applies." })).StatusCode);
    }

    [Fact]
    public async Task Others_cannot_see_standards_and_only_audit_readers_see_their_history()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.DataOwner).GetAsync("/api/v1/compliance/standards")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/api/v1/compliance/standards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/compliance/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(Roles.Auditor).GetAsync("/api/v1/compliance/history")).StatusCode);
    }
}

/// <summary>Counts across the whole installation, on a database of its own.</summary>
[Trait("Category", "Database")]
public sealed class ComplianceCountTests(TestDatabase db) : IClassFixture<TestDatabase>, IDisposable
{
    private readonly ApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_standard_starts_off_says_what_it_cannot_detect_and_counts_once_on()
    {
        var admin = _factory.ClientAs(Roles.PlatformAdmin);
        var fresh = await ComplianceApiTests.GetStandards(admin);
        Assert.Equal(["hipaa", "pci-dss", "glba", "state-breach", "secrets"], fresh.Standards.Select(s => s.Id));
        Assert.All(fresh.Standards, s =>
        {
            Assert.False(s.Enabled);
            Assert.Null(s.Counts);
            Assert.NotEmpty(s.NotDetected);
            Assert.NotEmpty(s.Requirements);
            Assert.Equal(s.DefaultCategories, s.Categories);
        });
        Assert.Contains("Payment card", fresh.Categories);

        await using (var ctx = db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = "cmp-count", State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
            var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "Pay", NameKey = "PAY", UncPath = @"\\cmp-count\Pay", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, CreatedAt = now, UpdatedAt = now };
            var root = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, Path = "", PathHash = InventoryStore.PathHash(""), FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
            ctx.AddRange(server, job, share);
            await ctx.SaveChangesAsync();
            ctx.Add(root);
            await ctx.SaveChangesAsync();
            InventoryFile File(string name, ContentState state) => new()
            {
                Id = Guid.CreateVersion7(),
                ShareId = share.Id,
                FolderId = root.Id,
                Name = name,
                NameKey = name.ToUpperInvariant(),
                Extension = PathText.Extension(name),
                FirstSeenJobId = job.Id,
                LastSeenJobId = job.Id,
                Sensitivity = state == ContentState.Scanned ? Sensitivity.Restricted : Sensitivity.Unknown,
                ContentState = state,
            };
            var cards = new[] { File("a.txt", ContentState.Scanned), File("b.txt", ContentState.Scanned) };
            ctx.AddRange(cards);
            ctx.Add(File("locked.pdf", ContentState.Encrypted));
            ctx.Findings.AddRange(cards.Select(c => new ClassificationFinding
            {
                FileId = c.Id,
                JobId = job.Id,
                PolicyVersion = ClassificationPolicy.CurrentVersion,
                RuleId = "DS-PCI-01",
                Category = FindingCategory.PaymentCard,
                Confidence = Confidence.High,
                MatchCount = 1,
                Evidence = [],
                DiscoveredAt = now,
            }));
            await ctx.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await ComplianceApiTests.Set(admin, "pci-dss", new { enabled = true, reason = "We take cards by phone." })).StatusCode);
        var on = await ComplianceApiTests.GetStandards(admin);
        var pci = on.Standards.Single(s => s.Id == "pci-dss");
        Assert.Equal(new ComplianceApiTests.Counts(2, 2, 0, 1, 1), pci.Counts);
        Assert.Null(on.Standards.Single(s => s.Id == "hipaa").Counts);
        // The file that could not be read may hold card data: it is counted as a limitation, never as clean.
        Assert.Equal(1, on.FilesNotRead);
    }
}
