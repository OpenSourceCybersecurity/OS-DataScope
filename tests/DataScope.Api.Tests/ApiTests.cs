using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Audit;
using DataScope.Core.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Api.Tests;

[Trait("Category", "Database")]
public sealed class ApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record ServerDto(Guid Id, string Hostname, string State);
    private sealed record JobDto(Guid Id, Guid ServerId, string State, string? RequestedState, string PolicyVersion);
    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);

    private async Task<ServerDto> Enroll(string hostname)
    {
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync("/api/v1/servers", new { hostname });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ServerDto>())!;
    }

    [Fact]
    public async Task Health_is_anonymous_and_checks_the_database()
    {
        var res = await _factory.ClientAs(null).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_sign_in_page_learns_it_is_on_a_server_without_signing_in_or_seeing_anything_else()
    {
        var res = await _factory.ClientAs(null).GetAsync("/api/v1/sign-in");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(res.Headers.Contains("WWW-Authenticate"));
        Assert.Equal("{\"product\":\"Data Scope\",\"signIn\":\"Windows\"}", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Api_requires_sign_in()
    {
        var res = await _factory.ClientAs(null).GetAsync("/api/v1/servers");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_routes_are_not_served_anonymously()
    {
        var res = await _factory.ClientAs(null).GetAsync("/api/v1/does-not-exist");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Viewer)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.DataOwner)]
    public async Task Only_platform_admins_can_enroll_servers(string role)
    {
        var res = await _factory.ClientAs(role).PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-denied" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await using var db = _db.CreateContext();
        Assert.False(await db.ManagedServers.AnyAsync(s => s.Hostname == "fs-denied"));
    }

    [Fact]
    public async Task Changes_without_the_anti_forgery_header_are_refused()
    {
        var client = _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false);
        var res = await client.PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-csrf" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("X-DataScope-Client", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = _db.CreateContext();
        Assert.False(await db.ManagedServers.AnyAsync(s => s.Hostname == "fs-csrf"));

        // Reads don't need it, and anonymous callers are still told to sign in first.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/servers")).StatusCode);
        var anonymous = await _factory.ClientAs(null, antiForgeryHeader: false).PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-csrf" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Bodyless_actions_also_need_the_anti_forgery_header()
    {
        var server = await Enroll("fs-csrf-2");
        var job = await (await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id }))
            .Content.ReadFromJsonAsync<JobDto>();
        var res = await _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false).PostAsync($"/api/v1/scan-jobs/{job!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await using var db = _db.CreateContext();
        var stored = await db.ScanJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(Core.ScanJobs.ScanJobState.Queued, stored.State);
        Assert.Null(stored.RequestedState);
    }

    [Fact]
    public async Task Data_owners_cannot_read_the_server_inventory()
    {
        var res = await _factory.ClientAs(Roles.DataOwner).GetAsync("/api/v1/servers");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_callers_roles()
    {
        var me = await _factory.ClientAs($"{Roles.Auditor},{Roles.Viewer}").GetFromJsonAsync<Dictionary<string, object>>("/api/v1/me");
        Assert.Equal("NW\\tester", me!["name"].ToString());
        Assert.Contains("Auditor", me["roles"].ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(me["version"].ToString()));
        Assert.DoesNotContain("+", me["version"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_enrolls_a_server_and_it_is_audited()
    {
        var server = await Enroll("NW-FS01.Northwind.Local");
        Assert.Equal("nw-fs01.northwind.local", server.Hostname);
        Assert.Equal("Pending", server.State);

        var list = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<PageDto<ServerDto>>("/api/v1/servers?limit=1000");
        Assert.Contains(list!.Items, s => s.Id == server.Id);
        Assert.Equal(200, list.Limit);

        await using var db = _db.CreateContext();
        var entry = await db.AppAuditLog.SingleAsync(a => a.Action == "server.enroll" && a.Target == "nw-fs01.northwind.local");
        Assert.Equal("NW\\tester", entry.ActorName);

        var verify = await _factory.ClientAs(Roles.Auditor).GetFromJsonAsync<AuditChain.VerifyResult>("/api/v1/audit-log/verify");
        Assert.True(verify!.Valid, verify.Message);
    }

    [Theory]
    [InlineData(@"\\evil\share")]
    [InlineData("fs01:445")]
    [InlineData("10.20.1.21")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("")]
    public async Task Enrollment_rejects_unsafe_host_names(string hostname)
    {
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync("/api/v1/servers", new { hostname });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Duplicate_enrollment_conflicts()
    {
        await Enroll("fs-dup");
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync("/api/v1/servers", new { hostname = "FS-DUP" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Scan_job_lifecycle_follows_the_state_machine()
    {
        var server = await Enroll("fs-jobs");
        var admin = _factory.ClientAs(Roles.PlatformAdmin);

        var created = await admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal("Queued", job.State);
        Assert.Equal(DataScope.Core.Classification.ClassificationPolicy.CurrentVersion, job.PolicyVersion);

        var second = await admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var pause = await admin.PostAsync($"/api/v1/scan-jobs/{job.Id}/pause", null);
        Assert.Equal(HttpStatusCode.Conflict, pause.StatusCode); // a queued job cannot be paused

        var cancel = await admin.PostAsync($"/api/v1/scan-jobs/{job.Id}/cancel", null);
        Assert.Equal("Cancelled", (await cancel.Content.ReadFromJsonAsync<JobDto>())!.State);

        var resume = await admin.PostAsync($"/api/v1/scan-jobs/{job.Id}/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode); // cancelled is terminal
    }

    [Fact]
    public async Task Pause_on_a_running_job_is_a_request_for_the_worker()
    {
        var server = await Enroll("fs-running");
        var admin = _factory.ClientAs(Roles.PlatformAdmin);
        var job = (await (await admin.PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = server.Id })).Content.ReadFromJsonAsync<JobDto>())!;
        await using (var db = _db.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, DataScope.Core.ScanJobs.ScanJobState.Running));
        }
        var paused = (await (await admin.PostAsync($"/api/v1/scan-jobs/{job.Id}/pause", null)).Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal("Running", paused.State);
        Assert.Equal("Paused", paused.RequestedState);
    }

    [Fact]
    public async Task Scanning_an_unknown_server_is_not_found()
    {
        var res = await _factory.ClientAs(Roles.PlatformAdmin).PostAsJsonAsync("/api/v1/scan-jobs", new { serverId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Audit_log_is_readable_by_auditors_only()
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(Roles.Auditor).GetAsync("/api/v1/audit-log")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.SecurityAnalyst).GetAsync("/api/v1/audit-log")).StatusCode);
    }

    [Fact]
    public async Task Audit_log_rejects_updates_and_detects_tampering()
    {
        await Enroll("fs-audit");
        // The services' login has no right to change the log at all.
        await using (var runtime = _db.CreateContext())
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.Database.ExecuteSqlRawAsync("UPDATE app_audit_log SET target = 'forged'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        // Even the owner login meets the trigger.
        await using var db = _db.CreateOwnerContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE app_audit_log SET target = 'forged'"));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);

        // Someone with database owner rights can bypass the trigger; the hash chain still exposes it.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app_audit_log DISABLE TRIGGER app_audit_log_no_update_delete");
        await db.Database.ExecuteSqlRawAsync("UPDATE app_audit_log SET target = 'forged' WHERE action = 'server.enroll' AND target = 'fs-audit'");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app_audit_log ENABLE TRIGGER app_audit_log_no_update_delete");
        var verify = await _factory.ClientAs(Roles.Auditor).GetFromJsonAsync<AuditChain.VerifyResult>("/api/v1/audit-log/verify");
        Assert.False(verify!.Valid);

        // Restore so other tests in this class see a valid chain.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app_audit_log DISABLE TRIGGER app_audit_log_no_update_delete");
        await db.Database.ExecuteSqlRawAsync("UPDATE app_audit_log SET target = 'fs-audit' WHERE target = 'forged'");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app_audit_log ENABLE TRIGGER app_audit_log_no_update_delete");
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var res = await _factory.ClientAs(null).GetAsync("/health");
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        // No inline script or style anywhere in the UI.
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.False(res.Headers.Contains("Server"));

        // API answers are never cached.
        var api = await _factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.True(api.Headers.CacheControl!.NoStore);
    }
}
