using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.Api.Reports;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// The compliance evidence report (D135) over the synthetic governance lab: SSNs in HR (owned, reviewed recently),
/// Public (no owner, any domain user can read) and Ops (permissions not read, review overdue), and card numbers in
/// Public only. Every name and number here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class ComplianceReportTests : IClassFixture<ComplianceReportTests.ComplianceLab>
{
    private const string Host = "gov-fs01";
    private readonly GovernanceApiTests.Lab _lab;

    public ComplianceReportTests(ComplianceLab lab) => _lab = lab.Lab;

    public sealed class ComplianceLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"Public\staff.csv", "Name,SSN\nCasey Byrne,512-48-3311\nDana Shah,512-48-3312\n");
            Lab.Write(@"Public\Cards\orders.txt", "Visa 4111 1111 1111 1111 exp 04/28");
            // In the Ops share's root, whose permissions the scan may not read; last changed four years ago.
            Lab.Write(@"Ops\old-payroll.csv", "Name,SSN\nEli Ward,512-48-3313\n", DateTime.UtcNow.AddYears(-4));
            await Lab.RescanAsync();

            var admin = Lab.Factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-40-50-60-1110", @"GOV\admin");
            foreach (var id in new[] { "glba", "pci-dss" })
            {
                var response = await admin.PutAsJsonAsync($"/api/v1/compliance/standards/{id}", new { enabled = true, reason = "We hold customer tax records." });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            // HR's owner reviewed it ten days ago (quarterly); Ops' owner last reviewed it 120 days ago (also quarterly).
            await using var db = Lab.Database.CreateContext();
            var now = DateTimeOffset.UtcNow;
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

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private sealed record ReportInfoView(string Id, string Title, bool Standard);

    private HttpClient Admin => _lab.Factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-40-50-60-1110", @"GOV\admin");

    private HttpClient Client(string role) => _lab.Factory.ClientAs(role, "S-1-5-21-40-50-60-1111", @"GOV\vic");

    private static async Task<string> TextAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return body;
    }

    /// <summary>The table's rows, by folder, as raw CSV lines.</summary>
    private static Dictionary<string, string> Rows(string csv) =>
        csv.Split("\r\n\r\n", 2)[1].Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .ToDictionary(line => line.Split(',')[2], line => line);

    [Fact]
    public async Task Shows_where_the_data_is_who_can_read_it_and_the_reviews_for_one_standard()
    {
        var response = await Admin.GetAsync("/api/v1/reports/compliance-evidence?standard=glba&format=csv");
        var csv = await TextAsync(response);
        Assert.StartsWith("datascope-compliance-evidence-glba-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("Report,Compliance evidence: GLBA", csv);
        Assert.Contains("Standard,\"GLBA. US Gramm-Leach-Bliley Act Safeguards Rule.", csv);
        Assert.Contains("We hold customer tax records.", csv);
        Assert.Contains("What counts,\"US SSN, Payment card, Bank account, US ITIN, found at Medium confidence or higher.", csv);
        Assert.Contains("Data found,", csv);
        Assert.Contains("Requirement 16 CFR 314.4(c)(1),", csv);
        Assert.Contains("Limitation,\"Data Scope does not assess compliance and never says that data or an organisation is compliant.", csv);
        Assert.Contains("Limitation,\"Not detected: Account numbers written without a routing number nearby", csv);
        // The statement every report carries.
        foreach (var fact in new[] { "Scope,All enrolled file servers", "Latest scan finished,", "Shares covered,", "Access resolution," })
        {
            Assert.Contains(fact, csv);
        }

        var rows = Rows(csv);
        var hr = rows[$@"\\{Host}\HR\Payroll"];
        var pub = rows[$@"\\{Host}\Public"];
        var cards = rows[$@"\\{Host}\Public\Cards"];
        var ops = rows[$@"\\{Host}\Ops"];
        Assert.Equal(4, rows.Count);

        // Public: any domain user can read it through Everyone, and nobody owns it.
        Assert.Contains(",Yes,", pub);
        Assert.Contains("Everyone (everyone in the domain)", pub);
        Assert.Contains("No owner, so not reviewed", pub);
        Assert.Contains("US SSN", pub);
        Assert.Contains("Payment card", cards);

        // HR: only some people, through its own groups, owned by Maria and reviewed ten days ago.
        Assert.Contains(",No,", hr);
        Assert.Contains("HR_Staff", hr);
        Assert.Contains("maria", hr);
        Assert.Contains("Reviewed ", hr);
        Assert.Contains("next due", hr);

        // Ops: the scan was refused its permissions, so who can read it is unknown; its review is overdue.
        Assert.Contains("Unknown (permissions not read)", ops);
        Assert.Contains("Overdue: last reviewed", ops);

        // Open folders first.
        var order = rows.Keys.ToList();
        Assert.True(order.IndexOf($@"\\{Host}\Public") < order.IndexOf($@"\\{Host}\HR\Payroll"));

        Assert.Contains("Who can read it,\"Folders any domain user can read: 2 (2 files). Possibly: 0. Only some people: 1. Unknown, because their permissions were not read: 1.", csv);
        Assert.Contains("Age of the data,\"1 of these files were last changed over a year ago, 1 over three years ago", csv);
        Assert.Contains("Access reviews,Folders whose owner reviewed access within their review frequency: 1 of 4. Overdue: 1. Never reviewed: 0. No owner: 2.", csv);

        // No matched value or the names next to it.
        Assert.DoesNotContain("512-48", csv);
        Assert.DoesNotContain("4111", csv);
        Assert.DoesNotContain("Casey", csv);
    }

    [Fact]
    public async Task Only_the_data_a_standard_covers_is_in_its_report()
    {
        var rows = Rows(await TextAsync(await Admin.GetAsync("/api/v1/reports/compliance-evidence?standard=pci-dss")));
        Assert.Equal([$@"\\{Host}\Public\Cards"], rows.Keys);

        var hrOnly = Rows(await TextAsync(await Admin.GetAsync($"/api/v1/reports/compliance-evidence?standard=glba&shareId={_lab.Shares["HR"]}")));
        Assert.Equal([$@"\\{Host}\HR\Payroll"], hrOnly.Keys);
    }

    [Fact]
    public async Task The_printable_page_names_the_standard_and_runs_nothing()
    {
        var html = await TextAsync(await Admin.GetAsync("/api/v1/reports/compliance-evidence?standard=pci-dss&format=html"));
        Assert.Contains("<h1>Compliance evidence: PCI DSS</h1>", html);
        Assert.Contains("Read this first", html);
        Assert.Contains("PCI DSS 4.0 req. 7.2.4", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_standard_is_required_and_must_be_turned_on()
    {
        async Task<string> Problem(string query)
        {
            var response = await Admin.GetAsync($"/api/v1/reports/{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("errors").GetProperty("standard")[0].GetString()!;
        }

        Assert.Equal("Choose a compliance standard that is turned on.", await Problem("compliance-evidence"));
        Assert.StartsWith("Unknown standard.", await Problem("compliance-evidence?standard=sox"));
        Assert.Equal("HIPAA is turned off. Turn it on in Settings, Compliance standards.", await Problem("compliance-evidence?standard=hipaa"));
        Assert.Equal("This report is not about one compliance standard.", await Problem("sensitive-data?standard=glba"));
    }

    [Fact]
    public async Task Auditors_and_analysts_can_have_it_viewers_and_people_without_a_role_cannot()
    {
        foreach (var role in new[] { Roles.Auditor, Roles.SecurityAnalyst })
        {
            var client = Client(role);
            var list = await (await client.GetAsync("/api/v1/reports")).Content.ReadFromJsonAsync<List<ReportInfoView>>();
            Assert.True(list!.Single(r => r.Id == "compliance-evidence").Standard);
            Assert.False(list!.Single(r => r.Id == "sensitive-data").Standard);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/reports/compliance-evidence?standard=glba")).StatusCode);
        }
        var viewer = Client(Roles.Viewer);
        Assert.DoesNotContain((await (await viewer.GetAsync("/api/v1/reports")).Content.ReadFromJsonAsync<List<ReportInfoView>>())!, r => r.Id == "compliance-evidence");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/reports/compliance-evidence?standard=glba")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs("", "S-1-5-21-40-50-60-1101", @"GOV\maria").GetAsync("/api/v1/reports/compliance-evidence?standard=glba")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _lab.Factory.ClientAs(null).GetAsync("/api/v1/reports/compliance-evidence?standard=glba")).StatusCode);
    }

    [Fact]
    public async Task Each_export_records_the_standard_in_the_audit_log()
    {
        await TextAsync(await Client(Roles.Auditor).GetAsync("/api/v1/reports/compliance-evidence?standard=pci-dss&format=html"));
        await using var db = _lab.Database.CreateContext();
        var entry = await db.AppAuditLog.Where(a => a.Action == "report.export" && a.Target == "Compliance evidence").OrderByDescending(a => a.Sequence).FirstAsync();
        Assert.Equal(@"GOV\vic", entry.ActorName);
        Assert.Contains("\"Standard\":\"pci-dss\"", entry.Details);
    }

    [Theory]
    [InlineData("", "Payroll", true)]
    [InlineData("Payroll", "Payroll", true)]
    [InlineData("payroll", "Payroll\\2024", true)]
    [InlineData("Payroll", "Payroll2024", false)]
    [InlineData("Payroll\\2024", "Payroll", false)]
    public void A_folder_is_owned_through_the_nearest_owned_folder_above_it(string owned, string path, bool expected) =>
        Assert.Equal(expected, ComplianceEvidenceReport.IsAtOrAbove(owned, path));
}
