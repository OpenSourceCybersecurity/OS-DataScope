using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Api.Compliance;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Sensitive-data results. Findings are possible matches with a confidence; evidence is redacted and shown only to
/// roles allowed to read it. Files whose contents were not read are reported as unknown, never as clean.
/// </summary>
internal static class ClassificationEndpoints
{
    public sealed record CategoryCount(string Category, string Confidence, int Matches);

    /// <summary>Where an item lives (D146): a file on a file share.</summary>
    public const string FileShareSource = "FileShare";

    /// <param name="Source">Always FileShare: where the item lives.</param>
    /// <param name="Folder">The folder's path in the share; empty at the top.</param>
    public sealed record ClassifiedFileDto(
        Guid Id, Guid ShareId, string ShareName, string Hostname, string Path, string Name, string Extension, long Size,
        DateTimeOffset? ModifiedAt, string Sensitivity, string PolicySensitivity, string? OverrideSensitivity,
        string ContentState, string? ContentNote, DateTimeOffset? ClassifiedAt, string? ClassifiedPolicy,
        IReadOnlyList<CategoryCount> Categories, bool Removed, IReadOnlyList<string> Standards,
        string Source = FileShareSource, string Folder = "");

    public sealed record FindingDto(
        string RuleId, string RuleName, string Category, string Confidence, int Matches,
        IReadOnlyList<string>? Evidence, string PolicyVersion, DateTimeOffset DiscoveredAt);

    public sealed record OverrideDto(string? Sensitivity, string PolicySensitivity, string Reason, string ReviewerName, DateTimeOffset At);

    public sealed record FileClassificationDto(
        ClassifiedFileDto File, IReadOnlyList<FindingDto> Findings, bool EvidenceHidden,
        IReadOnlyList<OverrideDto> Overrides, bool ChangedSinceReview);

    public sealed record OverrideRequest(string? Sensitivity, string? Reason);

    public sealed record CategorySummary(string Category, int Files, int High, int Medium, int Low);

    public sealed record SummaryDto(
        string PolicyVersion, long Files, IReadOnlyDictionary<string, long> BySensitivity,
        IReadOnlyDictionary<string, long> ByContentState, IReadOnlyList<CategorySummary> ByCategory, long Overrides);

    public sealed record RuleDto(string Id, string Name, string Category, string Description);

    public sealed record RulesDto(string PolicyVersion, IReadOnlyList<RuleDto> Rules);

    // A class with init properties, not a positional record: EF can order and filter over member initializers,
    // but not over constructor arguments.
    private sealed class Row
    {
        public required InventoryFile File { get; init; }
        public required string ShareName { get; init; }
        public required string Hostname { get; init; }
        public required string FolderPath { get; init; }
        public required SourceKind Kind { get; init; }
    }

    private static IQueryable<Row> Rows(DataScopeDbContext db, IQueryable<InventoryFile> files) =>
        from f in files.AsNoTracking()
        join folder in db.Folders on f.FolderId equals folder.Id
        join share in db.Shares on f.ShareId equals share.Id
        join server in db.ManagedServers on share.ServerId equals server.Id
        select new Row { File = f, ShareName = share.Name, Hostname = server.Hostname, FolderPath = folder.Path, Kind = server.Kind };

    public static void MapClassification(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/classification").RequireAuthorization(Policies.ReadPosture);

        // The built-in rules, then the administrators' own (D133), turned off or not. A custom rule's words and pattern
        // are not shown here: they can name secret projects (see /custom-rules).
        group.MapGet("/rules", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var custom = await db.CustomRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
            return new RulesDto(
                await CustomRuleStore.PolicyVersionAsync(db, ct),
                [
                    .. ClassificationPolicy.Rules.Select(r => new RuleDto(r.Id, r.Name, r.Category.Label(), r.Description)),
                    .. custom.Select(r => new RuleDto(CustomRules.RuleId(r.Id), r.Name, FindingCategory.CustomRule.Label(),
                        (r.Enabled ? "" : "Turned off. ") + (r.Description ?? "A rule an administrator added."))),
                ]);
        });

