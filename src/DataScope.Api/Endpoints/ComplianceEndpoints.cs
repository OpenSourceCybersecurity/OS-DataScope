using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Api.Compliance;
using DataScope.Core.Classification;
using DataScope.Core.Compliance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Compliance standards (D126): which standards apply here, which kinds of data count towards each, and how much data
/// falls under them. Data Scope tags data with the standards it falls under; it never says anything is compliant.
/// </summary>
internal static class ComplianceEndpoints
{
    public sealed record RequirementDto(string Ref, string Text, string Helps);

    /// <param name="Files">Current files under the standard.</param>
    /// <param name="Folders">Folders holding those files.</param>
    public sealed record CountsDto(long Files, long Restricted, long Confidential, long Folders, long Shares);

    public sealed record StandardDto(
        string Id, string Name, string Summary, bool Enabled, IReadOnlyList<string> Categories, IReadOnlyList<string> DefaultCategories,
        string MinConfidence, string? Reason, DateTimeOffset? UpdatedAt, string? UpdatedByName,
        IReadOnlyList<string> NotDetected, IReadOnlyList<RequirementDto> Requirements, CountsDto? Counts);

    /// <param name="FilesNotRead">Current files whose contents were never read: any of them may hold data under a standard.</param>
    public sealed record StandardsDto(IReadOnlyList<StandardDto> Standards, IReadOnlyList<string> Categories, long FilesNotRead);

    public sealed record ChangeDto(DateTimeOffset At, string ActorName, string Action, string Target, string? Reason);

    public sealed record UpdateRequest(bool? Enabled, IReadOnlyList<string>? Categories, string? MinConfidence, string? Reason);

    private static readonly string[] HistoryActions = ["compliance.enable", "compliance.disable", "compliance.change"];

