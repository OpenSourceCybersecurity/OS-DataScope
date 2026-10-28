using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>Custom detection rules (D133). Rules are settings for the whole installation, so every test starts with none.</summary>
[Trait("Category", "Database")]
public sealed class CustomRuleApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public CustomRuleApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Rule(
        int Id, string RuleId, string Name, string? Description, string Kind, string? Pattern, List<string> Keywords, bool CaseSensitive,
        List<string> NearWords, int MinMatches, string Effect, bool Enabled, string Summary, string Reason, string CreatedByName, string UpdatedByName, long Files);

    private sealed record Template(string Id, string Name, string Kind, string? Pattern, List<string> Keywords, int MinMatches, string Effect, string Example);
    private sealed record Limits(int MaxRules, int MaxEnabled, int MaxSampleChars);
    private sealed record ListDto(List<Rule> Rules, List<Template> Templates, Limits Limits, string PolicyVersion, long FilesToReread);
    private sealed record Change(DateTimeOffset At, string ActorName, string Action, string Target, string? Reason);
    private sealed record TestResult(int Values, bool Reported, List<string> Evidence, string Label, bool Truncated);
    private sealed record Problem(string? Title, Dictionary<string, string[]>? Errors);
    private sealed record FindingDto(string RuleId, string RuleName, string Category, string Confidence, int Matches);
    private sealed record Detail(List<FindingDto> Findings);
    private sealed record FileRow(Guid Id, string Name);
    private sealed record PageDto<T>(List<T> Items, int Total);

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin, sid: "S-1-5-21-1-2-3-500", name: @"NW\admin");

    private static readonly object Falcon = new
    {
        name = "Project Falcon",
        kind = "Keywords",
        keywords = new List<string> { "Project Falcon", "falcon budget" },
        effect = "Restricted",
        reason = "The acquisition is secret until announced.",
    };

    private async Task Reset()
    {
        await using var db = _db.CreateContext();
        await db.CustomRules.ExecuteDeleteAsync();
    }

    private static async Task<ListDto> List(HttpClient client) => (await client.GetFromJsonAsync<ListDto>("/api/v1/custom-rules"))!;

    private async Task<Rule> Add(object body)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/custom-rules", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Rule>())!;
    }

    private static async Task<Dictionary<string, string[]>> Errors(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Problem>())!.Errors!;
    }

    [Fact]
    public async Task An_admin_adds_a_rule_with_a_reason_and_it_is_listed_audited_and_changes_the_policy_version()
    {
        await Reset();
        var empty = await List(Admin);
        Assert.Empty(empty.Rules);
        Assert.Equal(ClassificationPolicy.CurrentVersion, empty.PolicyVersion);
        Assert.Contains(empty.Templates, t => t.Id == "ipv4" && t.Kind == "Pattern");
        Assert.Equal(CustomRules.MaxEnabled, empty.Limits.MaxEnabled);

        var rule = await Add(Falcon);
        Assert.Equal($"DS-CUSTOM-{rule.Id}", rule.RuleId);
        Assert.True(rule.Enabled);
        Assert.Equal(["Project Falcon", "falcon budget"], rule.Keywords);
        Assert.Equal(@"NW\admin", rule.CreatedByName);
        Assert.Contains("labelled Restricted", rule.Summary, StringComparison.Ordinal);

        var list = await List(Admin);
        Assert.Equal("Project Falcon", Assert.Single(list.Rules).Name);
        Assert.StartsWith(ClassificationPolicy.CurrentVersion + "+c", list.PolicyVersion, StringComparison.Ordinal);

        var history = (await Admin.GetFromJsonAsync<List<Change>>("/api/v1/custom-rules/history"))!;
        var added = history.First();
        Assert.Equal(("custom-rule.add", "Project Falcon", "The acquisition is secret until announced."), (added.Action, added.Target, added.Reason));

    }

    [Fact]
    public async Task The_rule_catalogue_names_custom_rules_without_their_words()
    {
        await Reset();
        var rule = await Add(Falcon);
        var json = await _factory.ClientAs(Roles.Viewer).GetStringAsync("/api/v1/classification/rules");
        Assert.Contains(rule.RuleId, json, StringComparison.Ordinal);
        Assert.Contains("Custom rule", json, StringComparison.Ordinal);
        Assert.DoesNotContain("falcon budget", json, StringComparison.Ordinal);
        Assert.Contains("+c", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_that_cant_be_used_are_refused_with_reasons()
    {
        await Reset();
        var bad = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "", kind = "Pattern", pattern = "(?=x)a", effect = "Loud", reason = "" }));
        Assert.Contains("name", bad.Keys);
        Assert.Contains("effect", bad.Keys);
        Assert.Contains("reason", bad.Keys);

        var pattern = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "Lookahead", kind = "Pattern", pattern = "(?=x)a", effect = "ListOnly", reason = "testing" }));
        Assert.Contains("Look-ahead", pattern["pattern"][0], StringComparison.Ordinal);

        var empty = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "Everything", kind = "Pattern", pattern = ".*", effect = "ListOnly", reason = "testing" }));
        Assert.Contains("empty text", empty["pattern"][0], StringComparison.Ordinal);

        var kind = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "Odd", kind = "1", pattern = "abc", effect = "ListOnly", reason = "testing" }));
        Assert.Contains("kind", kind.Keys);

        var words = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "No words", kind = "Keywords", keywords = new List<string> { " " }, effect = "ListOnly", reason = "testing" }));
        Assert.Contains("keywords", words.Keys);

        await Add(Falcon);
        var duplicate = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "project FALCON", kind = "Keywords", keywords = new List<string> { "x" }, effect = "ListOnly", reason = "testing" }));
        Assert.Contains("already", duplicate["name"][0], StringComparison.Ordinal);
        Assert.Single((await List(Admin)).Rules);
    }

    [Theory]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.DataOwner)]
    public async Task Only_administrators_change_or_try_rules(string role)
    {
        await Reset();
        var rule = await Add(Falcon);
        var client = _factory.ClientAs(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/custom-rules", Falcon)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { enabled = false, reason = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/custom-rules/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/custom-rules/test", new { kind = "Keywords", keywords = new List<string> { "x" }, effect = "ListOnly", sample = "x" })).StatusCode);
        // Auditors read the rules and their history; others don't see the words at all.
        var read = await client.GetAsync("/api/v1/custom-rules");
        Assert.Equal(role == Roles.Auditor ? HttpStatusCode.OK : HttpStatusCode.Forbidden, read.StatusCode);
        Assert.True((await List(Admin)).Rules.Single().Enabled);
    }

    [Fact]
    public async Task Signed_out_callers_get_nothing()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/custom-rules")).StatusCode);
    }

    [Fact]
    public async Task Changing_turning_off_and_on_each_need_a_reason_and_are_audited()
    {
        await Reset();
        var rule = await Add(Falcon);
        var version = (await List(Admin)).PolicyVersion;

        var noReason = await Errors(await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { effect = "Confidential" }));
        Assert.Contains("reason", noReason.Keys);

        var changed = await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { effect = "Confidential", nearWords = new List<string> { "board" }, reason = "Announced to staff." });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var after = (await changed.Content.ReadFromJsonAsync<Rule>())!;
        Assert.Equal(("Confidential", "Keywords", 2), (after.Effect, after.Kind, after.Keywords.Count));
        Assert.Equal(["board"], after.NearWords);
        var changedVersion = (await List(Admin)).PolicyVersion;
        Assert.NotEqual(version, changedVersion);

        var off = await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { enabled = false, reason = "Paused while we tune it." });
        Assert.False((await off.Content.ReadFromJsonAsync<Rule>())!.Enabled);
        Assert.Equal(ClassificationPolicy.CurrentVersion, (await List(Admin)).PolicyVersion);

        await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { enabled = true, reason = "Tuned." });
        Assert.Equal(changedVersion, (await List(Admin)).PolicyVersion);

        // A rename changes nothing a scan does, so files aren't read again for it.
        await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { name = "Falcon deal", reason = "Clearer name." });
        Assert.Equal(changedVersion, (await List(Admin)).PolicyVersion);

        await using var db = _db.CreateContext();
        var actions = await db.AppAuditLog.Where(a => a.Action.StartsWith("custom-rule.")).OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync();
        Assert.Equal(["custom-rule.add", "custom-rule.change", "custom-rule.disable", "custom-rule.enable", "custom-rule.change"], actions.TakeLast(5));

        // Saving the same thing with the same reason adds nothing.
        var count = await db.AppAuditLog.CountAsync();
        Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { effect = "Confidential", reason = "Clearer name." })).StatusCode);
        Assert.Equal(count, await db.AppAuditLog.CountAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync("/api/v1/custom-rules/999999", new { enabled = false, reason = "missing" })).StatusCode);
    }

    [Fact]
    public async Task A_rule_can_switch_from_keywords_to_a_pattern()
    {
        await Reset();
        var rule = await Add(Falcon);
        var response = await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { kind = "Pattern", pattern = @"\bFAL-[0-9]{4}\b", reason = "Use the deal code." });
        var after = (await response.Content.ReadFromJsonAsync<Rule>())!;
        Assert.Equal(("Pattern", @"\bFAL-[0-9]{4}\b"), (after.Kind, after.Pattern));
        Assert.Empty(after.Keywords);
        // Switching without saying what to look for is refused.
        var bad = await Errors(await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{rule.Id}", new { kind = "Keywords", reason = "Back to words." }));
        Assert.Contains("keywords", bad.Keys);
    }

    [Fact]
    public async Task At_most_25_rules_are_on_at_once()
    {
        await Reset();
        for (var i = 0; i < CustomRules.MaxEnabled; i++)
        {
            await Add(new { name = $"Code {i}", kind = "Keywords", keywords = new List<string> { $"codeword{i}" }, effect = "ListOnly", reason = "load test" });
        }
        var tooMany = await Admin.PostAsJsonAsync("/api/v1/custom-rules", new { name = "One more", kind = "Keywords", keywords = new List<string> { "more" }, effect = "ListOnly", reason = "load test" });
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        var off = await Add(new { name = "One more", kind = "Keywords", keywords = new List<string> { "more" }, effect = "ListOnly", enabled = false, reason = "load test" });
        Assert.False(off.Enabled);
        var turnOn = await Admin.PutAsJsonAsync($"/api/v1/custom-rules/{off.Id}", new { enabled = true, reason = "load test" });
        Assert.Equal(HttpStatusCode.Conflict, turnOn.StatusCode);
        await Reset();
    }

    [Fact]
    public async Task The_test_box_shows_masked_matches_and_the_label_without_storing_anything()
    {
        await Reset();
        await using var db = _db.CreateContext();
        var before = await db.AppAuditLog.CountAsync();
        var response = await Admin.PostAsJsonAsync("/api/v1/custom-rules/test", new
        {
            kind = "Pattern",
            pattern = CustomRules.Templates.Single(t => t.Id == "ipv4").Pattern,
            effect = "Confidential",
            minMatches = 2,
            sample = "Allow 192.0.2.10 and 198.51.100.7 through the firewall.",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TestResult>())!;
        Assert.Equal((2, true, "Confidential", false), (result.Values, result.Reported, result.Label, result.Truncated));
        Assert.Equal(2, result.Evidence.Count);
        Assert.DoesNotContain(result.Evidence, e => e.Contains("192.0", StringComparison.Ordinal) || e.Contains("198.51", StringComparison.Ordinal));

        var notEnough = (await (await Admin.PostAsJsonAsync("/api/v1/custom-rules/test", new
        {
            kind = "Keywords",
            keywords = new List<string> { "falcon" },
            effect = "Restricted",
            minMatches = 2,
            sample = "Falcon, falcon and FALCON are one word.",
        })).Content.ReadFromJsonAsync<TestResult>())!;
        Assert.Equal((1, false, "Internal"), (notEnough.Values, notEnough.Reported, notEnough.Label));
        Assert.Single(notEnough.Evidence);

        var bad = await Errors(await Admin.PostAsJsonAsync("/api/v1/custom-rules/test", new { kind = "Pattern", pattern = "[", effect = "ListOnly", sample = "" }));
        Assert.Contains("pattern", bad.Keys);
        Assert.Contains("sample", bad.Keys);

        var longSample = (await (await Admin.PostAsJsonAsync("/api/v1/custom-rules/test", new
        {
            kind = "Keywords",
            keywords = new List<string> { "falcon" },
            effect = "ListOnly",
            sample = new string('x', CustomRules.MaxSampleChars) + " falcon",
        })).Content.ReadFromJsonAsync<TestResult>())!;
        Assert.True(longSample.Truncated);
        Assert.Equal(0, longSample.Values);

        Assert.Equal(before, await db.AppAuditLog.CountAsync());
        Assert.Empty(await db.CustomRules.ToListAsync());
    }

    [Fact]
    public async Task Files_can_be_listed_by_rule_and_a_removed_rule_still_names_its_findings()
    {
        await Reset();
        var rule = await Add(Falcon);
        var fileId = await SeedFileWithFinding(rule.RuleId);

        var page = (await Admin.GetFromJsonAsync<PageDto<FileRow>>($"/api/v1/classification/files?rule={rule.RuleId}"))!;
        Assert.Equal(fileId, Assert.Single(page.Items).Id);
        var other = (await Admin.GetFromJsonAsync<PageDto<FileRow>>("/api/v1/classification/files?rule=DS-CUSTOM-999999"))!;
        Assert.Empty(other.Items);
        var badRule = await Admin.GetAsync("/api/v1/classification/files?rule=drop%20table");
        Assert.Equal(HttpStatusCode.BadRequest, badRule.StatusCode);
        var byCategory = (await Admin.GetFromJsonAsync<PageDto<FileRow>>("/api/v1/classification/files?category=Custom%20rule"))!;
        Assert.Contains(byCategory.Items, f => f.Id == fileId);

        Assert.Equal(1, (await List(Admin)).Rules.Single().Files);
        var named = (await Admin.GetFromJsonAsync<Detail>($"/api/v1/files/{fileId}/classification"))!;
        Assert.Equal(("Project Falcon", "Custom rule"), (named.Findings.Single().RuleName, named.Findings.Single().Category));

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/custom-rules/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/api/v1/custom-rules/{rule.Id}")).StatusCode);
        var removed = (await Admin.GetFromJsonAsync<Detail>($"/api/v1/files/{fileId}/classification"))!;
        Assert.Equal("Custom rule (removed)", removed.Findings.Single().RuleName);
        var history = (await Admin.GetFromJsonAsync<List<Change>>("/api/v1/custom-rules/history"))!;
        Assert.Equal(("custom-rule.remove", "Project Falcon"), (history[0].Action, history[0].Target));
        // The file was classified with the rule; with it gone, it is read again on the next scan.
        Assert.True((await List(Admin)).FilesToReread >= 1);
    }

    private async Task<Guid> SeedFileWithFinding(string ruleId)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var host = $"cr-{Guid.NewGuid():N}"[..12];
        var policy = await CustomRuleStore.PolicyVersionAsync(db, CancellationToken.None);
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = policy, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
        var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "Deals", NameKey = "DEALS", UncPath = $@"\\{host}\Deals", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, CreatedAt = now, UpdatedAt = now };
        var root = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, Path = "", PathHash = InventoryStore.PathHash(""), FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
        var file = new InventoryFile
        {
            Id = Guid.CreateVersion7(),
            ShareId = share.Id,
            FolderId = root.Id,
            Name = "falcon-memo.txt",
            NameKey = "FALCON-MEMO.TXT",
            Extension = "txt",
            Size = 100,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            Sensitivity = Sensitivity.Restricted,
            ContentState = ContentState.Scanned,
            ClassifiedAt = now,
            ClassifiedJobId = job.Id,
            ClassifiedPolicy = policy,
        };
        db.AddRange(server, job, share);
        await db.SaveChangesAsync();
        db.Add(root);
        await db.SaveChangesAsync();
        db.Add(file);
        db.Findings.Add(new ClassificationFinding
        {
            FileId = file.Id,
            JobId = job.Id,
            PolicyVersion = policy,
            RuleId = ruleId,
            Category = FindingCategory.CustomRule,
            Confidence = Confidence.High,
            MatchCount = 1,
            Evidence = ["Notes on Project Falcon"],
            DiscoveredAt = now,
        });
        await db.SaveChangesAsync();
        return file.Id;
    }
}
