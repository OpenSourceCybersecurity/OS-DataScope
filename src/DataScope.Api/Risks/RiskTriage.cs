using DataScope.Core.Governance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Risks;

public static class RiskStatus
{
    public const string Open = "Open";
    public const string Accepted = "Accepted";
    public const string Snoozed = "Snoozed";
}

/// <param name="Superseded">The risk is now more severe than when this was decided, so it shows again.</param>
public sealed record RiskDecisionDto(
    long Id, string Kind, string? Note, DateOnly Until, string Severity, string DecidedByName, DateTimeOffset DecidedAt, bool Superseded);

/// <summary>
/// Accepting and snoozing risks (D115). Risks are worked out on each request, so a decision is
/// matched to a risk by its id, which stays the same while the facts behind the risk do. Nothing here changes a
/// permission, a group or a file.
/// </summary>
internal sealed class RiskTriage(DataScopeDbContext db, RiskEngine engine, TimeProvider clock)
{
    public const int MaxAcceptDays = 365;
    public const int MaxSnoozeDays = 90;

    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>The risk as it is now, or null when the current data no longer shows it.</summary>
    public async Task<Risk?> FindAsync(string riskId, CancellationToken ct) =>
        (await engine.CheckAsync(null, null, permissionsOnly: false, ct)).Items.FirstOrDefault(r => r.Id == riskId);

    /// <summary>Adds each risk's decision, and whether a decision hides it.</summary>
    public async Task<List<Risk>> AnnotateAsync(IReadOnlyList<Risk> risks, CancellationToken ct)
    {
        var today = Today;
        var decisions = await db.RiskDecisions.AsNoTracking().Where(d => d.EndedAt == null && d.Until >= today).ToListAsync(ct);
        var byRisk = decisions.ToDictionary(d => d.RiskId, StringComparer.Ordinal);
        return risks.Select(r =>
        {
            if (!byRisk.TryGetValue(r.Id, out var d))
            {
                return r;
            }
            var superseded = Rank(r.Severity) > Rank(Enum.Parse<RiskSeverity>(d.Severity));
            var status = superseded ? RiskStatus.Open : d.Kind == RiskDecisionKind.Accepted ? RiskStatus.Accepted : RiskStatus.Snoozed;
            return r with
            {
                Status = status,
                Decision = new RiskDecisionDto(d.Id, d.Kind.ToString(), d.Note, d.Until, d.Severity, d.DecidedByName, d.DecidedAt, superseded),
            };
        }).ToList();
    }

    private static int Rank(RiskSeverity severity) => severity switch
    {
        RiskSeverity.High => 3,
        RiskSeverity.Medium => 2,
        _ => 1,
    };
}
