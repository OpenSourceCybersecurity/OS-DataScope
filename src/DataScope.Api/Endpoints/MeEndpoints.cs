using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Api.Hosting;
using DataScope.Api.Reviews;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

internal static class MeEndpoints
{
    /// <param name="OpenReviews">Reviews waiting for this person: as owner, delegate, or escalated backup owner.</param>
    /// <param name="Owns">Shares and folders this person owns or backs up.</param>
    public sealed record MeDto(string Name, string Sid, IReadOnlyList<string> Roles, string Version, int OpenReviews, int Owns);

    /// <summary>What the sign-in page needs before anyone signs in. Nothing about the customer, their data or the version.</summary>
    public sealed record SignInInfoDto(string Product, string SignIn);

    private static readonly string Version = ProductVersion.Current;

    public static void MapMe(this RouteGroupBuilder api)
    {
        // Lets the web UI show its own sign-in page instead of starting Windows sign-in (and a browser password prompt)
        // the moment the page opens.
        api.MapGet("/sign-in", () => new SignInInfoDto("Data Scope", "Windows")).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal user, DataScopeDbContext db, CancellationToken ct) =>
        {
            var actor = Actor.From(user);
            var sid = actor.Sid.ToUpperInvariant();
            var open = await (from r in ReviewViews.Mine(db, actor.Sid)
                              join c in db.ReviewCampaigns on r.CampaignId equals c.Id
                              where c.State == CampaignState.Open && r.State != ReviewState.Submitted
                              select r.Id).CountAsync(ct);
            var owns = await db.OwnerAssignments.CountAsync(a => a.EndedAt == null && (a.OwnerSid == sid || a.BackupSid == sid), ct);
            var roles = Roles.All.Where(user.IsInRole).ToList();
            return new MeDto(actor.Name, actor.Sid, roles, Version, open, owns);
        });
    }
}
