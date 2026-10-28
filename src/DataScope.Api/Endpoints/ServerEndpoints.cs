using DataScope.Core.Settings;
using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

internal static class ServerEndpoints
{
    /// <param name="Kind">FileServer (D141).</param>
    public sealed record ServerDto(Guid Id, string Hostname, string State, DateTimeOffset? LastSeenAt, string? LastError, DateTimeOffset CreatedAt, ScheduleDto Schedule,
        string Kind = nameof(SourceKind.FileServer));

    /// <param name="NextScanAt">The next scheduled start; null when scheduled scans are off or the server is removed.</param>
    /// <param name="MissedAt">A scheduled start that never happened, because the worker service wasn't running in time.</param>
    /// <param name="TimeZone">The Data Scope server's time zone, which the hours are in.</param>
    public sealed record ScheduleDto(
        string Frequency, int StartHour, string? Day, int? StopHour, DateTimeOffset? NextScanAt, DateTimeOffset? MissedAt, string TimeZone, int StartMinute, int? EveryHours,
        string FullScan, string? FullScanDay, DateTimeOffset? NextFullScanAt);

    public sealed record EnrollServerRequest(string? Hostname);

    /// <param name="FullScan">Off, Weekly or Monthly (D130). Left out, the server's full scans stay as they are.</param>
    public sealed record ScheduleRequest(string? Frequency, int? StartHour, string? Day, int? StopHour, int? StartMinute = null, int? EveryHours = null,
        string? FullScan = null, string? FullScanDay = null);

    private static ServerDto ToDto(ManagedServer s, DateTimeOffset now) =>
        new(s.Id, s.Hostname, s.State.ToString(), s.LastSeenAt, s.LastError, s.CreatedAt, Schedule(s, now), s.Kind.ToString());

    /// <summary>Scan schedules use the time zone of the server Data Scope runs on; the worker uses the same one.</summary>
    internal static TimeZoneInfo Zone => TimeZoneInfo.Local;

    private static ScheduleDto Schedule(ManagedServer s, DateTimeOffset now)
    {
        var active = s.State != EnrollmentState.Retired;
        var due = active ? ScanSchedule.LastDue(s.Schedule, now, Zone) : null;
        var missed = due is { } d && now - d > ScanSchedule.CatchUp && !(s.LastScheduledAt >= d) ? due : null;
        return new(s.ScanFrequency.ToString(), s.ScanStartHour, s.ScanDay?.ToString(), s.ScanStopHour,
            active ? ScanSchedule.NextDue(s.Schedule, now, Zone) : null, missed, Zone.DisplayName, s.ScanStartMinute, s.ScanEveryHours,
            s.FullScanFrequency.ToString(), s.FullScanDay?.ToString(), active ? ScanSchedule.NextFullDue(s.Schedule, s.FullScan, now, Zone) : null);
    }

