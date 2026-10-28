using DataScope.Core.Classification;
using DataScope.Core.Compliance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Compliance;

/// <summary>A standard from the catalog with the administrator's choices applied (D126).</summary>
internal sealed record EffectiveStandard(
    ComplianceStandard Definition,
    bool Enabled,
    IReadOnlyList<FindingCategory> Categories,
    Confidence MinConfidence,
    string? Reason,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName)
{
    public string Id => Definition.Id;
    public string Name => Definition.Name;
}

/// <summary>Which files fall under which compliance standards. Computed from the current findings, never stored.</summary>
internal static class ComplianceScope
{
    public static async Task<List<EffectiveStandard>> LoadAsync(DataScopeDbContext db, CancellationToken ct)
    {
        var saved = await db.ComplianceStandards.AsNoTracking().ToDictionaryAsync(s => s.StandardId, ct);
        return ComplianceCatalog.Standards.Select(d => From(d, saved.GetValueOrDefault(d.Id))).ToList();
    }

    public static EffectiveStandard From(ComplianceStandard definition, ComplianceStandardSetting? saved)
    {
        if (saved is null)
        {
            return new EffectiveStandard(definition, false, definition.DefaultCategories, ComplianceCatalog.DefaultMinConfidence, null, null, null);
        }
        // A category name this version doesn't know (from a newer or older version) is ignored, never guessed.
        var categories = saved.Categories
            .Select(c => Enum.TryParse<FindingCategory>(c, out var v) && Enum.IsDefined(v) ? v : (FindingCategory?)null)
            .OfType<FindingCategory>().Distinct().ToList();
        // Saved with the defaults of an earlier version: the administrator never chose, so today's defaults apply.
        if (categories.Count == 0 || (definition.EarlierDefaults ?? []).Any(d => d.ToHashSet().SetEquals(categories)))
        {
            categories = [.. definition.DefaultCategories];
        }
        return new EffectiveStandard(definition, saved.Enabled, categories,
            saved.MinConfidence, saved.Reason, saved.UpdatedAt, saved.UpdatedByName);
    }

    /// <summary>
    /// Files under a standard: a finding in one of its categories at or above its confidence, unless a reviewer set the
    /// file's label below Confidential. Matches <see cref="ComplianceCatalog.Covers"/>.
    /// </summary>
    public static IQueryable<InventoryFile> Covered(DataScopeDbContext db, IQueryable<InventoryFile> files, EffectiveStandard standard)
    {
        var categories = standard.Categories.ToList();
        var confidences = Enum.GetValues<Confidence>().Where(c => c >= standard.MinConfidence).ToList();
        return files.Where(f =>
            (f.OverrideSensitivity == null || f.OverrideSensitivity == Sensitivity.Confidential || f.OverrideSensitivity == Sensitivity.Restricted)
            && db.Findings.Any(x => x.FileId == f.Id && categories.Contains(x.Category) && confidences.Contains(x.Confidence)));
    }

    /// <summary>The ids of the turned-on standards a file falls under, in catalog order.</summary>
    public static IReadOnlyList<string> For(
        IEnumerable<EffectiveStandard> standards, IEnumerable<(FindingCategory Category, Confidence Confidence)> findings, Sensitivity? overrideSensitivity)
    {
        var list = findings.ToList();
        return standards
            .Where(s => s.Enabled && ComplianceCatalog.Covers([.. s.Categories], s.MinConfidence, list, overrideSensitivity))
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// The turned-on standard a <c>standard</c> query parameter names, or the reason it can't be used. Null input
    /// means no filter.
    /// </summary>
    public static async Task<(EffectiveStandard? Standard, string? Problem)> FilterAsync(DataScopeDbContext db, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return (null, null);
        }
        if (ComplianceCatalog.Find(id) is not { } definition)
        {
            return (null, $"Unknown standard. Use one of: {string.Join(", ", ComplianceCatalog.Standards.Select(s => s.Id))}.");
        }
        var saved = await db.ComplianceStandards.AsNoTracking().FirstOrDefaultAsync(s => s.StandardId == definition.Id, ct);
        var standard = From(definition, saved);
        return standard.Enabled ? (standard, null) : (null, $"{definition.Name} is turned off. Turn it on in Settings, Compliance standards.");
    }
}
