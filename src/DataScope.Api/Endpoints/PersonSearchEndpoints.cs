using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Api.People;
using DataScope.Core.Access;
using DataScope.Core.Privacy;
using DataScope.Core.Security;
using DataScope.Persistence;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Find everything about a person (D160): platform admins only, with a reason, and every search in the audit log with
/// who was searched for. A POST, so names and addresses stay out of addresses, proxies and request logs.
/// </summary>
internal static class PersonSearchEndpoints
{
    public sealed record PersonSearchRequest(string? Sid, IReadOnlyList<string?>? Names, IReadOnlyList<string?>? Addresses, string? Reason);

    public static void MapPersonSearch(this RouteGroupBuilder api)
    {
        api.MapPost("/people/search", async (PersonSearchRequest request, ClaimsPrincipal user, PersonLookup lookup, AuditLogWriter audit, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var sid = string.IsNullOrWhiteSpace(request.Sid) ? null : request.Sid.Trim();
            if (sid is not null && !Sid.IsValid(sid))
            {
                errors["sid"] = ["Pick a person from Active Directory, or leave it empty."];
            }
            var names = PersonSearch.Names(request.Names, out var nameProblem);
            if (nameProblem is not null)
            {
                errors["names"] = [nameProblem];
            }
            var addresses = PersonSearch.Addresses(request.Addresses, out var addressProblem);
            if (addressProblem is not null)
            {
                errors["addresses"] = [addressProblem];
            }
            if (PersonSearch.ReasonProblem(request.Reason) is { } reasonProblem)
            {
                errors["reason"] = [reasonProblem];
            }
            if (errors.Count == 0 && sid is null && names!.Count == 0 && addresses!.Count == 0)
            {
                errors["names"] = ["Pick a person from Active Directory, or type a name or an email address."];
            }
            if (errors.Count == 0 && sid is not null && !await lookup.KnownUserAsync(sid, ct))
            {
                errors["sid"] = ["That person isn't in the last Active Directory sync."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var terms = new PersonTerms(sid, names!, addresses!);
            var reason = request.Reason!.Trim();
            // Written before searching, so a search that fails or is cut off is still on record.
            var actor = Actor.From(user);
            await audit.AppendAsync(actor.Sid, actor.Name, "person.search", Target(terms),
                new { Reason = reason, terms.Sid, terms.Names, terms.Addresses }, ct);
            return Results.Ok(await lookup.FindAsync(terms, ct));
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private static string Target(PersonTerms terms) =>
        string.Join(", ", new[] { terms.Sid }.Concat(terms.Names).Concat(terms.Addresses).OfType<string>());
}
