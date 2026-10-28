using System.Security.Claims;
using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Core.Access;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Active Directory as Data Scope last read it. Reads only: Data Scope never changes users, groups or memberships.
/// A sync is requested here and run by the worker.
/// </summary>
internal static class DirectoryEndpoints
{
    public sealed record SnapshotDto(
        Guid Id, string State, string? Domain, string? ServerName, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, int Attempts, int Users, int DisabledUsers, int Groups, int Computers, int ForeignPrincipals,
        int Memberships, int UnresolvedMembers, int Cycles, bool Truncated, IReadOnlyList<string> Warnings, string? LastError);

    public sealed record DirectoryStatusDto(SnapshotDto? Latest, SnapshotDto? Pending, SnapshotDto? LastFailure);

    public sealed record PrincipalDto(
        string Sid, string Kind, string Name, string? DisplayName, bool Enabled, bool IsSecurityGroup, string? Scope,
        string? UserPrincipalName, string? Department, string? Title);

    /// <param name="MemberOf">Groups the account certainly belongs to, directly or through nesting (users only).</param>
    /// <param name="MaybeMemberOf">Groups whose membership could not be resolved for certain.</param>
    /// <param name="Members">Direct members (groups only), at most <see cref="MaxMembers"/>.</param>
    /// <param name="Manager">Who the user reports to, from the AD <c>manager</c> attribute.</param>
    public sealed record PrincipalDetailDto(
        PrincipalDto Principal, string DistinguishedName, string? Mail, DateTimeOffset? LastLogon, DateTimeOffset? WhenChanged,
        IReadOnlyList<string> SidHistory, bool MembersIncomplete, IReadOnlyList<TrusteeDto> DirectGroups,
        IReadOnlyList<TrusteeDto> MemberOf, IReadOnlyList<TrusteeDto> MaybeMemberOf, IReadOnlyList<TrusteeDto> Members,
        int DirectMemberCount, int? UsersCertain, int? UsersPossible, bool InCycle, Guid SnapshotId, DateTimeOffset? SnapshotAt,
        TrusteeDto? Manager);

    private const int MaxMembers = 500;

    public static SnapshotDto ToDto(DirectorySnapshot s) => new(
        s.Id, s.State.ToString(), s.Domain, s.ServerName, s.RequestedAt, s.StartedAt, s.CompletedAt, s.Attempts, s.Users,
        s.DisabledUsers, s.Groups, s.Computers, s.ForeignPrincipals, s.Memberships, s.UnresolvedMembers, s.Cycles, s.Truncated,
        s.Warnings, s.LastError);

    public static void MapDirectory(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/directory").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/", async (DataScopeDbContext db, DirectoryStore store, CancellationToken ct) =>
        {
            var latest = await store.LatestCompletedAsync(ct);
            var pending = await db.DirectorySnapshots.AsNoTracking()
                .Where(s => s.State == DirectorySyncState.Queued || s.State == DirectorySyncState.Running)
                .OrderBy(s => s.RequestedAt).FirstOrDefaultAsync(ct);
            var failed = await db.DirectorySnapshots.AsNoTracking()
                .Where(s => s.State == DirectorySyncState.Failed && (latest == null || s.RequestedAt > latest.RequestedAt))
                .OrderByDescending(s => s.RequestedAt).FirstOrDefaultAsync(ct);
            return new DirectoryStatusDto(
                latest is null ? null : ToDto(latest), pending is null ? null : ToDto(pending), failed is null ? null : ToDto(failed));
        });

        group.MapPost("/sync", async (ClaimsPrincipal user, DirectoryStore store, AuditLogWriter audit, DataScopeDbContext db, CancellationToken ct) =>
        {
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (snapshot, created) = await store.QueueAsync(actor.Sid, ct);
            if (created)
            {
                await audit.AppendAsync(actor.Sid, actor.Name, "directory.sync.request", "Active Directory", new { snapshot.Id }, ct);
            }
            await tx.CommitAsync(ct);
            return Results.Accepted("/api/v1/directory", ToDto(snapshot));
        }).RequireAuthorization(Policies.ManagePlatform);

