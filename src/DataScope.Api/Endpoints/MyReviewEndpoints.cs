using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Api.Reviews;
using DataScope.Core.Access;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// The owner portal. Any signed-in person can call these, but each sees only the reviews that are theirs: as owner, as
/// the person the owner handed it to, or as backup owner once a review is escalated. Anything else is "not found", so
/// the portal does not reveal which reviews exist.
/// </summary>
internal static class MyReviewEndpoints
{
    public sealed record PersonDto(string Sid, string Name, string? Department, string? Title);

    public sealed record OwnedDto(Guid FolderId, Guid ShareId, string Resource, string Role, string ReviewFrequency, string? Purpose, DateTimeOffset AssignedAt);

    public static void MapMyReviews(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/my").RequireAuthorization();

        group.MapGet("/reviews", async (ClaimsPrincipal user, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var sid = Actor.From(user).Sid;
            return Results.Ok(await ReviewViews.SummariesAsync(db, ReviewViews.Mine(db, sid), sid, clock.GetUtcNow(), ct));
        });

        group.MapGet("/reviews/{id:guid}", async (Guid id, ClaimsPrincipal user, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var sid = Actor.From(user).Sid;
            if (!await ReviewViews.Mine(db, sid).AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }
            return Results.Ok(await ReviewViews.DetailAsync(db, id, sid, viewerIsReviewer: true, clock.GetUtcNow(), ct));
        });

        group.MapGet("/reviews/{id:guid}/items/{itemId:long}/users", async (
            Guid id, long itemId, string? search, int? limit, int? offset, ClaimsPrincipal user, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (!await ReviewViews.Mine(db, Actor.From(user).Sid).AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound();
            }
            if (search is { Length: > 200 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Search for at most 200 characters."] });
            }
            var (l, o) = Paging.Clamp(limit, offset);
            return Results.Ok(await ReviewViews.ItemUsersAsync(db, id, itemId, search, l, o, ct, hideListed: true));
        });

        group.MapPost("/reviews/{id:guid}/decisions", (Guid id, DecisionsRequest request, ClaimsPrincipal user, ReviewActions actions, CancellationToken ct) =>
            actions.DecideAsync(id, request, Actor.From(user), ct));

        group.MapPost("/reviews/{id:guid}/submit", (Guid id, SubmitRequest request, ClaimsPrincipal user, ReviewActions actions, CancellationToken ct) =>
            actions.SubmitAsync(id, request, Actor.From(user), ct));

        group.MapPost("/reviews/{id:guid}/delegate", (Guid id, DelegateRequest request, ClaimsPrincipal user, ReviewActions actions, CancellationToken ct) =>
            actions.DelegateAsync(id, request, Actor.From(user), asAdministrator: false, ct));

        // Owners pick a person to hand a review to. Only people who own something or have a review may search, and only
        // enabled users' names come back: what any domain user could already read from Active Directory.
        group.MapGet("/people", async (string? search, ClaimsPrincipal user, DataScopeDbContext db, DirectoryStore directory, CancellationToken ct) =>
        {
            var sid = Actor.From(user).Sid.ToUpperInvariant();
            var term = search?.Trim() ?? "";
            if (term.Length is < 2 or > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Type 2 to 100 characters of a name."] });
            }
            var allowed = await db.OwnerAssignments.AnyAsync(a => a.EndedAt == null && (a.OwnerSid == sid || a.BackupSid == sid), ct)
                || await ReviewViews.Mine(db, sid).AnyAsync(ct);
            if (!allowed)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only data owners can look people up here.");
            }
            var snapshot = await directory.LatestCompletedAsync(ct);
            if (snapshot is null)
            {
                return Results.Ok(Array.Empty<PersonDto>());
            }
            var like = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var people = await db.DirectoryPrincipals.AsNoTracking()
                .Where(p => p.SnapshotId == snapshot.Id && p.Kind == PrincipalKind.User && p.Enabled
                    && (EF.Functions.ILike(p.Name, like) || (p.DisplayName != null && EF.Functions.ILike(p.DisplayName, like))))
                .OrderBy(p => p.DisplayName ?? p.Name).Take(20)
                .Select(p => new { p.Sid, p.Name, p.DisplayName, p.Department, p.Title })
                .ToListAsync(ct);
            return Results.Ok(people.Select(p => new PersonDto(p.Sid, p.DisplayName is { Length: > 0 } d ? $"{d} ({p.Name})" : p.Name, p.Department, p.Title)).ToList());
        });

        group.MapGet("/owned", async (ClaimsPrincipal user, DataScopeDbContext db, CancellationToken ct) =>
        {
            var sid = Actor.From(user).Sid.ToUpperInvariant();
            var rows = await (from a in db.OwnerAssignments.AsNoTracking()
                              join f in db.Folders on a.FolderId equals f.Id
                              join s in db.Shares on a.ShareId equals s.Id
                              join m in db.ManagedServers on s.ServerId equals m.Id
                              where a.EndedAt == null && (a.OwnerSid == sid || a.BackupSid == sid)
                              orderby m.Hostname, s.Name, f.Path
                              select new { a.FolderId, a.ShareId, m.Hostname, Share = s.Name, f.Path, a.OwnerSid, a.ReviewFrequency, a.Purpose, a.AssignedAt })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new OwnedDto(
                r.FolderId, r.ShareId, CampaignBuilder.Unc(r.Hostname, r.Share, r.Path), r.OwnerSid == sid ? "Owner" : "Backup",
                r.ReviewFrequency.ToString(), r.Purpose, r.AssignedAt)).ToList());
        });
    }
}
