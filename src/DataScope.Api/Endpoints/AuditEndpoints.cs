using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

internal static class AuditEndpoints
{
    public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string ActorName, string Action, string Target, string Hash);

    public static void MapAuditLog(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/audit-log").RequireAuthorization(Policies.ReadAuditLog);

        group.MapGet("/", async (int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var total = await db.AppAuditLog.CountAsync(ct);
            var items = await db.AppAuditLog.AsNoTracking().OrderByDescending(a => a.Sequence).Skip(o).Take(l)
                .Select(a => new AuditEntryDto(a.Sequence, a.At, a.ActorName, a.Action, a.Target, a.Hash))
                .ToListAsync(ct);
            return new Page<AuditEntryDto>(items, l, o, total);
        });

        group.MapGet("/verify", (AuditLogWriter audit, CancellationToken ct) => audit.VerifyAsync(ct));
    }
}