        group.MapGet("/principals", async (
            string? search, string? kind, bool? enabled, int? limit, int? offset, DataScopeDbContext db, DirectoryStore store, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            PrincipalKind? k = null;
            if (kind is not null)
            {
                if (!Enum.TryParse(kind, ignoreCase: true, out PrincipalKind parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = [$"Use one of: {string.Join(", ", Enum.GetNames<PrincipalKind>())}."] });
                }
                k = parsed;
            }
            if (search is { Length: > 200 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Search for at most 200 characters."] });
            }
            var latest = await store.LatestCompletedAsync(ct);
            if (latest is null)
            {
                return Results.Ok(new Page<PrincipalDto>([], l, o, 0));
            }
            var query = db.DirectoryPrincipals.AsNoTracking().Where(p => p.SnapshotId == latest.Id);
            if (k is not null)
            {
                query = query.Where(p => p.Kind == k);
            }
            if (enabled is not null)
            {
                query = query.Where(p => p.Enabled == enabled);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                if (Sid.IsValid(term))
                {
                    query = query.Where(p => p.Sid == term);
                }
                else
                {
                    var pattern = "%" + Like(term) + "%";
                    query = query.Where(p => EF.Functions.ILike(p.Name, pattern, "\\")
                        || (p.DisplayName != null && EF.Functions.ILike(p.DisplayName, pattern, "\\"))
                        || (p.UserPrincipalName != null && EF.Functions.ILike(p.UserPrincipalName, pattern, "\\")));
                }
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(p => p.Name).ThenBy(p => p.Sid).Skip(o).Take(l)
                .Select(p => new PrincipalDto(p.Sid, p.Kind.ToString(), p.Name, p.DisplayName, p.Enabled, p.IsSecurityGroup,
                    p.Scope == null ? null : p.Scope.ToString(), p.UserPrincipalName, p.Department, p.Title))
                .ToListAsync(ct);
            return Results.Ok(new Page<PrincipalDto>(items, l, o, total));
        });

        group.MapGet("/principals/{sid}", async (string sid, DataScopeDbContext db, AccessService access, CancellationToken ct) =>
        {
            if (!Sid.IsValid(sid))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sid"] = ["Not a valid SID."] });
            }
            var snapshot = await access.SnapshotAsync(ct);
            if (snapshot is null)
            {
                return Results.NotFound();
            }
            var p = await db.DirectoryPrincipals.AsNoTracking().SingleOrDefaultAsync(x => x.SnapshotId == snapshot.Id && x.Sid == sid, ct);
            if (p is null)
            {
                return Results.NotFound();
            }
            var (graph, _) = await access.GraphAsync(null, ct);
            List<TrusteeDto> memberOf = [], maybe = [], members = [];
            int? certain = null, possible = null;
            if (p.Kind == PrincipalKind.User)
            {
                var token = graph.TokenFor(sid);
                memberOf = token.Sids.Where(s => s != sid && graph.Find(s)?.Kind == PrincipalKind.Group)
                    .Select(s => AccessViews.Trustee(graph, s)!).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                maybe = graph.UncertainGroups.Where(g => token.Contains(g) == Membership.Maybe)
                    .Select(s => AccessViews.Trustee(graph, s)!).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (p.Kind == PrincipalKind.Group)
            {
                members = graph.DirectMembers(sid).Take(MaxMembers).Select(s => AccessViews.Trustee(graph, s)!)
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                (certain, possible) = graph.CountMembers(sid);
            }
            var direct = graph.DirectParents(sid).Select(s => AccessViews.Trustee(graph, s)!).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var dto = new PrincipalDto(p.Sid, p.Kind.ToString(), p.Name, p.DisplayName, p.Enabled, p.IsSecurityGroup, p.Scope?.ToString(), p.UserPrincipalName, p.Department, p.Title);
            return Results.Ok(new PrincipalDetailDto(
                dto, p.DistinguishedName, p.Mail, p.LastLogon, p.WhenChanged, p.SidHistory, p.MembersIncomplete, direct, memberOf, maybe, members,
                p.DirectMemberCount, certain, possible, graph.Cycles.Any(c => c.Contains(sid)), snapshot.Id, snapshot.CompletedAt,
                p.ManagerSid is { } manager ? AccessViews.Trustee(graph, manager) : null));
        });
    }

    private static string Like(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