        group.MapGet("/summary", async (Guid? serverId, Guid? shareId, string? standard, string? source, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (!TryParseSource(source, out var kind))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = [SourceProblem] });
            }
            var files = Scope(db, serverId, shareId, kind);
            var (std, problem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (problem is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["standard"] = [problem] });
            }
            if (std is not null)
            {
                files = ComplianceScope.Covered(db, files, std);
            }
            var bySensitivity = await files
                .GroupBy(f => f.OverrideSensitivity ?? f.Sensitivity)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .ToListAsync(ct);
            var byState = await files.GroupBy(f => f.ContentState).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct);
            var byCategory = await (from finding in db.Findings
                                    join f in files on finding.FileId equals f.Id
                                    group finding by new { finding.Category, finding.Confidence } into g
                                    select new { g.Key.Category, g.Key.Confidence, Files = g.Select(x => x.FileId).Distinct().Count() })
                .ToListAsync(ct);
            var filesByCategory = await (from finding in db.Findings
                                         join f in files on finding.FileId equals f.Id
                                         group finding by finding.Category into g
                                         select new { Category = g.Key, Files = g.Select(x => x.FileId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Category, x => x.Files, ct);
            var overrides = await files.LongCountAsync(f => f.OverrideSensitivity != null, ct);
            int Count(FindingCategory category, Confidence confidence) =>
                byCategory.Where(x => x.Category == category && x.Confidence == confidence).Sum(x => x.Files);
            var categories = filesByCategory
                .OrderByDescending(x => x.Value)
                .Select(x => new CategorySummary(x.Key.Label(), x.Value, Count(x.Key, Confidence.High), Count(x.Key, Confidence.Medium), Count(x.Key, Confidence.Low)))
                .ToList();
            return Results.Ok(new SummaryDto(
                await CustomRuleStore.PolicyVersionAsync(db, ct),
                bySensitivity.Sum(x => x.Count),
                Enum.GetValues<Sensitivity>().ToDictionary(s => s.ToString(), s => bySensitivity.Where(x => x.Key == s).Sum(x => x.Count)),
                Enum.GetValues<ContentState>().ToDictionary(s => s.ToString(), s => byState.Where(x => x.Key == s).Sum(x => x.Count)),
                categories,
                overrides));
        });

        group.MapGet("/files", async (
            Guid? serverId, Guid? shareId, string? sensitivity, string? minSensitivity, string? category, string? minConfidence,
            string? contentState, bool? notRead, string? standard, string? rule, string? source,
            int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var errors = new Dictionary<string, string[]>();
            if (!TryParseSource(source, out var kind))
            {
                errors["source"] = [SourceProblem];
            }
            var files = Scope(db, serverId, shareId, kind);
            if (Parse<Sensitivity>(sensitivity, nameof(sensitivity), errors) is { } exact)
            {
                files = files.Where(f => (f.OverrideSensitivity ?? f.Sensitivity) == exact);
            }
            if (Parse<Sensitivity>(minSensitivity, nameof(minSensitivity), errors) is { } min)
            {
                var allowed = Enum.GetValues<Sensitivity>().Where(s => s >= min).ToList();
                files = files.Where(f => allowed.Contains(f.OverrideSensitivity ?? f.Sensitivity));
            }
            if (Parse<ContentState>(contentState, nameof(contentState), errors) is { } state)
            {
                files = files.Where(f => f.ContentState == state);
            }
            if (notRead == true)
            {
                // Everything whose contents were not read in full or in part: coverage gaps, never "clean".
                files = files.Where(f => f.ContentState != ContentState.Scanned && f.ContentState != ContentState.Partial);
            }
            FindingCategory? cat = null;
            if (category is not null)
            {
                if (FindingCategoryText.TryParseLabel(category, out var parsed))
                {
                    cat = parsed;
                }
                else
                {
                    errors["category"] = [$"Unknown category. Use one of: {string.Join(", ", Enum.GetValues<FindingCategory>().Select(c => c.Label()))}."];
                }
            }
            var conf = Parse<Confidence>(minConfidence, nameof(minConfidence), errors);
            if (rule is not null && (rule.Length > 32 || !rule.StartsWith("DS-", StringComparison.Ordinal)))
            {
                errors["rule"] = ["Use a rule id such as DS-SSN-01 or DS-CUSTOM-3."];
            }
            var (std, standardProblem) = await ComplianceScope.FilterAsync(db, standard, ct);
            if (standardProblem is not null)
            {
                errors["standard"] = [standardProblem];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            if (std is not null)
            {
                files = ComplianceScope.Covered(db, files, std);
            }
            if (cat is not null || conf is not null || rule is not null)
            {
                var confidences = Enum.GetValues<Confidence>().Where(c => conf is null || c >= conf).ToList();
                files = files.Where(f => db.Findings.Any(x => x.FileId == f.Id
                    && (cat == null || x.Category == cat)
                    && (rule == null || x.RuleId == rule)
                    && confidences.Contains(x.Confidence)));
            }

            var total = await files.CountAsync(ct);
            // Most sensitive first. Labels are stored as text, so rank them explicitly.
            var page = await Rows(db, files)
                .OrderByDescending(r => (r.File.OverrideSensitivity ?? r.File.Sensitivity) == Sensitivity.Restricted ? 4
                    : (r.File.OverrideSensitivity ?? r.File.Sensitivity) == Sensitivity.Confidential ? 3
                    : (r.File.OverrideSensitivity ?? r.File.Sensitivity) == Sensitivity.Internal ? 2
                    : (r.File.OverrideSensitivity ?? r.File.Sensitivity) == Sensitivity.Public ? 1
                    : 0)
                .ThenBy(r => r.ShareName).ThenBy(r => r.FolderPath).ThenBy(r => r.File.NameKey)
                .Skip(o).Take(l)
                .ToListAsync(ct);
            var categories = await CategoriesAsync(db, page.Select(r => r.File.Id).ToList(), ct);
            var standards = await ComplianceScope.LoadAsync(db, ct);
            var items = page.Select(r => ToDto(r, categories.GetValueOrDefault(r.File.Id), standards)).ToList();
            return Results.Ok(new Page<ClassifiedFileDto>(items, l, o, total));
        });

        api.MapGet("/files/{id:guid}/classification", async (Guid id, ClaimsPrincipal user, IAuthorizationService authz, DataScopeDbContext db, CancellationToken ct) =>
            await DetailAsync(id, user, authz, db, ct) is { } detail ? Results.Ok(detail) : Results.NotFound())
            .RequireAuthorization(Policies.ReadPosture);

        api.MapPost("/files/{id:guid}/classification-override", async (
            Guid id, OverrideRequest request, ClaimsPrincipal user, IAuthorizationService authz, DataScopeDbContext db,
            AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var label = Parse<Sensitivity>(request.Sensitivity, "sensitivity", errors);
            if (label == Sensitivity.Unknown)
            {
                errors["sensitivity"] = ["Choose Public, Internal, Confidential or Restricted, or send null to remove the override."];
            }
            var reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length < 3 || reason.Length > 500)
            {
                errors["reason"] = ["Give a reason of 3 to 500 characters. It is kept in the audit log."];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (file is null)
            {
                return Results.NotFound();
            }
            if (file.OverrideSensitivity == label && label is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sensitivity"] = ["This file has no override to remove."] });
            }

            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var previous = file.OverrideSensitivity;
            file.OverrideSensitivity = label;
            db.ClassificationOverrides.Add(new ClassificationOverride
            {
                FileId = file.Id,
                Sensitivity = label,
                PolicySensitivity = file.Sensitivity,
                Reason = reason!,
                ReviewerSid = actor.Sid,
                ReviewerName = actor.Name,
                At = now,
            });
            var target = await Rows(db, db.Files.Where(f => f.Id == id)).Select(r => new { r.Hostname, r.ShareName, r.FolderPath }).SingleAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, label is null ? "classification.override.remove" : "classification.override",
                UncPath(target.Hostname, target.ShareName, target.FolderPath, file.Name),
                new
                {
                    FileId = file.Id,
                    From = (previous ?? file.Sensitivity).ToString(),
                    To = (label ?? file.Sensitivity).ToString(),
                    Policy = file.Sensitivity.ToString(),
                    Reason = reason,
                }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await DetailAsync(id, user, authz, db, ct));
        }).RequireAuthorization(Policies.ReviewClassification);
    }

    internal const string SourceProblem = "Use files for file shares, or leave it out for all.";

    /// <summary>The <c>source</c> filter (D146): <c>files</c>; empty means all.</summary>
    internal static bool TryParseSource(string? source, out SourceKind? kind)
    {
        kind = source?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "files" => SourceKind.FileServer,
            _ => (SourceKind)(-1),
        };
        return kind is null || Enum.IsDefined(kind.Value);
    }

    private static IQueryable<InventoryFile> Scope(DataScopeDbContext db, Guid? serverId, Guid? shareId, SourceKind? kind = null)
    {
        var files = db.Files.Where(f => f.RemovedAt == null);
        if (kind is { } k)
        {
            files = files.Where(f => db.Shares.Any(s => s.Id == f.ShareId && db.ManagedServers.Any(m => m.Id == s.ServerId && m.Kind == k)));
        }
        if (shareId is { } share)
        {
            files = files.Where(f => f.ShareId == share);
        }
        if (serverId is { } server)
        {
            files = files.Where(f => db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == server));
        }
        return files;
    }

    private static T? Parse<T>(string? value, string field, Dictionary<string, string[]> errors) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            return parsed;
        }
        errors[field] = [$"Unknown value. Use one of: {string.Join(", ", Enum.GetNames<T>())}."];
        return null;
    }

    /// <summary>A file's findings: each category's strongest confidence and its matches.</summary>
    private sealed record FileCategories(IReadOnlyList<(FindingCategory Category, Confidence Confidence)> Raw, IReadOnlyList<CategoryCount> Counts);

    private static async Task<Dictionary<Guid, FileCategories>> CategoriesAsync(DataScopeDbContext db, List<Guid> fileIds, CancellationToken ct)
    {
        var rows = await db.Findings.AsNoTracking()
            .Where(f => fileIds.Contains(f.FileId))
            .Select(f => new { f.FileId, f.Category, f.Confidence, f.MatchCount })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.FileId)
            .ToDictionary(g => g.Key, g =>
            {
                var byCategory = g.GroupBy(r => r.Category).ToList();
                return new FileCategories(
                    byCategory.Select(c => (c.Key, c.Max(x => x.Confidence))).ToList(),
                    byCategory
                        .Select(c => new CategoryCount(c.Key.Label(), Label(c.Max(x => x.Confidence)), c.Sum(x => x.MatchCount)))
                        .OrderByDescending(c => c.Confidence == "high")
                        .ThenBy(c => c.Category, StringComparer.Ordinal)
                        .ToList());
            });
    }

    /// <summary>Lower-case confidence, matching the web UI's type.</summary>
    private static string Label(Confidence c) => c.ToString().ToLowerInvariant();

    private static ClassifiedFileDto ToDto(Row r, FileCategories? categories, IReadOnlyList<EffectiveStandard> standards)
    {
        var f = r.File;
        var name = f.Name;
        return new ClassifiedFileDto(
            f.Id, f.ShareId, r.ShareName, r.Hostname, PathText.Child(r.FolderPath, name), name, f.Extension, f.Size,
            f.ModifiedAtSource, (f.OverrideSensitivity ?? f.Sensitivity).ToString(), f.Sensitivity.ToString(),
            f.OverrideSensitivity?.ToString(), f.ContentState.ToString(), f.ContentNote, f.ClassifiedAt, f.ClassifiedPolicy,
            categories?.Counts ?? [], f.RemovedAt != null,
            categories is null ? [] : ComplianceScope.For(standards, categories.Raw, f.OverrideSensitivity),
            SourceOf(r.Kind), r.FolderPath);
    }

    /// <summary>What an item is, for the web UI: a file on a share.</summary>
    internal static string SourceOf(SourceKind kind) => FileShareSource;

    private static string UncPath(string host, string share, string folder, string name) =>
        $@"\\{host}\{share}\{PathText.Child(folder, name)}";

    private static async Task<FileClassificationDto?> DetailAsync(Guid id, ClaimsPrincipal user, IAuthorizationService authz, DataScopeDbContext db, CancellationToken ct)
    {
        var row = await Rows(db, db.Files.Where(f => f.Id == id)).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var canSeeEvidence = (await authz.AuthorizeAsync(user, Policies.ReadEvidence)).Succeeded;
        var findings = await db.Findings.AsNoTracking().Where(f => f.FileId == id).OrderBy(f => f.RuleId).ToListAsync(ct);
        var rules = ClassificationPolicy.Rules.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
        foreach (var custom in await db.CustomRules.AsNoTracking().Select(r => new { r.Id, r.Name }).ToListAsync(ct))
        {
            rules[CustomRules.RuleId(custom.Id)] = custom.Name;
        }
        var overrides = await db.ClassificationOverrides.AsNoTracking().Where(o => o.FileId == id).OrderByDescending(o => o.Id).Take(50).ToListAsync(ct);
        var categories = await CategoriesAsync(db, [id], ct);
        var standards = await ComplianceScope.LoadAsync(db, ct);
        var latest = overrides.FirstOrDefault();
        return new FileClassificationDto(
            ToDto(row, categories.GetValueOrDefault(id), standards),
            findings.Select(f => new FindingDto(
                f.RuleId,
                rules.TryGetValue(f.RuleId, out var name) ? name : CustomRules.IsCustom(f.RuleId) ? "Custom rule (removed)" : f.RuleId,
                f.Category.Label(),
                Label(f.Confidence),
                f.MatchCount,
                canSeeEvidence ? f.Evidence : null,
                f.PolicyVersion,
                f.DiscoveredAt)).ToList(),
            !canSeeEvidence && findings.Count > 0,
            overrides.Select(o => new OverrideDto(o.Sensitivity?.ToString(), o.PolicySensitivity.ToString(), o.Reason, o.ReviewerName, o.At)).ToList(),
            latest?.Sensitivity is not null && row.File.ClassifiedAt > latest.At);
    }
}
