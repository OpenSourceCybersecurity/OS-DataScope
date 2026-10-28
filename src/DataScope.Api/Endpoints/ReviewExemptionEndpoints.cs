using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Core.Access;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// The administrators' list of users and groups that need no decision in access reviews, such as IT admin groups and
/// backup accounts. It applies to campaigns created after a change; reviews already frozen keep what they were given.
/// </summary>
internal static class ReviewExemptionEndpoints
{
    public sealed record ExemptionDto(long Id, string Sid, string Name, string Kind, string Reason, string AddedByName, DateTimeOffset AddedAt);

    public sealed record AddExemptionRequest(string? Sid, string? Reason);

    private const int MaxEntries = 500;

    public static void MapReviewExemptions(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/review-exemptions").RequireAuthorization(Policies.ReadReviews);

        group.MapGet("/", async (DataScopeDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ReviewExemptions.AsNoTracking().Where(x => x.EndedAt == null).OrderBy(x => x.Name).ThenBy(x => x.Sid)
                .Select(x => new ExemptionDto(x.Id, x.Sid, x.Name, x.Kind, x.Reason, x.AddedByName, x.AddedAt)).ToListAsync(ct)));

        group.MapPost("/", async (AddExemptionRequest request, ClaimsPrincipal user, DataScopeDbContext db, DirectoryStore directory, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var sid = request.Sid?.Trim().ToUpperInvariant() ?? "";
            var reason = request.Reason?.Trim() ?? "";
            if (!Sid.IsValid(sid))
            {
                return Invalid("sid", "Choose a user or group from Active Directory.");
            }
            if (reason.Length is < 3 or > 500)
            {
                return Invalid("reason", "Say why in 3 to 500 characters, for example \"IT administrators; access managed by IT\".");
            }
            var snapshot = await directory.LatestCompletedAsync(ct);
            var principal = snapshot is null ? null : await db.DirectoryPrincipals.AsNoTracking()
                .Where(p => p.SnapshotId == snapshot.Id && p.Sid == sid && (p.Kind == PrincipalKind.User || p.Kind == PrincipalKind.Group))
                .Select(p => new { p.Name, p.DisplayName, p.Kind }).SingleOrDefaultAsync(ct);
            if (principal is null)
            {
                return Invalid("sid", "That account is not a user or group in the latest Active Directory sync.");
            }
            if (await db.ReviewExemptions.AnyAsync(x => x.Sid == sid && x.EndedAt == null, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{principal.Name} is already on the list.");
            }
            if (await db.ReviewExemptions.CountAsync(x => x.EndedAt == null, ct) >= MaxEntries)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"The list holds at most {MaxEntries} accounts. Use a group instead of many people.");
            }
            var actor = Actor.From(user);
            var name = principal.DisplayName is { Length: > 0 } d && principal.Kind == PrincipalKind.User ? $"{d} ({principal.Name})" : principal.Name;
            var entry = new ReviewExemption
            {
                Sid = sid,
                Name = name.Length > 256 ? name[..256] : name,
                Kind = principal.Kind.ToString(),
                Reason = reason,
                AddedBySid = actor.Sid,
                AddedByName = actor.Name,
                AddedAt = clock.GetUtcNow(),
            };
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ReviewExemptions.Add(entry);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "review.exemption.add", entry.Name, new { entry.Id, entry.Sid, entry.Kind, entry.Reason }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/review-exemptions/{entry.Id}",
                new ExemptionDto(entry.Id, entry.Sid, entry.Name, entry.Kind, entry.Reason, entry.AddedByName, entry.AddedAt));
        }).RequireAuthorization(Policies.ManageReviews);

        // Ends the entry; the row stays so the list's history can be audited.
        group.MapDelete("/{id:long}", async (long id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var entry = await db.ReviewExemptions.FirstOrDefaultAsync(x => x.Id == id && x.EndedAt == null, ct);
            if (entry is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            entry.EndedAt = clock.GetUtcNow();
            entry.EndedBySid = actor.Sid;
            entry.EndedByName = actor.Name;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "review.exemption.remove", entry.Name, new { entry.Id, entry.Sid }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageReviews);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
