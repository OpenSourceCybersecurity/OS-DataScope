using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Core.Classification;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Custom detection rules (D133): words, phrases and patterns an administrator decides are sensitive, beyond the
/// built-in detectors. Their matches are possible matches like any other, with masked evidence. Every change needs a
/// reason and is audited, and files are read again with the new rules on the next scan.
/// </summary>
internal static class CustomRuleEndpoints
{
    public sealed record RuleDto(
        int Id, string RuleId, string Name, string? Description, string Kind, string? Pattern, IReadOnlyList<string> Keywords, bool CaseSensitive,
        IReadOnlyList<string> NearWords, int MinMatches, string Effect, bool Enabled, string Summary, string Reason,
        DateTimeOffset CreatedAt, string CreatedByName, DateTimeOffset UpdatedAt, string UpdatedByName, long Files);

    public sealed record TemplateDto(
        string Id, string Name, string Description, string Kind, string? Pattern, IReadOnlyList<string> Keywords, IReadOnlyList<string> NearWords,
        int MinMatches, string Effect, string Example);

    public sealed record LimitsDto(
        int MaxRules, int MaxEnabled, int MaxNameChars, int MaxDescriptionChars, int MaxPatternChars, int MaxKeywords, int MaxKeywordChars,
        int MaxNearWords, int MaxMinMatches, int MaxSampleChars, int NearWindow, int MaxMatchChars);

    /// <param name="FilesToReread">Current files last classified with other rules or an older policy: read again on the next scan.</param>
    public sealed record ListDto(IReadOnlyList<RuleDto> Rules, IReadOnlyList<TemplateDto> Templates, LimitsDto Limits, string PolicyVersion, long FilesToReread);

    public sealed record ChangeDto(DateTimeOffset At, string ActorName, string Action, string Target, string? Reason);

    /// <summary>A new rule, or the parts of one to change; null leaves a part as it is.</summary>
    public sealed record SaveRequest(
        string? Name, string? Description, string? Kind, string? Pattern, IReadOnlyList<string>? Keywords, bool? CaseSensitive,
        IReadOnlyList<string>? NearWords, int? MinMatches, string? Effect, bool? Enabled, string? Reason);

    public sealed record TestRequest(
        string? Kind, string? Pattern, IReadOnlyList<string>? Keywords, bool? CaseSensitive, IReadOnlyList<string>? NearWords, int? MinMatches,
        string? Effect, string? Sample);

    /// <param name="Values">Different matching values found (after nearby words).</param>
    /// <param name="Reported">Whether a file holding the sample would be reported: enough values for the minimum.</param>
    /// <param name="Label">The label the rule alone would give such a file.</param>
    public sealed record TestResultDto(int Values, bool Reported, IReadOnlyList<string> Evidence, string Label, bool Truncated);

    private static readonly string[] HistoryActions =
        ["custom-rule.add", "custom-rule.change", "custom-rule.enable", "custom-rule.disable", "custom-rule.remove"];

    private static readonly LimitsDto Limits = new(
        CustomRules.MaxRules, CustomRules.MaxEnabled, CustomRules.MaxNameChars, CustomRules.MaxDescriptionChars, CustomRules.MaxPatternChars,
        CustomRules.MaxKeywords, CustomRules.MaxKeywordChars, CustomRules.MaxNearWords, CustomRules.MaxMinMatches, CustomRules.MaxSampleChars,
        CustomRules.NearWindow, CustomRules.MaxMatchChars);

    public static void MapCustomRules(this RouteGroupBuilder api)
    {
        // The rules themselves can name secret projects, so only administrators and auditors see them; everyone who
        // reads findings sees each rule's name and description.
        var group = api.MapGroup("/custom-rules").RequireAuthorization(Policies.ReadAuditLog);

        group.MapGet("/", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var rows = await db.CustomRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
            var files = await FilesByRuleAsync(db, ct);
            var policy = await CustomRuleStore.PolicyVersionAsync(db, ct);
            var reread = await db.Files.LongCountAsync(f => f.RemovedAt == null && f.ClassifiedPolicy != null && f.ClassifiedPolicy != policy, ct);
            return new ListDto(
                rows.Select(r => ToDto(r, files.GetValueOrDefault(CustomRules.RuleId(r.Id)))).ToList(),
                CustomRules.Templates.Select(t => new TemplateDto(
                    t.Id, t.Name, t.Description, t.Kind.ToString(), t.Pattern, t.Keywords, t.NearWords, t.MinMatches, t.Effect.ToString(), t.Example)).ToList(),
                Limits,
                policy,
                reread);
        });

