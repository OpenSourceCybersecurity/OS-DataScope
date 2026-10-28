using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Activity;
using DataScope.Core.Security;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

[Trait("Category", "Database")]
public sealed class ActivityAlertApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ActivityAlertApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record AlertView(long Id, string Rule, string Title, string Severity, string State, Guid ServerId, string Hostname, string? PrincipalSid,
        string? PrincipalName, DateTimeOffset FirstEventAt, DateTimeOffset LastEventAt, int EventCount, string Description, string? Operation, string? Outcome,
        string? AcknowledgedByName, DateTimeOffset? AcknowledgedAt, string? Note);

    private sealed record ListView(List<AlertView> Items, int Open);

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin);

    /// <summary>A server with one open mass-delete alert and one acknowledged permission-change alert.</summary>
    private async Task<(string Hostname, long Open, long Acknowledged)> Seed()
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = $"fs-alerts-{Guid.NewGuid():N}"[..20], CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var open = new ActivityAlert
        {
            Rule = ActivityAlertRule.MassDelete,
            Severity = ActivityAlertSeverity.High,
            State = ActivityAlertState.Open,
            ServerId = server.Id,
            PrincipalSid = "S-1-5-21-1-2-3-1104",
            PrincipalName = @"NORTHWIND\jordan",
            FirstEventAt = now.AddMinutes(-14),
            LastEventAt = now.AddMinutes(-10),
            EventCount = 1200,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var acknowledged = new ActivityAlert
        {
            Rule = ActivityAlertRule.PermissionChange,
            Severity = ActivityAlertSeverity.Medium,
            State = ActivityAlertState.Acknowledged,
            ServerId = server.Id,
            PrincipalSid = "S-1-5-21-1-2-3-1105",
            PrincipalName = @"NORTHWIND\maria",
            FirstEventAt = now.AddHours(-3),
            LastEventAt = now.AddHours(-3),
            EventCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            AcknowledgedBySid = "S-1",
            AcknowledgedByName = "admin",
            AcknowledgedAt = now,
            Note = "Planned change",
        };
        db.AddRange(server, open, acknowledged);
        await db.SaveChangesAsync();
        return (server.Hostname, open.Id, acknowledged.Id);
    }

    [Fact]
    public async Task Alerts_are_read_by_evidence_roles_and_triaged_by_admins_and_analysts()
    {
        var (_, open, _) = await Seed();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/api/v1/activity/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/activity/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(Roles.Auditor).GetAsync("/api/v1/activity/alerts")).StatusCode);

        var url = $"/api/v1/activity/alerts/{open}";
        var body = new { state = "Acknowledged" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Auditor).PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Viewer).PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.SecurityAnalyst, antiForgeryHeader: false).PutAsJsonAsync(url, body)).StatusCode);

        await using var db = _db.CreateContext();
        Assert.Equal(ActivityAlertState.Open, (await db.ActivityAlerts.SingleAsync(a => a.Id == open)).State);
    }

    [Fact]
    public async Task The_list_is_newest_first_filters_by_state_and_counts_open_alerts()
    {
        var (hostname, open, acknowledged) = await Seed();

        var openOnly = await Admin.GetFromJsonAsync<ListView>("/api/v1/activity/alerts?state=Open&limit=200");
        Assert.Contains(openOnly!.Items, a => a.Id == open);
        Assert.DoesNotContain(openOnly.Items, a => a.Id == acknowledged);
        Assert.All(openOnly.Items, a => Assert.Equal("Open", a.State));
        Assert.Equal(openOnly.Items.Count, openOnly.Open);

        var all = await Admin.GetFromJsonAsync<ListView>("/api/v1/activity/alerts?state=All&limit=200");
        var mine = all!.Items.Where(a => a.Hostname == hostname).ToList();
        Assert.Equal([open, acknowledged], mine.Select(a => a.Id));
        var alert = mine[0];
        Assert.Equal(("MassDelete", "High", 1200, @"NORTHWIND\jordan"), (alert.Rule, alert.Severity, alert.EventCount, alert.PrincipalName));
        Assert.Equal(("Deleted", (string?)null), (alert.Operation, alert.Outcome));
        Assert.Contains("1,200", alert.Description, StringComparison.Ordinal);
        Assert.Equal(("Planned change", "admin"), (mine[1].Note, mine[1].AcknowledgedByName));

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/activity/alerts?state=Closed")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/activity/alerts?state=open")).StatusCode);
    }

    [Fact]
    public async Task Acknowledging_and_reopening_are_audited_with_the_note()
    {
        var (hostname, open, _) = await Seed();
        var analyst = _factory.ClientAs(Roles.SecurityAnalyst);
        var url = $"/api/v1/activity/alerts/{open}";

        var response = await analyst.PutAsJsonAsync(url, new { state = "Acknowledged", note = "Jordan archived the 2019 folders, approved by finance." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var alert = (await response.Content.ReadFromJsonAsync<AlertView>())!;
        Assert.Equal("Acknowledged", alert.State);
        Assert.NotNull(alert.AcknowledgedAt);
        Assert.Equal("Jordan archived the 2019 folders, approved by finance.", alert.Note);

        response = await Admin.PutAsJsonAsync(url, new { state = "Open" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Open", (await response.Content.ReadFromJsonAsync<AlertView>())!.State);

        await using var db = _db.CreateContext();
        var entries = await db.AppAuditLog.Where(a => a.Target == hostname).OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync();
        Assert.Equal(["activity.alert.acknowledge", "activity.alert.reopen"], entries);
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_change_nothing()
    {
        var (hostname, open, _) = await Seed();
        var url = $"/api/v1/activity/alerts/{open}";
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { state = "Closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { state = "3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { state = "Acknowledged", note = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync("/api/v1/activity/alerts/999999999", new { state = "Acknowledged" })).StatusCode);

        await using var db = _db.CreateContext();
        Assert.Equal(ActivityAlertState.Open, (await db.ActivityAlerts.SingleAsync(a => a.Id == open)).State);
        Assert.False(await db.AppAuditLog.AnyAsync(a => a.Target == hostname));
    }
}
