using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Api.Compliance;
using DataScope.Api.Endpoints;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Risks;

/// <summary>
/// The Risks screen: what needs attention, worked out from the current data. Accepting and snoozing a risk
/// record a decision; nothing here changes a permission, a group or a file (D115).
/// </summary>
internal static class RiskEndpoints
{
    /// <summary>A rule that matched something, with how many risks of each severity it found.</summary>
    public sealed record RuleDto(string Id, string Name, int High, int Medium, int Low);

    /// <param name="High">Open risks of each severity in scope, before the severity and rule filters.</param>
    /// <param name="Accepted">Risks in scope hidden by an accept or a snooze that still applies.</param>
    /// <param name="Truncated">More risks matched than <see cref="MaxItems"/>; narrow by severity, rule or share.</param>
    public sealed record RiskListDto(
        IReadOnlyList<Risk> Items, int High, int Medium, int Low, int Accepted, int Snoozed, IReadOnlyList<RuleDto> Rules,
        bool Truncated, IReadOnlyList<string> Limits, DateTimeOffset CheckedAt, DateTimeOffset? LastScanAt, DateTimeOffset? DirectorySyncedAt);

    public sealed record DecisionRequest(string? RiskId, string? Kind, string? Note, DateOnly? Until);

    /// <summary>Which risks the list shows: those needing attention (the default), accepted, snoozed, or all.</summary>
    private static readonly string[] Views = ["open", "accepted", "snoozed", "all"];

    /// <summary>Risks one response lists; the counts always cover them all.</summary>
    public const int MaxItems = 2_000;

