using System.Net;
using System.Net.Http.Json;
using DataScope.Api.Health;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>
/// System health: each check from what the services record, on a synthetic installation. Disk space is checked on the
/// temporary folder's drive, so the result doesn't depend on which drives the test machine has.
/// </summary>
[Trait("Category", "Database")]
public sealed class SystemHealthTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _base;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public SystemHealthTests(TestDatabase db)
    {
        _db = db;
        _base = new ApiFactory(db);
        _factory = _base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Notifications:Smtp:Host", "mail.northwind.test");
            b.UseSetting("Notifications:Smtp:From", "datascope@northwind.test");
            b.ConfigureTestServices(s => s.AddScoped(sp => new SystemHealth(sp.GetRequiredService<DataScopeDbContext>(), sp.GetRequiredService<DirectoryStore>(),
                sp.GetRequiredService<IConfiguration>(), TimeProvider.System)
            { Folders = () => [Path.GetTempPath()] }));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }

    private sealed record Item(string Name, string Status, string Detail);
    private sealed record Check(string Id, string Area, string Title, string Status, string Detail, string? Fix, string? Route, List<Item>? Items);
    private sealed record Report(string Overall, DateTimeOffset CheckedAt, List<Check> Checks);

    private HttpClient As(string? roles) => ApiFactory.Configure(_factory.CreateClient(), roles);

    private async Task<Report> Get() => (await As(Roles.Auditor).GetFromJsonAsync<Report>("/api/v1/system-health"))!;

    private static Check Of(Report report, string id) => report.Checks.Single(c => c.Id == id);

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task Reset()
    {
        await using var db = _db.CreateOwnerContext();
        await db.ScanJobs.ExecuteDeleteAsync();
        await db.ActivitySources.ExecuteDeleteAsync();
        await db.ManagedServers.ExecuteDeleteAsync();
        await db.Outbox.ExecuteDeleteAsync();
        await db.DirectoryPrincipals.ExecuteDeleteAsync();
        await db.DirectorySnapshots.ExecuteDeleteAsync();
        await db.ServiceHeartbeats.ExecuteDeleteAsync();
    }

    private async Task Beat(string service, DateTimeOffset seen)
    {
        await using var db = _db.CreateOwnerContext();
        await db.ServiceHeartbeats.Where(b => b.Service == service).ExecuteDeleteAsync();
        db.ServiceHeartbeats.Add(new ServiceHeartbeat { Service = service, Machine = "DS01", Version = "0.2.0-lab.1", StartedAt = seen.AddDays(-1), SeenAt = seen });
        await db.SaveChangesAsync();
    }

    private async Task Backup(DateTimeOffset at)
    {
        await using var db = _db.CreateContext();
        await new AuditLogWriter(db, new FixedClock(at)).AppendAsync("S-1-5-18", "SYSTEM", "backup.create", $"datascope-{at:yyyyMMdd}.dsbackup", new { Bytes = 1 });
    }

    private async Task<Guid> Server(string host, EnrollmentState state = EnrollmentState.Enrolled, ScanFrequency frequency = ScanFrequency.Daily, string? error = null,
        DateTimeOffset? lastCompleted = null, ScanJobState? latest = null)
    {
        await using var db = _db.CreateOwnerContext();
        var server = new ManagedServer
        {
            Id = Guid.NewGuid(),
            Hostname = host,
            State = state,
            LastError = error,
            ScanFrequency = frequency,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedBySid = "S-1",
        };
        db.ManagedServers.Add(server);
        if (lastCompleted is { } done)
        {
            db.ScanJobs.Add(new ScanJob
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                State = ScanJobState.Completed,
                PolicyVersion = "p",
                RequestedBySid = "S-1",
                CreatedAt = done.AddHours(-1),
                UpdatedAt = done,
                FinishedAt = done,
            });
        }
        if (latest is { } state2)
        {
            db.ScanJobs.Add(new ScanJob
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                State = state2,
                PolicyVersion = "p",
                RequestedBySid = "S-1",
                CreatedAt = Now.AddMinutes(-5),
                UpdatedAt = Now,
                LastError = state2 == ScanJobState.Failed ? "The network path was not found." : null,
            });
        }
        await db.SaveChangesAsync();
        return server.Id;
    }

    private async Task Directory(DateTimeOffset? completed, bool laterFailure = false)
    {
        await using var db = _db.CreateOwnerContext();
        if (completed is { } c)
        {
            db.DirectorySnapshots.Add(new DirectorySnapshot { Id = Guid.NewGuid(), State = DirectorySyncState.Completed, Domain = "northwind.test", RequestedAt = c.AddMinutes(-2), CompletedAt = c });
        }
        if (laterFailure)
        {
            db.DirectorySnapshots.Add(new DirectorySnapshot { Id = Guid.NewGuid(), State = DirectorySyncState.Failed, RequestedAt = Now.AddHours(-1) });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_new_installation_says_what_is_missing_and_the_web_service_checks_in_on_its_own()
    {
        await Reset();
        // The web service under test writes its own heartbeat when it starts.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ServiceHeartbeatWriter>().BeatAsync(CancellationToken.None);
        }
        var report = await Get();

        Assert.Equal("Problem", report.Overall);
        Assert.Equal(["services", "scans", "directory", "email", "backups", "disk", "database", "certificates", "activity"], report.Checks.Select(c => c.Id));
        var services = Of(report, "services");
        Assert.Equal("Problem", services.Status);
        Assert.Equal("Problem", services.Items!.Single(i => i.Name == "Scanner service").Status);
        Assert.Contains("Data Scope Scanner", services.Items!.Single(i => i.Name == "Scanner service").Detail);
        Assert.Equal("Ok", services.Items!.Single(i => i.Name == "Web service").Status);
        Assert.Equal("Warning", Of(report, "scans").Status);
        Assert.Equal("Warning", Of(report, "directory").Status);
        Assert.Equal("Problem", Of(report, "backups").Status);
        Assert.Equal("Ok", Of(report, "database").Status);
        Assert.Contains("PostgreSQL", Of(report, "database").Detail);
        Assert.Contains("free of", Assert.Single(Of(report, "disk").Items!).Detail);
        Assert.Equal("Unknown", Of(report, "activity").Status);
    }

    [Fact]
    public async Task A_healthy_installation_is_all_green()
    {
        await Reset();
        await Beat(ServiceHeartbeatWriter.Scanner, Now);
        await Beat(ServiceHeartbeatWriter.Web, Now);
        await Server("health-fs01", lastCompleted: Now.AddHours(-6));
        await Server("health-fs02", frequency: ScanFrequency.Off, lastCompleted: Now.AddDays(-40));
        await Directory(Now.AddHours(-3));
        await Backup(Now.AddHours(-5));

        var report = await Get();

        foreach (var id in new[] { "services", "scans", "directory", "email", "backups", "database" })
        {
            Assert.True(Of(report, id).Status == "Ok", $"{id}: {Of(report, id).Detail}");
        }
        Assert.Contains("scanned by hand", Of(report, "scans").Items!.Single(i => i.Name == "health-fs02").Detail);
        Assert.Contains("northwind.test", Of(report, "directory").Detail);
    }

    [Fact]
    public async Task Broken_and_overdue_parts_are_named_with_what_to_do()
    {
        await Reset();
        await Beat(ServiceHeartbeatWriter.Scanner, Now.AddMinutes(-20));
        await Server("broken-fs01", EnrollmentState.Error, error: "Access is denied listing the shares.");
        await Server("failed-fs02", lastCompleted: Now.AddDays(-1), latest: ScanJobState.Failed);
        await Server("late-fs03", lastCompleted: Now.AddDays(-3));
        await Server("new-fs04", frequency: ScanFrequency.Off);
        await Directory(Now.AddDays(-1), laterFailure: true);
        await Backup(Now.AddDays(-4));
        await using (var db = _db.CreateOwnerContext())
        {
            db.Outbox.Add(new OutboxMessage
            {
                IdempotencyKey = "health-1",
                RecipientSid = "S-1",
                RecipientName = "Avery",
                Subject = "Review",
                Body = "b",
                State = OutboxState.Failed,
                LastError = "535 Authentication failed",
                CreatedAt = Now.AddDays(-1),
                NextAttemptAt = Now,
            });
            await db.SaveChangesAsync();
        }

        var report = await Get();

        Assert.Equal("Problem", report.Overall);
        Assert.Equal("Problem", Of(report, "services").Status);
        Assert.Contains("20 minutes ago", Of(report, "services").Items![0].Detail);
        var scans = Of(report, "scans");
        Assert.Equal("Problem", scans.Status);
        Assert.Equal("setup", scans.Route);
        Assert.Contains("4 of 4", scans.Detail);
        var byName = scans.Items!.ToDictionary(i => i.Name);
        Assert.Equal(("Problem", "Access is denied listing the shares."), (byName["broken-fs01"].Status, byName["broken-fs01"].Detail));
        Assert.Equal(("Problem", "The network path was not found."), (byName["failed-fs02"].Status, byName["failed-fs02"].Detail));
        Assert.Equal("Warning", byName["late-fs03"].Status);
        Assert.Contains("scheduled every day", byName["late-fs03"].Detail);
        Assert.Equal("Warning", byName["new-fs04"].Status);
        // Worst first.
        Assert.Equal("Problem", scans.Items![0].Status);
        Assert.Equal("Warning", Of(report, "directory").Status);
        Assert.Contains("later sync failed", Of(report, "directory").Detail);
        Assert.Equal("Problem", Of(report, "backups").Status);
        Assert.Contains("Task Scheduler", Of(report, "backups").Fix);
        var email = Of(report, "email");
        Assert.Equal("Warning", email.Status);
        Assert.Contains("535 Authentication failed", email.Detail);
        // An email's subject or recipient is never shown here.
        Assert.DoesNotContain("Avery", email.Detail);
    }

    [Fact]
    public async Task Without_a_mail_server_it_says_emails_are_not_sent()
    {
        await Reset();
        var report = (await ApiFactory.Configure(_base.CreateClient(), Roles.PlatformAdmin).GetFromJsonAsync<Report>("/api/v1/system-health"))!;
        Assert.Equal("Warning", Of(report, "email").Status);
        Assert.Contains("No mail server", Of(report, "email").Detail);
    }

    [Fact]
    public async Task File_activity_collection_that_fails_or_falls_behind_is_a_warning()
    {
        await Reset();
        var ok = await Server("act-fs01", lastCompleted: Now);
        var failing = await Server("act-fs02", lastCompleted: Now);
        var behind = await Server("act-fs03", lastCompleted: Now);
        await using (var db = _db.CreateOwnerContext())
        {
            db.ActivitySources.Add(new ActivitySource { ServerId = ok, Channel = "Security", Enabled = true, State = ActivitySourceState.Ok, LastReadAt = Now.AddMinutes(-3) });
            db.ActivitySources.Add(new ActivitySource { ServerId = failing, Channel = "Security", Enabled = true, State = ActivitySourceState.Error, LastError = "RPC server unavailable" });
            db.ActivitySources.Add(new ActivitySource { ServerId = behind, Channel = "Security", Enabled = true, State = ActivitySourceState.Ok, LastReadAt = Now.AddHours(-5) });
            await db.SaveChangesAsync();
        }
        var activity = Of(await Get(), "activity");
        Assert.Equal("Warning", activity.Status);
        var byName = activity.Items!.ToDictionary(i => i.Name);
        Assert.Equal("Ok", byName["act-fs01"].Status);
        Assert.Equal(("Warning", "RPC server unavailable"), (byName["act-fs02"].Status, byName["act-fs02"].Detail));
        Assert.Contains("overwritten", byName["act-fs03"].Detail);
    }

    [Fact]
    public async Task Admins_and_auditors_read_it_and_nobody_else()
    {
        Assert.Equal(HttpStatusCode.OK, (await As(Roles.PlatformAdmin).GetAsync("/api/v1/system-health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(Roles.Auditor).GetAsync("/api/v1/system-health")).StatusCode);
        foreach (var role in new[] { Roles.SecurityAnalyst, Roles.Viewer, Roles.DataOwner })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await As(role).GetAsync("/api/v1/system-health")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync("/api/v1/system-health")).StatusCode);
    }

    [Fact]
    public async Task The_heartbeat_keeps_one_row_per_service()
    {
        await Reset();
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = new ServiceHeartbeatWriter(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ServiceHeartbeatWriter>.Instance, ServiceHeartbeatWriter.Scanner, "9.9.9");
        await writer.BeatAsync(CancellationToken.None);
        await writer.BeatAsync(CancellationToken.None);
        await using var db = _db.CreateContext();
        var row = await db.ServiceHeartbeats.SingleAsync(b => b.Service == ServiceHeartbeatWriter.Scanner);
        Assert.Equal("9.9.9", row.Version);
        Assert.True(DateTimeOffset.UtcNow - row.SeenAt < TimeSpan.FromMinutes(1));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
