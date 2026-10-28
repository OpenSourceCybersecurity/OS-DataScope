using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

internal static class ScanJobEndpoints
{
    public sealed record ScanJobDto(
        Guid Id, Guid ServerId, string State, string? RequestedState, string PolicyVersion,
        long ItemsDone, long ItemsTotal, int Attempts, string? LastError,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string Phase, bool Scheduled, bool KeepToWindow,
        string Kind);

    /// <param name="Kind">Quick (the default) or Full (D130).</param>
    public sealed record CreateScanJobRequest(Guid ServerId, string? Kind = null);

    private static ScanJobDto ToDto(ScanJob j) => new(
        j.Id, j.ServerId, j.State.ToString(), j.RequestedState?.ToString(), j.PolicyVersion,
        j.ItemsDone, j.ItemsTotal, j.Attempts, j.LastError, j.CreatedAt, j.StartedAt, j.FinishedAt, Phase(j.Checkpoint), j.Scheduled, j.KeepToWindow, j.Kind.ToString());

    /// <summary>
    /// "walk" while folders are listed (items are folders), "classify" while contents are read (items are files).
    /// Read from the worker's checkpoint. The column is jsonb, which rewrites spacing, so parse rather than match text.
    /// </summary>
    internal static string Phase(string? checkpoint)
    {
        if (string.IsNullOrEmpty(checkpoint))
        {
            return "walk";
        }
        try
        {
            using var doc = JsonDocument.Parse(checkpoint);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("phase", out var phase)
                && phase.ValueKind == JsonValueKind.String
                && phase.GetString() is "classify" ? phase.GetString()! : "walk";
        }
        catch (JsonException)
        {
            return "walk";
        }
    }

    public static void MapScanJobs(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/scan-jobs");

        group.MapGet("/", async (Guid? serverId, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var query = db.ScanJobs.AsNoTracking().Where(j => serverId == null || j.ServerId == serverId);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(j => j.CreatedAt).Skip(o).Take(l).ToListAsync(ct);
            return new Page<ScanJobDto>(items.Select(ToDto).ToList(), l, o, total);
        }).RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/{id:guid}", async (Guid id, DataScopeDbContext db, CancellationToken ct) =>
            await db.ScanJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is { } j
                ? Results.Ok(ToDto(j))
                : Results.NotFound()).RequireAuthorization(Policies.ReadPosture);

        group.MapPost("/", async (CreateScanJobRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var kind = ScanKind.Quick;
            if (request.Kind is not null && (!Enum.TryParse(request.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind) || int.TryParse(request.Kind, out _)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use Quick or Full."] });
            }
            var server = await db.ManagedServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServerId, ct);
            if (server is null)
            {
                return Results.NotFound();
            }
            if (server.State is EnrollmentState.Retired)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{server.Hostname} is retired and cannot be scanned.");
            }
            var active = await db.ScanJobs.AnyAsync(
                j => j.ServerId == server.Id && (j.State == ScanJobState.Queued || j.State == ScanJobState.Running || j.State == ScanJobState.Paused), ct);
            if (active)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{server.Hostname} already has a scan in progress.");
            }
            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var job = new ScanJob
            {
                Id = Guid.CreateVersion7(),
                ServerId = server.Id,
                Kind = kind,
                PolicyVersion = await CustomRuleStore.PolicyVersionAsync(db, ct),
                RequestedBySid = actor.Sid,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ScanJobs.Add(job);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "scan.start", server.Hostname, new { job.Id, Kind = kind.ToString() }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/scan-jobs/{job.Id}", ToDto(job));
        }).RequireAuthorization(Policies.ManagePlatform);

        group.MapPost("/{id:guid}/pause", (Guid id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
            Control(id, ScanJobState.Paused, user, db, audit, clock, ct)).RequireAuthorization(Policies.ManagePlatform);
        group.MapPost("/{id:guid}/resume", (Guid id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
            Control(id, ScanJobState.Queued, user, db, audit, clock, ct)).RequireAuthorization(Policies.ManagePlatform);
        group.MapPost("/{id:guid}/cancel", (Guid id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
            Control(id, ScanJobState.Cancelled, user, db, audit, clock, ct)).RequireAuthorization(Policies.ManagePlatform);
    }

    /// <summary>
    /// Running jobs are owned by a worker, so pause/cancel on them is a request the
    /// worker applies at its next checkpoint. Other jobs change state directly.
    /// </summary>
    private static async Task<IResult> Control(Guid id, ScanJobState target, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct)
    {
        var job = await db.ScanJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }
        if (job.State == ScanJobState.Running && target is ScanJobState.Paused or ScanJobState.Cancelled)
        {
            job.RequestedState = target;
        }
        else if (ScanJobStateMachine.CanTransition(job.State, target))
        {
            job.State = target;
            job.RequestedState = null;
            if (ScanJobStateMachine.IsTerminal(target))
            {
                job.FinishedAt = clock.GetUtcNow();
            }
        }
        else
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"A {job.State.ToString().ToLowerInvariant()} scan cannot be {Verb(target)}.");
        }
        job.UpdatedAt = clock.GetUtcNow();
        // Someone is now driving this scan by hand, so the schedule no longer pauses or continues it.
        job.KeepToWindow = false;
        if (job.State == ScanJobState.Queued && job.LastError == ScanJobStore.WindowNote)
        {
            job.LastError = null;
        }
        var actor = Actor.From(user);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            if (ScanJobStateMachine.IsTerminal(job.State))
            {
                await db.ScanFrontier.Where(f => f.JobId == job.Id).ExecuteDeleteAsync(ct);
            }
            await audit.AppendAsync(actor.Sid, actor.Name, $"scan.{Verb(target)}", job.Id.ToString(), null, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The scan changed while you were updating it. Refresh and try again.");
        }
        return Results.Ok(ToDto(job));
    }

    private static string Verb(ScanJobState target) => target switch
    {
        ScanJobState.Paused => "paused",
        ScanJobState.Queued => "resumed",
        _ => "cancelled",
    };
}
