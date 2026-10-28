using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

// Matching runs in PostgreSQL, where ToUpper() becomes upper(); no .NET culture is involved.
#pragma warning disable CA1304, CA1311, CA1862

/// <summary>What an exclusion leaves out, from the latest inventory: last known numbers, not a fresh look at the server.</summary>
/// <param name="TotalFiles">Files on the share (a folder) or on every included share (a file type), to compare with.</param>
public sealed record ScopeImpact(long Files, long Bytes, long Folders, long Restricted, long Confidential, long TotalFiles);

/// <summary>Scan exclusions (D124): the rules a scan applies, and what they cover in the inventory.</summary>
public sealed class ScanScopeStore(DataScopeDbContext db)
{
    private const char Escape = '!';

    /// <summary>The enabled exclusions for each included share on a server, as the scan applies them.</summary>
    public async Task<Dictionary<Guid, ShareScope>> ScopesAsync(Guid serverId, CancellationToken ct)
    {
        var shares = await db.Shares.AsNoTracking().Where(s => s.ServerId == serverId && s.Included).Select(s => s.Id).ToListAsync(ct);
        var rules = await RulesAsync(shares, ct);
        return shares.ToDictionary(id => id, id => new ShareScope(rules.Where(r => r.ShareId is null || r.ShareId == id)));
    }

    /// <summary>The enabled exclusions that apply to any of these shares.</summary>
    public async Task<List<ScanExclusionRule>> RulesAsync(IReadOnlyCollection<Guid> shareIds, CancellationToken ct) =>
        await db.ScanExclusions.AsNoTracking()
            .Where(x => x.Enabled && (x.ShareId == null || shareIds.Contains(x.ShareId.Value)))
            .OrderBy(x => x.Id)
            .Select(x => new ScanExclusionRule(x.Id, x.Kind, x.Effect, x.ShareId, x.Path, x.Extension, x.Reason))
            .ToListAsync(ct);

    /// <summary>
    /// Makes files this job saw that an exclusion now covers due for the read phase, which records them as excluded
    /// instead of keeping a result from before the exclusion. Returns how many.
    /// </summary>
    public async Task<int> RecheckExcludedAsync(Guid jobId, IReadOnlyList<ScanExclusionRule> rules, CancellationToken ct)
    {
        var changed = 0;
        foreach (var rule in rules)
        {
            var files = Covered(rule).Where(f => f.LastSeenJobId == jobId && f.ContentState != ContentState.Excluded && f.ClassifiedPolicy != null);
            changed += await files.ExecuteUpdateAsync(s => s.SetProperty(f => f.ClassifiedPolicy, (string?)null), ct);
        }
        return changed;
    }

    /// <summary>What an exclusion covers in the current inventory.</summary>
    public async Task<ScopeImpact> ImpactAsync(ScanExclusionKind kind, Guid? shareId, string? path, string? extension, CancellationToken ct)
    {
        var rule = new ScanExclusionRule(0, kind, ScanExclusionEffect.ListOnly, shareId, path, extension, "");
        var files = Covered(rule);
        var stats = await files.GroupBy(_ => 1).Select(g => new
        {
            Files = g.LongCount(),
            Bytes = g.Sum(f => (long?)f.Size) ?? 0,
            Restricted = g.LongCount(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Restricted),
            Confidential = g.LongCount(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Confidential),
        }).FirstOrDefaultAsync(ct);
        var folders = kind == ScanExclusionKind.Folder ? await FoldersAt(shareId!.Value, path!).LongCountAsync(ct) : 0;
        var total = await db.Files.LongCountAsync(f => f.RemovedAt == null
            && (shareId == null ? db.Shares.Any(s => s.Id == f.ShareId && s.Included) : f.ShareId == shareId), ct);
        return new ScopeImpact(stats?.Files ?? 0, stats?.Bytes ?? 0, folders, stats?.Restricted ?? 0, stats?.Confidential ?? 0, total);
    }

    /// <summary>Current files a rule covers.</summary>
    private IQueryable<InventoryFile> Covered(ScanExclusionRule rule)
    {
        var live = db.Files.Where(f => f.RemovedAt == null);
        if (rule.Kind == ScanExclusionKind.FileType)
        {
            var ext = rule.Extension!;
            return rule.ShareId is { } only
                ? live.Where(f => f.ShareId == only && f.Extension == ext)
                : live.Where(f => f.Extension == ext && db.Shares.Any(s => s.Id == f.ShareId && s.Included));
        }
        var share = rule.ShareId!.Value;
        var folderIds = FoldersAt(share, rule.Path!).Select(d => d.Id);
        return live.Where(f => f.ShareId == share && folderIds.Contains(f.FolderId));
    }

    /// <summary>Current folders at or below a share-relative path, ignoring case.</summary>
    private IQueryable<Folder> FoldersAt(Guid shareId, string path)
    {
        var upper = path.ToUpperInvariant();
        var below = EscapeLike(upper) + "\\%";
        return db.Folders.Where(d => d.ShareId == shareId && d.RemovedAt == null
            && (d.Path.ToUpper() == upper || EF.Functions.Like(d.Path.ToUpper(), below, Escape.ToString())));
    }

    private static string EscapeLike(string s) =>
        s.Replace(Escape.ToString(), $"{Escape}{Escape}", StringComparison.Ordinal)
            .Replace("%", $"{Escape}%", StringComparison.Ordinal)
            .Replace("_", $"{Escape}_", StringComparison.Ordinal);
}
