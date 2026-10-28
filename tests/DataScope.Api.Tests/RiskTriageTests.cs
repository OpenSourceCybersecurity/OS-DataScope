using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// Accepting, snoozing and sending risks to IT (D115) over the synthetic governance lab: a Public share anyone can read
/// holding made-up SSNs and no owner, and a Finance file with a made-up password. Every name and number here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class RiskTriageTests : IClassFixture<RiskTriageTests.TriageLab>
{
    private const string Host = "gov-fs01";
    private const string AdminSid = "S-1-5-21-40-50-60-1110", RitaSid = "S-1-5-21-40-50-60-1107", MariaSid = "S-1-5-21-40-50-60-1101";

    private readonly GovernanceApiTests.Lab _lab;

    public RiskTriageTests(TriageLab lab) => _lab = lab.Lab;

    public sealed class TriageLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"Public\staff.csv", "Name,SSN\nCasey Byrne,512-48-3311\nDana Shah,512-48-3312\n");
            Lab.Write(@"Finance\deploy-notes.txt", "Database server: sql01\npassword=Synthetic-Only-1\n");
            await Lab.RescanAsync();
        }

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private sealed record DecisionView(long Id, string Kind, string? Note, DateOnly Until, string Severity, string DecidedByName, bool Superseded);

    private sealed record RiskView(string Id, string Rule, string Severity, string Title, string Where, string Status, DecisionView? Decision);

    private sealed record RuleView(string Id, int High, int Medium, int Low);

    private sealed record RiskListView(List<RiskView> Items, int High, int Medium, int Low, int Accepted, int Snoozed, List<RuleView> Rules);

    private HttpClient Admin => _lab.Factory.ClientAs(Roles.PlatformAdmin, AdminSid, @"GOV\admin");

    private HttpClient Analyst => _lab.Factory.ClientAs(Roles.SecurityAnalyst, RitaSid, @"GOV\rita");

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<RiskListView> RisksAsync(string query = "") => await Ok<RiskListView>(await Admin.GetAsync("/api/v1/risks" + query));

    private async Task<RiskView> RiskAsync(string rule, string view = "all") =>
        Assert.Single((await RisksAsync($"?view={view}&rule={rule}")).Items);

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<HttpResponseMessage> Decide(HttpClient client, string riskId, string kind, string? note, DateOnly? until) =>
        client.PostAsJsonAsync("/api/v1/risks/decisions", new { riskId, kind, note, until });

    [Fact]
    public async Task Accepting_a_risk_takes_it_off_the_list_until_it_is_reopened()
    {
        var before = await RisksAsync();
        var risk = await RiskAsync("open-sensitive", "open");
        Assert.Equal(("High", "Open"), (risk.Severity, risk.Status));

        var decision = await Ok<DecisionView>(await Decide(Analyst, risk.Id, "accepted", "  Public menu share; HR is moving the file this quarter.  ", Today.AddDays(30)));
        Assert.Equal(("Accepted", "High", @"GOV\rita", "Public menu share; HR is moving the file this quarter."),
            (decision.Kind, decision.Severity, decision.DecidedByName, decision.Note));

        var after = await RisksAsync();
        Assert.DoesNotContain(after.Items, r => r.Id == risk.Id);
        Assert.Equal(before.High - 1, after.High);
        Assert.Equal(before.Accepted + 1, after.Accepted);
        var accepted = await RisksAsync("?view=accepted");
        var shown = Assert.Single(accepted.Items, r => r.Id == risk.Id);
        Assert.Equal("Accepted", shown.Status);
        Assert.False(shown.Decision!.Superseded);
        Assert.Contains(accepted.Rules, r => r.Id == "open-sensitive" && r.High == 1);
        Assert.Contains((await RisksAsync("?view=all")).Items, r => r.Id == risk.Id);
        Assert.DoesNotContain((await RisksAsync("?view=snoozed")).Items, r => r.Id == risk.Id);

        // One decision at a time; a second needs a reopen first.
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(Admin, risk.Id, "Snoozed", null, Today.AddDays(5))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/risks/decisions/{decision.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/api/v1/risks/decisions/{decision.Id}")).StatusCode);
        Assert.Contains((await RisksAsync()).Items, r => r.Id == risk.Id && r.Status == "Open" && r.Decision == null);
        Assert.Equal(before.High, (await RisksAsync()).High);

        await using var db = _lab.Database.CreateContext();
        var row = await db.RiskDecisions.SingleAsync(d => d.Id == decision.Id);
        Assert.Equal(@"GOV\admin", row.EndedByName); // kept as history
        var audit = await db.AppAuditLog.Where(a => a.Target == $@"\\{Host}\Public" && a.Action.StartsWith("risk.")).Select(a => a.Action).ToListAsync();
        Assert.Contains("risk.accept", audit);
        Assert.Contains("risk.reopen", audit);
    }

    [Fact]
    public async Task A_snooze_lapses_on_its_date_and_can_be_replaced()
    {
        var risk = await RiskAsync("secrets", "open");
        var snooze = await Ok<DecisionView>(await Decide(Admin, risk.Id, "Snoozed", null, Today.AddDays(7)));
        Assert.Null(snooze.Note);
        Assert.Contains((await RisksAsync("?view=snoozed")).Items, r => r.Id == risk.Id && r.Status == "Snoozed");
        Assert.DoesNotContain((await RisksAsync()).Items, r => r.Id == risk.Id);

        await using (var db = _lab.Database.CreateContext())
        {
            var row = await db.RiskDecisions.SingleAsync(d => d.Id == snooze.Id);
            row.Until = Today.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var list = await RisksAsync();
        Assert.Contains(list.Items, r => r.Id == risk.Id && r.Status == "Open" && r.Decision == null);
        Assert.Equal(0, list.Snoozed);

        // The lapsed snooze does not block a new decision; it is ended and replaced.
        var again = await Ok<DecisionView>(await Decide(Admin, risk.Id, "Snoozed", "Waiting on the vendor", Today.AddDays(90)));
        await using (var db = _lab.Database.CreateContext())
        {
            Assert.NotNull((await db.RiskDecisions.SingleAsync(d => d.Id == snooze.Id)).EndedAt);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/risks/decisions/{again.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_risk_that_becomes_more_severe_shows_again()
    {
        var risk = await RiskAsync("open-sensitive", "open");
        var decision = await Ok<DecisionView>(await Decide(Admin, risk.Id, "Accepted", "Only made-up records live here.", Today.AddDays(200)));
        await using (var db = _lab.Database.CreateContext())
        {
            var row = await db.RiskDecisions.SingleAsync(d => d.Id == decision.Id);
            row.Severity = "Medium"; // as if it was accepted when it was less severe
            await db.SaveChangesAsync();
        }
        var list = await RisksAsync();
        var shown = Assert.Single(list.Items, r => r.Id == risk.Id);
        Assert.Equal("Open", shown.Status);
        Assert.True(shown.Decision!.Superseded);
        Assert.Equal(0, list.Accepted);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/risks/decisions/{decision.Id}")).StatusCode);
    }

    [Fact]
    public async Task Bad_decisions_are_refused()
    {
        var risk = await RiskAsync("secrets", "open");
        async Task Refused(HttpStatusCode code, string? riskId, string? kind, string? note, DateOnly? until) =>
            Assert.Equal(code, (await Admin.PostAsJsonAsync("/api/v1/risks/decisions", new { riskId, kind, note, until })).StatusCode);

        await Refused(HttpStatusCode.BadRequest, risk.Id, "Accepted", null, Today.AddDays(30)); // no reason
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Accepted", "too short", Today.AddDays(30));
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Accepted", "A good enough reason", Today.AddDays(366));
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Accepted", "A good enough reason", null);
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Snoozed", null, Today.AddDays(91));
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Snoozed", null, Today);
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Snoozed", new string('x', 1001), Today.AddDays(3));
        await Refused(HttpStatusCode.BadRequest, risk.Id, "Ignored", null, Today.AddDays(3));
        await Refused(HttpStatusCode.BadRequest, risk.Id, "0", null, Today.AddDays(3));
        await Refused(HttpStatusCode.BadRequest, risk.Id, null, null, Today.AddDays(3));
        await Refused(HttpStatusCode.BadRequest, "", "Snoozed", null, Today.AddDays(3));
        await Refused(HttpStatusCode.BadRequest, new string('x', 401), "Snoozed", null, Today.AddDays(3));
        await Refused(HttpStatusCode.NotFound, "secrets:not-a-real-folder", "Snoozed", null, Today.AddDays(3));
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync("/api/v1/risks/decisions/987654")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/risks?view=hidden")).StatusCode);

        // Data Scope has no task queue: there is nothing to send a risk to.
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsJsonAsync("/api/v1/risks/tasks", new { riskId = risk.Id, note = "Please remove the password from this file." })).StatusCode);

        Assert.Contains((await RisksAsync()).Items, r => r.Id == risk.Id && r.Status == "Open");
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Auditor)]
    [InlineData("")]
    public async Task Only_administrators_and_analysts_can_accept_or_snooze(string role)
    {
        var risk = await RiskAsync("secrets", "open");
        var client = _lab.Factory.ClientAs(role, "S-1-5-21-40-50-60-1111", @"GOV\vic");
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(client, risk.Id, "Snoozed", null, Today.AddDays(3))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/v1/risks/decisions/1")).StatusCode);
        if (role != "")
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/risks?view=accepted")).StatusCode);
        }
        var noHeader = _lab.Factory.ClientAs(Roles.PlatformAdmin, AdminSid, @"GOV\admin", antiForgeryHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(noHeader, risk.Id, "Snoozed", null, Today.AddDays(3))).StatusCode);
        Assert.Contains((await RisksAsync()).Items, r => r.Id == risk.Id && r.Decision == null);
    }
}
