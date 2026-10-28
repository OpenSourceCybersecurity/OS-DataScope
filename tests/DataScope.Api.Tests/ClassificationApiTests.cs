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
public sealed class ClassificationApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ClassificationApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);
    private sealed record CategoryCount(string Category, string Confidence, int Matches);
    private sealed record FileDto(Guid Id, string Hostname, string ShareName, string Path, string Name, string Sensitivity, string PolicySensitivity,
        string? OverrideSensitivity, string ContentState, string? ContentNote, List<CategoryCount> Categories);
    private sealed record FindingDto(string RuleId, string RuleName, string Category, string Confidence, int Matches, List<string>? Evidence, string PolicyVersion);
    private sealed record OverrideDto(string? Sensitivity, string PolicySensitivity, string Reason, string ReviewerName);
    private sealed record DetailDto(FileDto File, List<FindingDto> Findings, bool EvidenceHidden, List<OverrideDto> Overrides, bool ChangedSinceReview);
    private sealed record CategorySummary(string Category, int Files, int High, int Medium, int Low);
    private sealed record SummaryDto(string PolicyVersion, long Files, Dictionary<string, long> BySensitivity, Dictionary<string, long> ByContentState,
        List<CategorySummary> ByCategory, long Overrides);
    private sealed record RuleDto(string Id, string Name, string Category);
    private sealed record RulesDto(string PolicyVersion, List<RuleDto> Rules);
    private sealed record AuditDto(string Action, string Target, string Details);

    private sealed record Seeded(Guid Server, Guid Share, Guid Payroll, Guid Card, Guid Welcome, Guid Locked, Guid Removed);

    /// <summary>
    /// One server, one share: payroll.csv (Restricted, SSN), card.txt (Confidential, medium card), welcome.txt (Internal),
    /// locked.docx (encrypted, Unknown) and a removed file that must never be listed.
    /// </summary>
    private async Task<Seeded> Seed(string host)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
        var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "HR", NameKey = "HR", UncPath = $@"\\{host}\HR", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, CreatedAt = now, UpdatedAt = now };
        var root = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, Path = "", PathHash = InventoryStore.PathHash(""), FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
        var folder = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, ParentId = root.Id, Path = "Payroll", PathHash = InventoryStore.PathHash("Payroll"), Depth = 1, FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
        InventoryFile File(string name, Sensitivity sensitivity, ContentState state, string? note = null, bool removed = false) => new()
        {
            Id = Guid.CreateVersion7(),
            ShareId = share.Id,
            FolderId = folder.Id,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Extension = PathText.Extension(name),
            Size = 100,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            Sensitivity = sensitivity,
            ContentState = state,
            ContentNote = note,
            ClassifiedAt = now,
            ClassifiedJobId = job.Id,
            ClassifiedPolicy = ClassificationPolicy.CurrentVersion,
            RemovedAt = removed ? now : null,
        };
        var payroll = File("payroll.csv", Sensitivity.Restricted, ContentState.Scanned);
        var card = File("card.txt", Sensitivity.Confidential, ContentState.Scanned);
        var welcome = File("welcome.txt", Sensitivity.Internal, ContentState.Scanned);
        var locked = File("locked.docx", Sensitivity.Unknown, ContentState.Encrypted, "The file is password-protected.");
        var removed = File("old.csv", Sensitivity.Restricted, ContentState.Scanned, removed: true);
        db.AddRange(server, job, share);
        await db.SaveChangesAsync();
        db.Add(root);
        await db.SaveChangesAsync();
        db.Add(folder);
        await db.SaveChangesAsync();
        db.AddRange(payroll, card, welcome, locked, removed);
        ClassificationFinding Finding(Guid file, string rule, FindingCategory category, Confidence confidence, string evidence) => new()
        {
            FileId = file,
            JobId = job.Id,
            PolicyVersion = ClassificationPolicy.CurrentVersion,
            RuleId = rule,
            Category = category,
            Confidence = confidence,
            MatchCount = 2,
            Evidence = [evidence],
            DiscoveredAt = now,
        };
        db.Findings.AddRange(
            Finding(payroll.Id, "DS-SSN-01", FindingCategory.UsSsn, Confidence.High, "Avery Okafor,***-**-3307"),
            Finding(payroll.Id, "DS-DOB-01", FindingCategory.DateOfBirth, Confidence.Medium, "DOB **/**/87"),
            Finding(card.Id, "DS-PCI-01", FindingCategory.PaymentCard, Confidence.Medium, "Number **** **** **** 1111"),
            Finding(removed.Id, "DS-SSN-01", FindingCategory.UsSsn, Confidence.High, "***-**-0001"));
        await db.SaveChangesAsync();
        return new Seeded(server.Id, share.Id, payroll.Id, card.Id, welcome.Id, locked.Id, removed.Id);
    }

    private static string Files(Guid server, string query = "") => $"/api/v1/classification/files?serverId={server}{query}";

    [Fact]
    public async Task Lists_live_files_most_sensitive_first_with_categories()
    {
        var s = await Seed("cls-api-1");
        var page = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<PageDto<FileDto>>(Files(s.Server));

        Assert.Equal(4, page!.Total);
        Assert.Equal(["payroll.csv", "card.txt", "welcome.txt", "locked.docx"], page.Items.Select(f => f.Name));
        var payroll = page.Items[0];
        Assert.Equal(@"Payroll\payroll.csv", payroll.Path);
        Assert.Equal("Restricted", payroll.Sensitivity);
        Assert.Equal(["Date of birth", "US SSN"], payroll.Categories.Select(c => c.Category).Order(StringComparer.Ordinal));
        Assert.Equal("high", payroll.Categories.Single(c => c.Category == "US SSN").Confidence);
        Assert.Equal("Unknown", page.Items[3].Sensitivity);
        Assert.Equal("Encrypted", page.Items[3].ContentState);
    }

    [Theory]
    [InlineData("&minSensitivity=Confidential", "payroll.csv card.txt")]
    [InlineData("&sensitivity=internal", "welcome.txt")]
    [InlineData("&category=US%20SSN", "payroll.csv")]
    [InlineData("&category=PaymentCard", "card.txt")]
    [InlineData("&minConfidence=High", "payroll.csv")]
    [InlineData("&contentState=Encrypted", "locked.docx")]
    [InlineData("&sensitivity=Unknown", "locked.docx")]
    [InlineData("&notRead=true", "locked.docx")]
    public async Task Filters_files(string query, string expected)
    {
        var s = await Seed("cls-api-f" + Guid.NewGuid().ToString("N")[..8]);
        var page = await _factory.ClientAs(Roles.Auditor).GetFromJsonAsync<PageDto<FileDto>>(Files(s.Server, query));
        Assert.Equal(expected.Split(' '), page!.Items.Select(f => f.Name));
    }

    [Theory]
    [InlineData("&sensitivity=Secret")]
    [InlineData("&minSensitivity=7")]
    [InlineData("&category=Nuclear")]
    [InlineData("&minConfidence=certain")]
    [InlineData("&contentState=Eaten")]
    public async Task Rejects_unknown_filter_values(string query)
    {
        var s = await Seed("cls-api-bad" + Guid.NewGuid().ToString("N")[..8]);
        var res = await _factory.ClientAs(Roles.Viewer).GetAsync(Files(s.Server, query));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData(Roles.PlatformAdmin)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    public async Task Evidence_is_shown_to_roles_that_may_read_it(string role)
    {
        var s = await Seed("cls-api-ev" + Guid.NewGuid().ToString("N")[..8]);
        var detail = await _factory.ClientAs(role).GetFromJsonAsync<DetailDto>($"/api/v1/files/{s.Payroll}/classification");
        Assert.False(detail!.EvidenceHidden);
        var ssn = detail.Findings.Single(f => f.RuleId == "DS-SSN-01");
        Assert.Equal("US Social Security number", ssn.RuleName);
        Assert.Equal(["Avery Okafor,***-**-3307"], ssn.Evidence);
    }

    [Fact]
    public async Task Viewers_see_categories_and_counts_but_never_evidence()
    {
        var s = await Seed("cls-api-viewer");
        var res = await _factory.ClientAs(Roles.Viewer).GetAsync($"/api/v1/files/{s.Payroll}/classification");
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("3307", body, StringComparison.Ordinal);
        var detail = await res.Content.ReadFromJsonAsync<DetailDto>();
        Assert.True(detail!.EvidenceHidden);
        Assert.All(detail.Findings, f => Assert.Null(f.Evidence));
        Assert.Equal(2, detail.Findings.Single(f => f.RuleId == "DS-SSN-01").Matches);
    }

    [Fact]
    public async Task Anonymous_and_unmapped_users_are_refused()
    {
        var s = await Seed("cls-api-anon");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync(Files(s.Server))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync($"/api/v1/files/{s.Payroll}/classification")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.DataOwner).GetAsync(Files(s.Server))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("").GetAsync("/api/v1/classification/summary")).StatusCode);
    }

    [Fact]
    public async Task Unknown_files_return_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs(Roles.Auditor).GetAsync($"/api/v1/files/{Guid.NewGuid()}/classification")).StatusCode);
        var res = await _factory.ClientAs(Roles.SecurityAnalyst).PostAsJsonAsync($"/api/v1/files/{Guid.NewGuid()}/classification-override", new { sensitivity = "Public", reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task An_analyst_can_override_a_label_and_it_is_audited()
    {
        var s = await Seed("cls-api-ovr");
        var client = _factory.ClientAs(Roles.SecurityAnalyst, name: "NW\\analyst");

        var res = await client.PostAsJsonAsync($"/api/v1/files/{s.Card}/classification-override", new { sensitivity = "internal", reason = "Test card number from the vendor's documentation." });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var detail = await res.Content.ReadFromJsonAsync<DetailDto>();
        Assert.Equal("Internal", detail!.File.Sensitivity);
        Assert.Equal("Confidential", detail.File.PolicySensitivity);
        Assert.Equal("Internal", detail.File.OverrideSensitivity);
        var history = Assert.Single(detail.Overrides);
        Assert.Equal("NW\\analyst", history.ReviewerName);
        Assert.Equal("Confidential", history.PolicySensitivity);
        Assert.False(detail.ChangedSinceReview);

        var filtered = await client.GetFromJsonAsync<PageDto<FileDto>>(Files(s.Server, "&minSensitivity=Confidential"));
        Assert.Equal(["payroll.csv"], filtered!.Items.Select(f => f.Name));

        await using var db = _db.CreateContext();
        var entry = await db.AppAuditLog.Where(a => a.Action == "classification.override").OrderByDescending(a => a.Sequence).FirstAsync();
        Assert.Equal(@"\\cls-api-ovr\HR\Payroll\card.txt", entry.Target);
        Assert.Contains("vendor", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"To\":\"Internal\"", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_an_override_restores_the_policy_label()
    {
        var s = await Seed("cls-api-undo");
        var client = _factory.ClientAs(Roles.PlatformAdmin);
        await client.PostAsJsonAsync($"/api/v1/files/{s.Welcome}/classification-override", new { sensitivity = "Restricted", reason = "Board minutes." });

        var res = await client.PostAsJsonAsync($"/api/v1/files/{s.Welcome}/classification-override", new { sensitivity = (string?)null, reason = "Wrong file." });

        var detail = await res.Content.ReadFromJsonAsync<DetailDto>();
        Assert.Equal("Internal", detail!.File.Sensitivity);
        Assert.Null(detail.File.OverrideSensitivity);
        Assert.Equal([null, "Restricted"], detail.Overrides.Select(o => o.Sensitivity));
        await using var db = _db.CreateContext();
        Assert.True(await db.AppAuditLog.AnyAsync(a => a.Action == "classification.override.remove" && a.Target.EndsWith("welcome.txt")));

        var again = await client.PostAsJsonAsync($"/api/v1/files/{s.Welcome}/classification-override", new { sensitivity = (string?)null, reason = "Again." });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.DataOwner)]
    public async Task Only_admins_and_analysts_can_override(string role)
    {
        var s = await Seed("cls-api-deny" + Guid.NewGuid().ToString("N")[..8]);
        var res = await _factory.ClientAs(role).PostAsJsonAsync($"/api/v1/files/{s.Payroll}/classification-override", new { sensitivity = "Public", reason = "Not sensitive." });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await using var db = _db.CreateContext();
        Assert.Null((await db.Files.SingleAsync(f => f.Id == s.Payroll)).OverrideSensitivity);
        Assert.False(await db.ClassificationOverrides.AnyAsync(o => o.FileId == s.Payroll));
    }

    [Fact]
    public async Task Overrides_need_the_anti_forgery_header()
    {
        var s = await Seed("cls-api-csrf");
        var res = await _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false)
            .PostAsJsonAsync($"/api/v1/files/{s.Payroll}/classification-override", new { sensitivity = "Public", reason = "Not sensitive." });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await using var db = _db.CreateContext();
        Assert.Null((await db.Files.SingleAsync(f => f.Id == s.Payroll)).OverrideSensitivity);
    }

    [Theory]
    [InlineData("Public", null)]
    [InlineData("Public", "")]
    [InlineData("Public", "  x ")]
    [InlineData("Unknown", "Not read yet.")]
    [InlineData("TopSecret", "Not a label.")]
    public async Task Override_requests_are_validated(string sensitivity, string? reason)
    {
        var s = await Seed("cls-api-val" + Guid.NewGuid().ToString("N")[..8]);
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync($"/api/v1/files/{s.Payroll}/classification-override", new { sensitivity, reason });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_file_reclassified_after_review_is_flagged()
    {
        var s = await Seed("cls-api-stale");
        await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync($"/api/v1/files/{s.Card}/classification-override", new { sensitivity = "Public", reason = "Sample data." });
        await using (var db = _db.CreateContext())
        {
            await db.Files.Where(f => f.Id == s.Card).ExecuteUpdateAsync(x => x.SetProperty(f => f.ClassifiedAt, DateTimeOffset.UtcNow.AddMinutes(10)));
        }
        var detail = await _factory.ClientAs(Roles.Auditor).GetFromJsonAsync<DetailDto>($"/api/v1/files/{s.Card}/classification");
        Assert.True(detail!.ChangedSinceReview);
    }

    [Fact]
    public async Task Summary_counts_effective_labels_coverage_and_categories()
    {
        var s = await Seed("cls-api-sum");
        await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync($"/api/v1/files/{s.Welcome}/classification-override", new { sensitivity = "Confidential", reason = "Draft offer letter." });

        var summary = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<SummaryDto>($"/api/v1/classification/summary?serverId={s.Server}");

        Assert.Equal(ClassificationPolicy.CurrentVersion, summary!.PolicyVersion);
        Assert.Equal(4, summary.Files);
        Assert.Equal(1, summary.BySensitivity["Restricted"]);
        Assert.Equal(2, summary.BySensitivity["Confidential"]);
        Assert.Equal(0, summary.BySensitivity["Internal"]);
        Assert.Equal(1, summary.BySensitivity["Unknown"]);
        Assert.Equal(3, summary.ByContentState["Scanned"]);
        Assert.Equal(1, summary.ByContentState["Encrypted"]);
        Assert.Equal(1, summary.Overrides);
        var ssn = summary.ByCategory.Single(c => c.Category == "US SSN");
        Assert.Equal((1, 1, 0), (ssn.Files, ssn.High, ssn.Medium));
        Assert.Equal(1, summary.ByCategory.Single(c => c.Category == "Payment card").Medium);
    }

    [Fact]
    public async Task Rules_are_listed_with_the_policy_version()
    {
        var rules = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<RulesDto>("/api/v1/classification/rules");
        Assert.Equal(ClassificationPolicy.CurrentVersion, rules!.PolicyVersion);
        Assert.Equal(12, rules.Rules.Count);
        Assert.Contains(rules.Rules, r => r.Id == "DS-PCI-01" && r.Category == "Payment card");
        Assert.Contains(rules.Rules, r => r.Id == "DS-DL-01" && r.Category == "Driver's licence");
        Assert.Contains(rules.Rules, r => r.Id == "DS-PASS-01" && r.Category == "Passport");
        Assert.Contains(rules.Rules, r => r.Id == "DS-BANK-02" && r.Category == "Bank account");
        Assert.Contains(rules.Rules, r => r.Id == "DS-PII-01" && r.Category == "Personal details");
    }
}