        group.MapGet("/history", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AppAuditLog.AsNoTracking().Where(a => HistoryActions.Contains(a.Action))
                .OrderByDescending(a => a.Sequence).Take(100)
                .Select(a => new { a.At, a.ActorName, a.Action, a.Target, a.Details }).ToListAsync(ct);
            return rows.Select(a => new ChangeDto(a.At, a.ActorName, a.Action, a.Target, ReasonFrom(a.Details))).ToList();
        });

        // Try a rule on text the administrator pastes. The text is not stored or logged, and the result is masked as in a scan.
        group.MapPost("/test", (TestRequest request) =>
        {
            var errors = new Dictionary<string, string[]>();
            var definition = Definition(0, request.Kind, request.Pattern, request.Keywords, request.CaseSensitive, request.NearWords, request.MinMatches,
                request.Effect, errors);
            if (definition is not null)
            {
                foreach (var (key, value) in CustomRules.Problems(definition))
                {
                    errors[key] = value;
                }
            }
            if (string.IsNullOrEmpty(request.Sample))
            {
                errors["sample"] = ["Paste some text to try the rule on. Use made-up values, not real people's data."];
            }
            if (errors.Count > 0 || definition is null)
            {
                return Results.ValidationProblem(errors);
            }
            var sample = request.Sample!;
            var truncated = sample.Length > CustomRules.MaxSampleChars;
            var text = truncated ? sample[..CustomRules.MaxSampleChars] : sample;
            // A minimum of one, so the result always shows what matched; Reported says whether the real minimum is met.
            var set = CustomRules.Compile([definition with { MinMatches = 1 }]);
            var finding = TextScanner.Scan(text, set).Findings.SingleOrDefault(f => f.Category == FindingCategory.CustomRule);
            var values = finding?.Count ?? 0;
            var reported = finding is not null && values >= definition.MinMatches;
            var label = reported ? ClassificationPolicy.Classify([finding!]) : Sensitivity.Internal;
            return Results.Ok(new TestResultDto(values, reported, finding?.Evidence ?? [], label.ToString(), truncated));
        }).RequireAuthorization(Policies.ManagePlatform);

        group.MapPost("/", async (SaveRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = Name(request.Name, errors);
            var description = Description(request.Description, errors);
            var reason = Reason(request.Reason, errors);
            var definition = Definition(0, request.Kind, request.Pattern, request.Keywords, request.CaseSensitive, request.NearWords,
                request.MinMatches ?? 1, request.Effect, errors);
            if (definition is not null)
            {
                foreach (var (key, value) in CustomRules.Problems(definition))
                {
                    errors[key] = value;
                }
            }
            if (errors.Count > 0 || definition is null)
            {
                return Results.ValidationProblem(errors);
            }
            var enabled = request.Enabled ?? true;
            if (await db.CustomRules.CountAsync(ct) >= CustomRules.MaxRules)
            {
                return Conflict($"There are already {CustomRules.MaxRules} custom rules. Remove ones you no longer need first.");
            }
            if (enabled && await db.CustomRules.CountAsync(r => r.Enabled, ct) >= CustomRules.MaxEnabled)
            {
                return Conflict($"{CustomRules.MaxEnabled} custom rules are already on, the most a scan runs. Turn one off first, or add this one turned off.");
            }
            if (await NameTakenAsync(db, name!, null, ct))
            {
                return Invalid("name", "Another custom rule already has this name.");
            }

            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var row = new CustomDetectionRule
            {
                Name = name!,
                Description = description,
                Keywords = [],
                NearWords = [],
                Enabled = enabled,
                Reason = reason!,
                CreatedAt = now,
                CreatedByName = actor.Name,
                UpdatedAt = now,
                UpdatedBySid = actor.Sid,
                UpdatedByName = actor.Name,
            };
            Apply(row, definition);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.CustomRules.Add(row);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "custom-rule.add", row.Name, AuditDetails(row), ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/custom-rules/{row.Id}", ToDto(row, 0));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Change a rule, or turn it on or off. Every change needs a reason.
        group.MapPut("/{id:int}", async (int id, SaveRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            var row = await db.CustomRules.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            var errors = new Dictionary<string, string[]>();
            var reason = Reason(request.Reason, errors);
            var name = request.Name is null ? row.Name : Name(request.Name, errors);
            var description = request.Description is null ? row.Description : Description(request.Description, errors);
            var before = row.ToDefinition();
            var kind = request.Kind ?? row.Kind.ToString();
            var switching = !string.Equals(kind, row.Kind.ToString(), StringComparison.OrdinalIgnoreCase);
            var definition = Definition(row.Id, kind,
                request.Pattern ?? (switching ? null : row.Pattern),
                request.Keywords ?? (switching ? null : row.Keywords),
                request.CaseSensitive ?? row.CaseSensitive, request.NearWords ?? row.NearWords, request.MinMatches ?? row.MinMatches,
                request.Effect ?? row.Effect.ToString(), errors);
            if (definition is not null)
            {
                foreach (var (key, value) in CustomRules.Problems(definition))
                {
                    errors[key] = value;
                }
            }
            if (errors.Count > 0 || definition is null)
            {
                return Results.ValidationProblem(errors);
            }
            if (name != row.Name && await NameTakenAsync(db, name!, row.Id, ct))
            {
                return Invalid("name", "Another custom rule already has this name.");
            }
            var enabled = request.Enabled ?? row.Enabled;
            if (enabled && !row.Enabled && await db.CustomRules.CountAsync(r => r.Enabled, ct) >= CustomRules.MaxEnabled)
            {
                return Conflict($"{CustomRules.MaxEnabled} custom rules are already on, the most a scan runs. Turn one off first.");
            }

            var actor = Actor.From(user);
            var target = row.Name;
            var changed = Changed(before, definition) || name != row.Name || description != row.Description;
            if (!changed && enabled == row.Enabled && reason == row.Reason)
            {
                return Results.Ok(ToDto(row, (await FilesByRuleAsync(db, ct)).GetValueOrDefault(CustomRules.RuleId(row.Id))));
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (changed || enabled == row.Enabled)
            {
                // A changed rule, or only a new reason: either way the audit log keeps what it was.
                var old = AuditDetails(row);
                row.Name = name!;
                row.Description = description;
                Apply(row, definition);
                row.Reason = reason!;
                await audit.AppendAsync(actor.Sid, actor.Name, "custom-rule.change", target, new { Before = old, After = AuditDetails(row), Reason = reason }, ct);
            }
            if (enabled != row.Enabled)
            {
                row.Enabled = enabled;
                await audit.AppendAsync(actor.Sid, actor.Name, enabled ? "custom-rule.enable" : "custom-rule.disable", row.Name,
                    new { Rule = CustomRules.RuleId(row.Id), Reason = reason }, ct);
            }
            row.Reason = reason!;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBySid = actor.Sid;
            row.UpdatedByName = actor.Name;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var files = await FilesByRuleAsync(db, ct);
            return Results.Ok(ToDto(row, files.GetValueOrDefault(CustomRules.RuleId(row.Id))));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Removing a rule: its findings stay until each file is read again on the next scan, shown as a removed rule.
        group.MapDelete("/{id:int}", async (int id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, CancellationToken ct) =>
        {
            var row = await db.CustomRules.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.CustomRules.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "custom-rule.remove", row.Name, AuditDetails(row), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private static CustomRuleDefinition? Definition(int id, string? kind, string? pattern, IReadOnlyList<string>? keywords, bool? caseSensitive,
        IReadOnlyList<string>? nearWords, int? minMatches, string? effect, Dictionary<string, string[]> errors)
    {
        CustomRuleKind? k = null;
        if (Enum.TryParse<CustomRuleKind>(kind, ignoreCase: true, out var parsedKind) && Enum.IsDefined(parsedKind) && !int.TryParse(kind, out _))
        {
            k = parsedKind;
        }
        else
        {
            errors["kind"] = ["Use Keywords or Pattern."];
        }
        CustomRuleEffect? e = null;
        if (Enum.TryParse<CustomRuleEffect>(effect, ignoreCase: true, out var parsedEffect) && Enum.IsDefined(parsedEffect) && !int.TryParse(effect, out _))
        {
            e = parsedEffect;
        }
        else
        {
            errors["effect"] = ["Use Restricted, Confidential or ListOnly."];
        }
        if (k is null || e is null)
        {
            return null;
        }
        return new CustomRuleDefinition(
            id,
            k.Value,
            k == CustomRuleKind.Pattern ? pattern?.Trim() : null,
            k == CustomRuleKind.Keywords ? CustomRules.Clean(keywords) : [],
            caseSensitive ?? false,
            CustomRules.Clean(nearWords),
            minMatches ?? 1,
            e.Value);
    }

    private static void Apply(CustomDetectionRule row, CustomRuleDefinition d)
    {
        row.Kind = d.Kind;
        row.Pattern = d.Pattern;
        row.Keywords = [.. d.Keywords];
        row.CaseSensitive = d.CaseSensitive;
        row.NearWords = [.. d.NearWords];
        row.MinMatches = d.MinMatches;
        row.Effect = d.Effect;
    }

    private static bool Changed(CustomRuleDefinition a, CustomRuleDefinition b) =>
        a.Kind != b.Kind || a.Pattern != b.Pattern || !a.Keywords.SequenceEqual(b.Keywords) || a.CaseSensitive != b.CaseSensitive
        || !a.NearWords.SequenceEqual(b.NearWords) || a.MinMatches != b.MinMatches || a.Effect != b.Effect;

    private static string? Name(string? name, Dictionary<string, string[]> errors)
    {
        if (CustomRules.NameProblem(name) is { } problem)
        {
            errors["name"] = [problem];
            return null;
        }
        return name!.Trim();
    }

    private static string? Description(string? description, Dictionary<string, string[]> errors)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.Length > CustomRules.MaxDescriptionChars || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            errors["description"] = [$"Use at most {CustomRules.MaxDescriptionChars} characters of plain text."];
            return null;
        }
        return text;
    }

    private static string? Reason(string? reason, Dictionary<string, string[]> errors)
    {
        if (CustomRules.ReasonProblem(reason) is { } problem)
        {
            errors["reason"] = [problem];
            return null;
        }
        return reason!.Trim();
    }

    /// <summary>Names are unique ignoring case. There are at most a hundred rules, so they are compared here.</summary>
    private static async Task<bool> NameTakenAsync(DataScopeDbContext db, string name, int? except, CancellationToken ct) =>
        (await db.CustomRules.Where(r => r.Id != except).Select(r => r.Name).ToListAsync(ct)).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Current files each custom rule found something in, by rule id.</summary>
    private static async Task<Dictionary<string, long>> FilesByRuleAsync(DataScopeDbContext db, CancellationToken ct) =>
        await (from finding in db.Findings
               join f in db.Files on finding.FileId equals f.Id
               where finding.Category == FindingCategory.CustomRule && f.RemovedAt == null
               group finding by finding.RuleId into g
               select new { g.Key, Files = g.Select(x => x.FileId).Distinct().LongCount() })
            .ToDictionaryAsync(x => x.Key, x => x.Files, ct);

    private static RuleDto ToDto(CustomDetectionRule r, long files) => new(
        r.Id, CustomRules.RuleId(r.Id), r.Name, r.Description, r.Kind.ToString(), r.Pattern, r.Keywords, r.CaseSensitive, r.NearWords, r.MinMatches,
        r.Effect.ToString(), r.Enabled, CustomRuleText.Describe(r), r.Reason, r.CreatedAt, r.CreatedByName, r.UpdatedAt, r.UpdatedByName, files);

    private static object AuditDetails(CustomDetectionRule r) => new
    {
        Rule = CustomRules.RuleId(r.Id),
        r.Name,
        Kind = r.Kind.ToString(),
        r.Pattern,
        r.Keywords,
        r.CaseSensitive,
        r.NearWords,
        r.MinMatches,
        Effect = r.Effect.ToString(),
        r.Enabled,
        r.Reason,
    };

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult Conflict(string title) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title);

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
}

/// <summary>A custom rule in a sentence, for lists and the rule catalogue.</summary>
internal static class CustomRuleText
{
    public static string Describe(CustomDetectionRule r)
    {
        var what = r.Kind == CustomRuleKind.Keywords
            ? $"Any of {r.Keywords.Length:N0} {(r.Keywords.Length == 1 ? "word or phrase" : "words or phrases")}{(r.CaseSensitive ? ", matching case" : "")}"
            : $"Text matching a pattern{(r.CaseSensitive ? ", matching case" : "")}";
        var near = r.NearWords.Length > 0 ? $", within {CustomRules.NearWindow} characters of {string.Join(", ", r.NearWords.Take(5).Select(w => $"\"{w}\""))}{(r.NearWords.Length > 5 ? "…" : "")}" : "";
        var min = r.MinMatches > 1 ? $"; reported when a file has {r.MinMatches:N0} or more different matches" : "";
        var effect = r.Effect switch
        {
            CustomRuleEffect.Restricted => "Files with a match are labelled Restricted.",
            CustomRuleEffect.Confidential => "Files with a match are labelled at least Confidential.",
            _ => "Matches are listed without changing the label.",
        };
        return $"{what}{near}{min}. {effect}";
    }
}
