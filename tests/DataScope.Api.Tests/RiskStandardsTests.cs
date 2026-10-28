using System.Net;
using System.Net.Http.Json;
using DataScope.Api.Compliance;
using DataScope.Api.Risks;
using DataScope.Core.Classification;
using DataScope.Core.Compliance;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// Risks per compliance standard (D136) over the synthetic governance lab, with GLBA and PCI DSS turned on: made-up
/// SSNs in Public (no owner, any domain user can read), card numbers in Public\Cards, an SSN in Finance\Tax (owner
/// assigned 13 months ago, never reviewed), a password in Finance, and a four-year-old SSN file in Ops (permissions
/// not read, last reviewed 120 days ago on a quarterly cycle) beside a card number a reviewer labelled Internal. HR's
/// owner reviewed ten days ago. Every name and number here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class RiskStandardsTests : IClassFixture<RiskStandardsTests.StandardsLab>
{
    private const string Host = "gov-fs01";
    private const string AdminSid = "S-1-5-21-40-50-60-1110";

    private readonly StandardsLab _fixture;
    private readonly GovernanceApiTests.Lab _lab;

    public RiskStandardsTests(StandardsLab lab) => (_fixture, _lab) = (lab, lab.Lab);

    public sealed record RiskView(
        string Id, string Rule, string Severity, string Title, string Where, string? Who, int? Count, int? Restricted, string Why, string Status,
        List<string> Standards, string? RaisedFrom, DecisionView? Decision);

    public sealed record DecisionView(long Id, bool Superseded);

    public sealed record RiskListView(List<RiskView> Items, int High, int Medium, int Low);

    public sealed class StandardsLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();

        /// <summary>Every risk before any standard was turned on.</summary>
        public List<RiskView> Baseline { get; private set; } = [];

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"Public\staff.csv", "Name,SSN\nCasey Byrne,512-48-3311\nDana Shah,512-48-3312\n");
            Lab.Write(@"Public\Cards\orders.txt", "Visa 4111 1111 1111 1111 exp 04/28");
            Lab.Write(@"Finance\deploy-notes.txt", "Database server: sql01\npassword=Synthetic-Only-1\n");
            Lab.Write(@"Finance\Tax\w2.csv", "Name,SSN\nFrankie Lowe,512-48-3314\n");
            Lab.Write(@"Ops\old-payroll.csv", "Name,SSN\nEli Ward,512-48-3313\n");
            Lab.Write(@"Ops\card-sample.txt", "Test card 4111 1111 1111 1111 exp 04/28");
            await Lab.RescanAsync();

            await using (var db = Lab.Database.CreateContext())
            {
                var now = DateTimeOffset.UtcNow;
                var ops = Lab.Shares["Ops"];
                var old = await db.Files.SingleAsync(f => f.ShareId == ops && f.Name == "old-payroll.csv");
                old.ModifiedAtSource = old.CreatedAtSource = now.AddYears(-4);
                var sample = await db.Files.SingleAsync(f => f.ShareId == ops && f.Name == "card-sample.txt");
                sample.OverrideSensitivity = Sensitivity.Internal;
                foreach (var a in await db.OwnerAssignments.Where(a => a.EndedAt == null).ToListAsync())
                {
                    a.AssignedAt = now.AddMonths(-13);
                }
                var campaign = new ReviewCampaign
                {
                    Id = Guid.NewGuid(),
                    Name = "Earlier review",
                    DueOn = DateOnly.FromDateTime(now.UtcDateTime),
                    State = CampaignState.Closed,
                    Scope = "{}",
                    Skipped = "[]",
                    CreatedBySid = "S-1",
                    CreatedByName = "t",
                    CreatedAt = now.AddDays(-150),
                };
                db.ReviewCampaigns.Add(campaign);
                AccessReview Review(string share, int daysAgo) => new()
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    FolderId = Lab.Roots[share],
                    ShareId = Lab.Shares[share],
                    ServerId = Lab.ServerId,
                    Resource = $@"\\{Host}\{share}",
                    OwnerSid = "S-1",
                    OwnerName = "t",
                    State = ReviewState.Submitted,
                    EvidenceHash = new string('0', 64),
                    FrozenAt = now.AddDays(-daysAgo - 5),
                    SubmittedAt = now.AddDays(-daysAgo),
                    SubmittedBySid = "S-1",
                    SubmittedByName = "t",
                };
                db.AccessReviews.AddRange(Review("HR", 10), Review("Ops", 120));
                await db.SaveChangesAsync();
            }

            var admin = Admin(Lab);
            Baseline = (await Ok<RiskListView>(await admin.GetAsync("/api/v1/risks?view=all"))).Items;
            foreach (var id in new[] { "glba", "pci-dss" })
            {
                await SetAsync(admin, id, true);
            }
        }

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private static HttpClient Admin(GovernanceApiTests.Lab lab) => lab.Factory.ClientAs(Roles.PlatformAdmin, AdminSid, @"GOV\admin");

    private static async Task SetAsync(HttpClient admin, string id, bool enabled)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/compliance/standards/{id}", new { enabled, reason = "We hold customer tax and card records." });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<RiskListView> RisksAsync(string query = "?view=all") => await Ok<RiskListView>(await Admin(_lab).GetAsync("/api/v1/risks" + query));

    private static string Raise(string severity) => severity == "Low" ? "Medium" : "High";

    private static bool In(RiskView r, string share) =>
        r.Where.Equals($@"\\{Host}\{share}", StringComparison.OrdinalIgnoreCase) || r.Where.StartsWith($@"\\{Host}\{share}\", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Without_a_standard_turned_on_no_risk_names_one()
    {
        Assert.NotEmpty(_fixture.Baseline);
        Assert.All(_fixture.Baseline, r => Assert.Empty(r.Standards));
        Assert.All(_fixture.Baseline, r => Assert.Null(r.RaisedFrom));
        Assert.DoesNotContain(_fixture.Baseline, r => r.Rule == RiskRules.ReviewOverdue);
        Assert.DoesNotContain(_fixture.Baseline, r => r.Why.Contains("you turned on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Risks_name_the_standards_whose_data_they_involve_and_rise_one_step()
    {
        var now = (await RisksAsync()).Items;
        foreach (var r in now.Where(r => r.Rule != RiskRules.ReviewOverdue))
        {
            var before = Assert.Single(_fixture.Baseline, b => b.Id == r.Id);
            if (r.Standards.Count == 0)
            {
                Assert.Equal((before.Severity, before.Why), (r.Severity, r.Why));
                Assert.Null(r.RaisedFrom);
                continue;
            }
            Assert.Equal(Raise(before.Severity), r.Severity);
            Assert.Equal(before.Severity == "High" ? null : before.Severity, r.RaisedFrom);
            Assert.StartsWith(before.Why, r.Why, StringComparison.Ordinal);
            Assert.Contains(r.Standards.Count == 1 ? "a standard you turned on" : "standards you turned on", r.Why);
            if (r.RaisedFrom is not null)
            {
                Assert.EndsWith($"so it is rated {r.Severity} rather than {before.Severity}.", r.Why);
            }
        }
        Assert.Equal(_fixture.Baseline.Select(b => b.Id).Order(), now.Where(r => r.Rule != RiskRules.ReviewOverdue).Select(r => r.Id).Order());

        // Public: the SSNs are GLBA data and the card numbers PCI DSS data. The share-wide risk names both, in catalog order.
        var cards = Assert.Single(now, r => r.Rule == RiskRules.OpenSensitive && r.Where == $@"\\{Host}\Public\Cards");
        Assert.Contains("pci-dss", cards.Standards);
        Assert.Contains(now, r => r.Rule == RiskRules.OpenSensitive && In(r, "Public") && r.Standards.Contains("glba"));
        var unowned = Assert.Single(now, r => r.Rule == RiskRules.NoOwner && r.Where == $@"\\{Host}\Public");
        Assert.Equal(["pci-dss", "glba"], unowned.Standards);
        Assert.Contains("PCI DSS and GLBA cover, standards you turned on", unowned.Why);

        // A password is no GLBA or PCI DSS data, so the secrets risk is as it was.
        var secrets = Assert.Single(now, r => r.Rule == RiskRules.Secrets);
        Assert.Empty(secrets.Standards);
        Assert.Null(secrets.RaisedFrom);

        // Ops: the card number there was labelled Internal by a reviewer, so no Ops risk names PCI DSS.
        var ops = now.Where(r => In(r, "Ops")).ToList();
        Assert.Contains(ops, r => r.Rule == RiskRules.OpenSensitive);
        Assert.Contains(ops, r => r.Rule == RiskRules.StaleSensitive);
        Assert.All(ops, r => Assert.Equal(["glba"], r.Standards));

        // Permission entries and coverage are not about where data is.
        Assert.All(now.Where(r => r.Rule is RiskRules.DirectUser or RiskRules.DisabledAccount or RiskRules.DeletedAccount or RiskRules.UnusualOrder
            or RiskRules.OpenChange or RiskRules.NotCovered or RiskRules.NestingLoop), r => Assert.Empty(r.Standards));
    }

    [Fact]
    public async Task An_overdue_or_missing_review_of_data_under_a_standard_is_a_risk()
    {
        var reviews = (await RisksAsync()).Items.Where(r => r.Rule == RiskRules.ReviewOverdue).ToList();

        // HR was reviewed ten days ago; Public has no owner, which is the no-owner risk's.
        Assert.Equal([$@"\\{Host}\Finance", $@"\\{Host}\Ops"], reviews.Select(r => r.Where).Order());

        var ops = reviews.Single(r => In(r, "Ops"));
        Assert.StartsWith("Access review overdue since ", ops.Title);
        Assert.Contains("nate", ops.Who, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["glba"], ops.Standards);
        Assert.Equal(1, ops.Count);
        Assert.Contains("This folder holds data that GLBA covers.", ops.Why);
        Assert.Contains("every three months", ops.Why);
        Assert.Contains("the last review was submitted on ", ops.Why);
        Assert.Null(ops.RaisedFrom);

        var finance = reviews.Single(r => In(r, "Finance"));
        Assert.Equal("Access never reviewed since the owner was assigned", finance.Title);
        Assert.Contains("olivia", finance.Who, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 folder here holds data that GLBA covers.", finance.Why);
        Assert.Contains("every year", finance.Why);
        Assert.Contains("no review has been submitted since they became the owner on ", finance.Why);

        foreach (var r in reviews)
        {
            Assert.Equal(r.Restricted > 0 ? "Medium" : "Low", r.Severity);
        }
    }

    [Fact]
    public async Task A_new_owner_has_one_review_period_before_a_missing_review_is_a_risk()
    {
        var finance = _lab.Roots["Finance"];
        await using var db = _lab.Database.CreateContext();
        var assignment = await db.OwnerAssignments.SingleAsync(a => a.FolderId == finance && a.EndedAt == null);
        var was = assignment.AssignedAt;
        try
        {
            assignment.AssignedAt = DateTimeOffset.UtcNow.AddMonths(-11); // annual, so due in a month
            await db.SaveChangesAsync();
            Assert.DoesNotContain((await RisksAsync()).Items, r => r.Rule == RiskRules.ReviewOverdue && In(r, "Finance"));
        }
        finally
        {
            assignment.AssignedAt = was;
            await db.SaveChangesAsync();
        }
        Assert.Contains((await RisksAsync()).Items, r => r.Rule == RiskRules.ReviewOverdue && In(r, "Finance"));
    }

    [Fact]
    public async Task The_list_narrows_to_one_turned_on_standard()
    {
        var pci = await RisksAsync("?standard=pci-dss");
        Assert.NotEmpty(pci.Items);
        Assert.All(pci.Items, r => Assert.Contains("pci-dss", r.Standards));
        Assert.Contains(pci.Items, r => r.Rule == RiskRules.NoOwner);
        Assert.DoesNotContain(pci.Items, r => In(r, "Ops"));
        // The counts cover only that standard's risks (nothing in this lab is accepted or snoozed).
        Assert.Equal((pci.Items.Count(r => r.Severity == "High"), pci.Items.Count(r => r.Severity == "Medium"), pci.Items.Count(r => r.Severity == "Low")),
            (pci.High, pci.Medium, pci.Low));

        var glbaReviews = await RisksAsync("?standard=GLBA&rule=review-overdue");
        Assert.Equal(2, glbaReviews.Items.Count);

        var off = await Admin(_lab).GetAsync("/api/v1/risks?standard=hipaa");
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("HIPAA is turned off", await off.Content.ReadAsStringAsync());
        var unknown = await Admin(_lab).GetAsync("/api/v1/risks?standard=sox");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Unknown standard", await unknown.Content.ReadAsStringAsync());

        // Anyone who can see risks can filter them; signed-out callers can't.
        var viewer = _lab.Factory.ClientAs(Roles.Viewer, "S-1-5-21-40-50-60-1111", @"GOV\vic");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/risks?standard=pci-dss")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _lab.Factory.CreateClient().GetAsync("/api/v1/risks?standard=pci-dss")).StatusCode);
    }

    [Fact]
    public async Task Turning_a_standard_on_brings_back_a_risk_accepted_at_a_lower_severity()
    {
        var admin = Admin(_lab);
        var risk = Assert.Single((await RisksAsync()).Items, r => r.Rule == RiskRules.NoOwner && r.Where == $@"\\{Host}\Public");
        long? decision = null;
        try
        {
            await SetAsync(admin, "glba", false);
            await SetAsync(admin, "pci-dss", false);
            var plain = Assert.Single((await RisksAsync()).Items, r => r.Id == risk.Id);
            Assert.Equal((_fixture.Baseline.Single(b => b.Id == risk.Id).Severity, null), (plain.Severity, plain.RaisedFrom));
            Assert.DoesNotContain((await RisksAsync()).Items, r => r.Rule == RiskRules.ReviewOverdue);

            var accepted = await admin.PostAsJsonAsync("/api/v1/risks/decisions",
                new { riskId = risk.Id, kind = "Accepted", note = "Public share is being retired this quarter.", until = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
            decision = (await Ok<DecisionView>(accepted)).Id;
            Assert.DoesNotContain((await RisksAsync("")).Items, r => r.Id == risk.Id);

            // Card data under PCI DSS makes it more severe than when it was accepted, so it shows again.
            await SetAsync(admin, "pci-dss", true);
            var back = Assert.Single((await RisksAsync("")).Items, r => r.Id == risk.Id);
            Assert.Equal(("Open", true), (back.Status, back.Decision!.Superseded));
            Assert.Equal(["pci-dss"], back.Standards);
        }
        finally
        {
            if (decision is { } id)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/risks/decisions/{id}")).StatusCode);
            }
            await SetAsync(admin, "glba", true);
            await SetAsync(admin, "pci-dss", true);
        }
    }
}

/// <summary>How a risk names standards and rises one step for them (D136).</summary>
public sealed class RiskSeverityForStandardsTests
{
    private static readonly IReadOnlyList<EffectiveStandard> On =
        ComplianceCatalog.Standards.Where(s => s.Id is "pci-dss" or "glba" or "hipaa").Select(s => ComplianceScope.From(s, null) with { Enabled = true }).ToList();

    private static Risk Risk(RiskSeverity severity) => new("open-sensitive:x", RiskRules.OpenSensitive, severity, "Title", @"\\fs\Data", null, null, null,
        1, 0, null, null, null, null, 1, "Any account in the domain can read files here.", "Fix it.");

    [Theory]
    [InlineData(RiskSeverity.Low, RiskSeverity.Medium)]
    [InlineData(RiskSeverity.Medium, RiskSeverity.High)]
    public void A_risk_with_data_under_a_standard_rises_one_step(RiskSeverity before, RiskSeverity after)
    {
        var r = RiskEngine.Regulated(Risk(before), On, ["pci-dss"]);
        Assert.Equal((after, before), (r.Severity, r.RaisedFrom));
        Assert.Equal(["pci-dss"], r.Standards);
        Assert.Equal($"Any account in the domain can read files here. It involves data PCI DSS covers, a standard you turned on, so it is rated {after} rather than {before}.", r.Why);
    }

    [Fact]
    public void A_high_risk_stays_high_and_says_why()
    {
        var r = RiskEngine.Regulated(Risk(RiskSeverity.High), On, ["glba", "hipaa", "pci-dss"]);
        Assert.Equal((RiskSeverity.High, (RiskSeverity?)null), (r.Severity, r.RaisedFrom));
        Assert.Equal(["hipaa", "pci-dss", "glba"], r.Standards); // catalog order
        Assert.EndsWith("It involves data HIPAA, PCI DSS and GLBA cover, standards you turned on.", r.Why);
    }

    [Fact]
    public void Standards_not_turned_on_are_ignored()
    {
        var r = RiskEngine.Regulated(Risk(RiskSeverity.Low), On, ["state-breach", "nope"]);
        Assert.Equal((RiskSeverity.Low, (RiskSeverity?)null), (r.Severity, r.RaisedFrom));
        Assert.Empty(r.Standards!);
        Assert.Equal("Any account in the domain can read files here.", r.Why);
        Assert.Empty(RiskEngine.Regulated(Risk(RiskSeverity.Low), [], ["pci-dss"]).Standards!);
    }

    [Fact]
    public void Names_join_in_plain_english()
    {
        Assert.Equal("", RiskEngine.JoinNames([]));
        Assert.Equal("HIPAA", RiskEngine.JoinNames(["HIPAA"]));
        Assert.Equal("PCI DSS and GLBA", RiskEngine.JoinNames(["PCI DSS", "GLBA"]));
        Assert.Equal("HIPAA, PCI DSS and GLBA", RiskEngine.JoinNames(["HIPAA", "PCI DSS", "GLBA"]));
    }
}