    public static void MapServers(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/servers");

        // Removed servers are left out unless asked for.
        group.MapGet("/", async (bool? includeRemoved, int? limit, int? offset, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var query = db.ManagedServers.AsNoTracking().Where(s => includeRemoved == true || s.State != EnrollmentState.Retired);
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(s => s.Hostname).Skip(o).Take(l).ToListAsync(ct);
            var now = clock.GetUtcNow();
            return new Page<ServerDto>(items.Select(s => ToDto(s, now)).ToList(), l, o, total);
        }).RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/{id:guid}", async (Guid id, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
            await db.ManagedServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } s
                ? Results.Ok(ToDto(s, clock.GetUtcNow()))
                : Results.NotFound()).RequireAuthorization(Policies.ReadPosture);

        // Enrollment only records the server. Its shares are discovered by the first scan. Enrolling a removed
        // server's name brings that server back with its history.
        group.MapPost("/", async (EnrollServerRequest request, ClaimsPrincipal user, DataScopeDbContext db, ServerStore servers, AuditLogWriter audit, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            if (!HostName.TryNormalize(request.Hostname, out var hostname, out var error))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = [error!] });
            }
            var actor = Actor.From(user);
            await using (var restore = await db.Database.BeginTransactionAsync(ct))
            {
                if (await servers.RestoreAsync(hostname, ct) is { } restored)
                {
                    await audit.AppendAsync(actor.Sid, actor.Name, "server.restore", hostname, new { restored.Id }, ct);
                    await restore.CommitAsync(ct);
                    return Results.Ok(ToDto(restored, clock.GetUtcNow()));
                }
            }
            if (await db.ManagedServers.AnyAsync(s => s.Hostname == hostname, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{hostname} is already enrolled.");
            }
            var now = clock.GetUtcNow();
            // New servers get the default schedule from System settings (nightly unless changed, D123); the first
            // scheduled scan is the next one, not one already past.
            var schedule = SystemSettings.NewServerScheduleFrom(k => config[k]);
            if (ScanSchedule.Problem(schedule) is not null)
            {
                schedule = ScanScheduleSettings.NewServer;
            }
            var fullScan = SystemSettings.NewServerFullScanFrom(k => config[k]);
            if (ScanSchedule.Problem(fullScan) is not null)
            {
                fullScan = FullScanSettings.NewServer;
            }
            var server = new ManagedServer
            {
                Id = Guid.CreateVersion7(),
                Hostname = hostname,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBySid = actor.Sid,
                Schedule = schedule,
                LastScheduledAt = now,
                FullScan = fullScan,
                LastFullScheduledAt = now,
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ManagedServers.Add(server);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "server.enroll", hostname, new { server.Id }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/servers/{server.Id}", ToDto(server, now));
        }).RequireAuthorization(Policies.ManagePlatform);

        // When the server is scanned on its own. Saving never starts a scan for a time already past.
        group.MapPut("/{id:guid}/schedule", async (Guid id, ScheduleRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            ScanFrequency frequency = default;
            if (request.Frequency is null || !Enum.TryParse(request.Frequency, ignoreCase: true, out frequency) || !Enum.IsDefined(frequency) || int.TryParse(request.Frequency, out _))
            {
                errors["frequency"] = ["Use Off, Hourly, Daily or Weekly."];
            }
            DayOfWeek? day = null;
            if (request.Day is not null)
            {
                if (!Enum.TryParse<DayOfWeek>(request.Day, ignoreCase: true, out var d) || !Enum.IsDefined(d) || int.TryParse(request.Day, out _))
                {
                    errors["day"] = ["Use a day of the week, for example Saturday."];
                }
                day = d;
            }
            FullScanFrequency? fullScan = null;
            if (request.FullScan is not null)
            {
                if (!Enum.TryParse<FullScanFrequency>(request.FullScan, ignoreCase: true, out var f) || !Enum.IsDefined(f) || int.TryParse(request.FullScan, out _))
                {
                    errors["fullScan"] = ["Use Off, Weekly or Monthly."];
                }
                fullScan = f;
            }
            DayOfWeek? fullScanDay = null;
            if (request.FullScanDay is not null)
            {
                if (!Enum.TryParse<DayOfWeek>(request.FullScanDay, ignoreCase: true, out var d) || !Enum.IsDefined(d) || int.TryParse(request.FullScanDay, out _))
                {
                    errors["fullScanDay"] = ["Use a day of the week, for example Saturday."];
                }
                fullScanDay = d;
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var settings = new ScanScheduleSettings(frequency, request.StartHour ?? ScanScheduleSettings.NewServer.StartHour,
                frequency == ScanFrequency.Weekly ? day : null, request.StopHour, request.StartMinute ?? 0,
                frequency == ScanFrequency.Hourly ? request.EveryHours : null);
            if (ScanSchedule.Problem(settings) is { } problem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["schedule"] = [problem] });
            }
            var full = fullScan is { } fs ? new FullScanSettings(fs, fs == FullScanFrequency.Off ? null : fullScanDay) : null;
            if (full is not null && ScanSchedule.Problem(full) is { } fullProblem)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fullScan"] = [fullProblem] });
            }
            var server = await db.ManagedServers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (server is null)
            {
                return Results.NotFound();
            }
            if (server.State == EnrollmentState.Retired)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{server.Hostname} was removed. Add it again to scan it.");
            }
            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var before = server.Schedule;
            var fullBefore = server.FullScan;
            server.Schedule = settings;
            server.LastScheduledAt = now;
            if (full is not null)
            {
                server.FullScan = full;
                server.LastFullScheduledAt = now;
            }
            server.UpdatedAt = now;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "server.schedule", server.Hostname,
                new { server.Id, Before = Describe(before, fullBefore), After = Describe(settings, server.FullScan) }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToDto(server, now));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Removes the server from scans and every screen. Nothing is deleted; see ServerStore.
        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, DataScopeDbContext db, ServerStore servers, AuditLogWriter audit, CancellationToken ct) =>
        {
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (outcome, removal) = await servers.RemoveAsync(id, actor.Sid, ct);
            switch (outcome)
            {
                case RemoveServerOutcome.NotFound:
                    return Results.NotFound();
                case RemoveServerOutcome.AlreadyRemoved:
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This server was already removed.");
                case RemoveServerOutcome.ScanRunning:
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A scan of this server is running. Cancel it, then remove the server.");
            }
            var hostname = await db.ManagedServers.Where(s => s.Id == id).Select(s => s.Hostname).SingleAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "server.remove", hostname, new { Id = id, removal }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(removal);
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private static object Describe(ScanScheduleSettings s, FullScanSettings f) =>
        new { Frequency = s.Frequency.ToString(), s.StartHour, s.StartMinute, Day = s.Day?.ToString(), s.StopHour, s.EveryHours, FullScan = f.Frequency.ToString(), FullScanDay = f.Day?.ToString() };
}