    public static void MapRisks(this RouteGroupBuilder api)
    {
        api.MapGet("/risks", async (
            Guid? serverId, Guid? shareId, string? severity, string? rule, string? view, string? standard,
            RiskEngine engine, RiskTriage triage, DataScopeDbContext db, DirectoryStore directory, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            view = (view ?? "open").ToLowerInvariant();
            if (!Views.Contains(view))
            {
                errors["view"] = ["Use open, accepted, snoozed or all."];
            }
            RiskSeverity? sev = null;
            if (severity is not null)
            {
                if (!Enum.TryParse<RiskSeverity>(severity, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(severity, out _))
                {
                    errors["severity"] = ["Use High, Medium or Low."];
                }
                sev = parsed;
            }
            if (rule is not null && !RiskRules.Names.ContainsKey(rule))
            {
                errors["rule"] = [$"Use one of: {string.Join(", ", RiskRules.Names.Keys)}."];
            }
            // A standard narrows the risks like a scope does: every count then covers only risks that involve its data.
            var (std, problem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (problem is not null)
            {
                errors["standard"] = [problem];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            if (shareId is { } sh && !await db.Shares.AnyAsync(s => s.Id == sh, ct))
            {
                return Results.NotFound();
            }
            if (serverId is { } sv && !await db.ManagedServers.AnyAsync(s => s.Id == sv, ct))
            {
                return Results.NotFound();
            }

            var scan = await engine.CheckAsync(serverId, shareId, permissionsOnly: false, ct);
            var everything = await triage.AnnotateAsync(
                std is null ? scan.Items : scan.Items.Where(r => r.Standards?.Contains(std.Id) == true).ToList(), ct);
            var open = everything.Where(x => x.Status == RiskStatus.Open).ToList();
            var all = view switch
            {
                "open" => open,
                "accepted" => everything.Where(x => x.Status == RiskStatus.Accepted).ToList(),
                "snoozed" => everything.Where(x => x.Status == RiskStatus.Snoozed).ToList(),
                _ => everything,
            };
            var rules = RiskRules.Names
                .Select(r => new RuleDto(r.Key, r.Value,
                    all.Count(x => x.Rule == r.Key && x.Severity == RiskSeverity.High),
                    all.Count(x => x.Rule == r.Key && x.Severity == RiskSeverity.Medium),
                    all.Count(x => x.Rule == r.Key && x.Severity == RiskSeverity.Low)))
                .Where(r => r.High + r.Medium + r.Low > 0)
                .ToList();
            var shown = all.Where(x => (sev == null || x.Severity == sev) && (rule == null || x.Rule == rule)).ToList();
            var lastScan = await db.Shares.Where(s => s.LastScannedAt != null).MaxAsync(s => s.LastScannedAt, ct);
            var snapshot = await directory.LatestCompletedAsync(ct);
            return Results.Ok(new RiskListDto(
                shown.Take(MaxItems).ToList(),
                open.Count(x => x.Severity == RiskSeverity.High),
                open.Count(x => x.Severity == RiskSeverity.Medium),
                open.Count(x => x.Severity == RiskSeverity.Low),
                everything.Count(x => x.Status == RiskStatus.Accepted),
                everything.Count(x => x.Status == RiskStatus.Snoozed),
                rules,
                shown.Count > MaxItems,
                scan.Limits,
                clock.GetUtcNow(),
                lastScan,
                snapshot?.CompletedAt));
        }).RequireAuthorization(Policies.ReadPosture);

        // Accept or snooze a risk. It leaves the default list until the date, or until it becomes more severe.
        api.MapPost("/risks/decisions", async (
            DecisionRequest request, ClaimsPrincipal user, RiskTriage triage, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var riskId = request.RiskId?.Trim() ?? "";
            var note = request.Note?.Trim();
            var errors = new Dictionary<string, string[]>();
            if (riskId.Length is 0 or > 400)
            {
                errors["riskId"] = ["Choose a risk from the list."];
            }
            RiskDecisionKind kind = default;
            if (request.Kind is null || !Enum.TryParse(request.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind) || int.TryParse(request.Kind, out _))
            {
                errors["kind"] = ["Use Accepted or Snoozed."];
            }
            else
            {
                var today = triage.Today;
                var maxDays = kind == RiskDecisionKind.Accepted ? RiskTriage.MaxAcceptDays : RiskTriage.MaxSnoozeDays;
                if (request.Until is not { } until || until <= today || until > today.AddDays(maxDays))
                {
                    errors["until"] = [kind == RiskDecisionKind.Accepted
                        ? "Choose when the acceptance ends: after today and within a year. The risk is looked at again then."
                        : "Choose when the snooze ends: after today and within 90 days."];
                }
                if (kind == RiskDecisionKind.Accepted && (note?.Length ?? 0) < 10)
                {
                    errors["note"] = ["Say why the risk is acceptable, in 10 to 1,000 characters."];
                }
            }
            if (note is { Length: > 1000 })
            {
                errors["note"] = ["Keep the note under 1,000 characters."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var risk = await triage.FindAsync(riskId, ct);
            if (risk is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "That risk is no longer found. Refresh the list.");
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var actor = Actor.From(user);
            var current = await db.RiskDecisions.FirstOrDefaultAsync(d => d.RiskId == riskId && d.EndedAt == null, ct);
            if (current is not null && current.Until >= triage.Today)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This risk is already accepted or snoozed. Reopen it first to change that.");
            }
            if (current is not null)
            {
                // Its date has passed, so it no longer hides the risk; end it so the new decision takes its place.
                current.EndedAt = clock.GetUtcNow();
                current.EndedBySid = actor.Sid;
                current.EndedByName = actor.Name;
                await db.SaveChangesAsync(ct);
            }
            var decision = new RiskDecision
            {
                RiskId = riskId,
                Rule = risk.Rule,
                Title = Cut(risk.Title, 512),
                Where = Cut(risk.Where, 2048),
                Severity = risk.Severity.ToString(),
                Kind = kind,
                Note = string.IsNullOrEmpty(note) ? null : note,
                Until = request.Until!.Value,
                DecidedBySid = actor.Sid,
                DecidedByName = actor.Name,
                DecidedAt = clock.GetUtcNow(),
            };
            db.RiskDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, kind == RiskDecisionKind.Accepted ? "risk.accept" : "risk.snooze", risk.Where,
                new { decision.Id, RiskId = riskId, risk.Rule, Severity = decision.Severity, decision.Until, decision.Note }, ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/risks/decisions/{decision.Id}",
                new RiskDecisionDto(decision.Id, kind.ToString(), decision.Note, decision.Until, decision.Severity, decision.DecidedByName, decision.DecidedAt, false));
        }).RequireAuthorization(Policies.TriageRisks);

        // Reopen: the risk shows again. The decision's row stays, ended, as history.
        api.MapDelete("/risks/decisions/{id:long}", async (long id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var decision = await db.RiskDecisions.FirstOrDefaultAsync(d => d.Id == id && d.EndedAt == null, ct);
            if (decision is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            decision.EndedAt = clock.GetUtcNow();
            decision.EndedBySid = actor.Sid;
            decision.EndedByName = actor.Name;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "risk.reopen", decision.Where, new { decision.Id, decision.RiskId, Was = decision.Kind.ToString() }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.TriageRisks);
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