    public static void MapCompliance(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/compliance").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/standards", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var standards = await ComplianceScope.LoadAsync(db, ct);
            var items = new List<StandardDto>(standards.Count);
            foreach (var s in standards)
            {
                items.Add(ToDto(s, s.Enabled ? await CountsAsync(db, s, ct) : null));
            }
            var notRead = await db.Files.LongCountAsync(
                f => f.RemovedAt == null && f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial, ct);
            return new StandardsDto(items, Enum.GetValues<FindingCategory>().Select(c => c.Label()).ToList(), notRead);
        });

        group.MapGet("/history", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AppAuditLog.AsNoTracking().Where(a => HistoryActions.Contains(a.Action))
                .OrderByDescending(a => a.Sequence).Take(100)
                .Select(a => new { a.At, a.ActorName, a.Action, a.Target, a.Details }).ToListAsync(ct);
            return rows.Select(a => new ChangeDto(a.At, a.ActorName, a.Action, a.Target, ReasonFrom(a.Details))).ToList();
        }).RequireAuthorization(Policies.ReadAuditLog);

        // Turn a standard on or off, or change what counts towards it. Every change needs a reason and is audited.
        group.MapPut("/standards/{id}", async (string id, UpdateRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (ComplianceCatalog.Find(id) is not { } definition)
            {
                return Results.NotFound();
            }
            var errors = new Dictionary<string, string[]>();
            if (request.Enabled is null && request.Categories is null && request.MinConfidence is null)
            {
                errors["enabled"] = ["Send enabled, categories, minConfidence, or any of them."];
            }
            if (ComplianceCatalog.ReasonProblem(request.Reason) is { } reasonProblem)
            {
                errors["reason"] = [reasonProblem];
            }
            List<FindingCategory>? categories = null;
            if (request.Categories is { } names)
            {
                categories = [];
                foreach (var name in names)
                {
                    if (FindingCategoryText.TryParseLabel(name, out var c))
                    {
                        categories.Add(c);
                    }
                    else
                    {
                        errors["categories"] = [$"Unknown kind of data \"{Clip(name)}\". Use: {string.Join(", ", Enum.GetValues<FindingCategory>().Select(x => x.Label()))}."];
                        break;
                    }
                }
                categories = [.. categories.Distinct().Order()];
                if (categories.Count == 0 && !errors.ContainsKey("categories"))
                {
                    errors["categories"] = ["Choose at least one kind of data, or turn the standard off."];
                }
            }
            Confidence? min = null;
            if (request.MinConfidence is { } text)
            {
                if (Enum.TryParse<Confidence>(text, ignoreCase: true, out var c) && ComplianceCatalog.AllowedMinConfidence.Contains(c) && !int.TryParse(text, out _))
                {
                    min = c;
                }
                else
                {
                    errors["minConfidence"] = ["Use Medium or High. Low-confidence matches never count."];
                }
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = Actor.From(user);
            var reason = request.Reason!.Trim();
            var row = await db.ComplianceStandards.FirstOrDefaultAsync(s => s.StandardId == definition.Id, ct);
            var before = ComplianceScope.From(definition, row);
            var enabled = request.Enabled ?? before.Enabled;
            var newCategories = categories ?? [.. before.Categories.Order()];
            var newMin = min ?? before.MinConfidence;
            var changedWhat = !newCategories.SequenceEqual(before.Categories.Order()) || newMin != before.MinConfidence;
            if (enabled == before.Enabled && !changedWhat && (row is null || row.Reason == reason))
            {
                // Nothing to change; a standard that was never turned on stays without a row.
                return Results.Ok(ToDto(before, enabled ? await CountsAsync(db, before, ct) : null));
            }
            if (row is null)
            {
                row = new ComplianceStandardSetting
                {
                    StandardId = definition.Id,
                    Categories = [],
                    Reason = reason,
                    UpdatedBySid = actor.Sid,
                    UpdatedByName = actor.Name,
                };
                db.ComplianceStandards.Add(row);
            }
            row.Enabled = enabled;
            row.Categories = [.. newCategories.Select(c => c.ToString())];
            row.MinConfidence = newMin;
            row.Reason = reason;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBySid = actor.Sid;
            row.UpdatedByName = actor.Name;

            var after = ComplianceScope.From(definition, row);
            var action = enabled == before.Enabled ? "compliance.change" : enabled ? "compliance.enable" : "compliance.disable";
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, action, definition.Name, new
            {
                Standard = definition.Id,
                Enabled = enabled,
                Categories = after.Categories.Select(c => c.Label()).ToList(),
                MinConfidence = after.MinConfidence.ToString(),
                BeforeCategories = before.Categories.Select(c => c.Label()).ToList(),
                BeforeMinConfidence = before.MinConfidence.ToString(),
                Reason = reason,
            }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToDto(after, enabled ? await CountsAsync(db, after, ct) : null));
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private static async Task<CountsDto> CountsAsync(DataScopeDbContext db, EffectiveStandard standard, CancellationToken ct)
    {
        var files = ComplianceScope.Covered(db, db.Files.Where(f => f.RemovedAt == null), standard);
        var bySensitivity = await files.GroupBy(f => f.OverrideSensitivity ?? f.Sensitivity)
            .Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var folders = await files.Select(f => f.FolderId).Distinct().LongCountAsync(ct);
        var shares = await files.Select(f => f.ShareId).Distinct().LongCountAsync(ct);
        return new CountsDto(
            bySensitivity.Sum(x => x.Count),
            bySensitivity.Where(x => x.Key == Sensitivity.Restricted).Sum(x => x.Count),
            bySensitivity.Where(x => x.Key == Sensitivity.Confidential).Sum(x => x.Count),
            folders,
            shares);
    }

    private static StandardDto ToDto(EffectiveStandard s, CountsDto? counts) => new(
        s.Id, s.Name, s.Definition.Summary, s.Enabled,
        s.Categories.Select(c => c.Label()).ToList(), s.Definition.DefaultCategories.Select(c => c.Label()).ToList(),
        s.MinConfidence.ToString(), s.Reason, s.UpdatedAt, s.UpdatedByName,
        s.Definition.NotDetected, s.Definition.Requirements.Select(r => new RequirementDto(r.Ref, r.Text, r.Helps)).ToList(), counts);

    private static string? ReasonFrom(string details)
    {
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Reason", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
