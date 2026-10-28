using DataScope.Api.Auth;
using DataScope.Api.Endpoints;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Cleanup;

/// <summary>
/// The Stale and duplicate data screen (D119): what could be archived or removed, and where. Read-only; nothing here
/// moves, changes or deletes a file.
/// </summary>
internal static class CleanupEndpoints
{
    /// <param name="InUse">Files older than the cutoff that file activity shows were opened or changed since; not stale.</param>
    /// <param name="NoDate">Files without a modified or created time, which can't be judged.</param>
    public sealed record TotalsDto(
        long Files, long Bytes, long StaleFiles, long StaleBytes, long StaleRestricted, long StaleConfidential, long InUse, long NoDate,
        long DuplicateGroups, long DuplicateCopies, long DuplicateBytes);

    /// <param name="Source">FileShare (D155).</param>
    public sealed record ShareDto(
        Guid ShareId, Guid ServerId, string UncPath, long Files, long Bytes, long StaleFiles, long StaleBytes, long StaleRestricted, long StaleConfidential,
        DateTimeOffset? LastScannedAt, string Source = ClassificationEndpoints.FileShareSource);

    public sealed record FolderDto(Guid FolderId, Guid ShareId, string UncPath, long StaleFiles, long StaleBytes, long Restricted, long Confidential, DateTimeOffset? NewestChange);

    /// <param name="ActivityServers">Servers in scope whose file activity Data Scope collects, so recent use can count.</param>
    public sealed record CleanupDto(
        int Years, int DefaultYears, DateTimeOffset Cutoff, TotalsDto Totals, IReadOnlyList<ShareDto> Shares, IReadOnlyList<FolderDto> Folders,
        int Servers, int ActivityServers, long MinDuplicateBytes, DateTimeOffset? LastScanAt);

    public sealed record StaleFileDto(Guid FileId, Guid ShareId, Guid FolderId, string Path, string Name, long Size, DateTimeOffset? LastChanged, string Label,
        string Source = ClassificationEndpoints.FileShareSource);

    public sealed record CopyDto(Guid FileId, Guid ShareId, string Path, DateTimeOffset? Modified, string Label);

    /// <param name="SameModified">Every copy also has the same last-modified time, which makes a copy more likely.</param>
    public sealed record DuplicateDto(string Name, long Size, int Copies, int Shares, long Wasted, bool SameModified, string Label, IReadOnlyList<CopyDto> Locations);

    public sealed record DuplicatesDto(IReadOnlyList<DuplicateDto> Items, int Limit, int Offset, int Total, long Copies, long Wasted, long MinDuplicateBytes);

    private static readonly string[] Sorts = ["size", "oldest"];

    public static void MapCleanup(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/cleanup").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/", async (Guid? serverId, Guid? shareId, int? years, string? source, DataCleanup cleanup, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (Problem(years, null, source, out var kind) is { } problem)
            {
                return problem;
            }
            if (await MissingAsync(db, serverId, shareId, ct))
            {
                return Results.NotFound();
            }
            var y = years ?? cleanup.Options.StaleAfterYears;
            var cutoff = cleanup.Cutoff(y);

            var all = await cleanup.Files(serverId, shareId, kind)
                .GroupBy(f => new { f.ShareId, f.ServerId, f.ShareUnc, f.Kind })
                .Select(g => new { g.Key.ShareId, g.Key.ServerId, g.Key.ShareUnc, g.Key.Kind, Files = g.LongCount(), Bytes = g.Sum(x => x.Size), NoDate = g.LongCount(x => x.Changed == null) })
                .ToListAsync(ct);
            var stale = await cleanup.Stale(serverId, shareId, cutoff, kind)
                .GroupBy(f => f.ShareId)
                .Select(g => new
                {
                    ShareId = g.Key,
                    Files = g.LongCount(),
                    Bytes = g.Sum(x => x.Size),
                    Restricted = g.LongCount(x => x.Label == Sensitivity.Restricted),
                    Confidential = g.LongCount(x => x.Label == Sensitivity.Confidential),
                })
                .ToDictionaryAsync(x => x.ShareId, ct);
            var inUse = await cleanup.InUse(serverId, shareId, cutoff, kind).LongCountAsync(ct);
            var shareIds = all.Select(s => s.ShareId).ToList();
            var scanned = await db.Shares.AsNoTracking().Where(s => shareIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.LastScannedAt, ct);

            var shares = all
                .Select(s =>
                {
                    var x = stale.GetValueOrDefault(s.ShareId);
                    return new ShareDto(s.ShareId, s.ServerId, s.ShareUnc, s.Files, s.Bytes, x?.Files ?? 0, x?.Bytes ?? 0, x?.Restricted ?? 0, x?.Confidential ?? 0,
                        scanned.GetValueOrDefault(s.ShareId), ClassificationEndpoints.SourceOf(s.Kind));
                })
                .OrderByDescending(s => s.StaleBytes).ThenBy(s => s.UncPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var folders = await cleanup.Stale(serverId, shareId, cutoff, kind)
                .GroupBy(f => new { f.FolderId, f.ShareId, f.ShareUnc, f.FolderPath })
                .Select(g => new
                {
                    g.Key.FolderId,
                    g.Key.ShareId,
                    g.Key.ShareUnc,
                    g.Key.FolderPath,
                    Files = g.LongCount(),
                    Bytes = g.Sum(x => x.Size),
                    Restricted = g.LongCount(x => x.Label == Sensitivity.Restricted),
                    Confidential = g.LongCount(x => x.Label == Sensitivity.Confidential),
                    Newest = g.Max(x => x.Changed),
                })
                .OrderByDescending(g => g.Bytes).ThenByDescending(g => g.Files)
                .Take(DataCleanup.MaxFolders)
                .ToListAsync(ct);

            var duplicates = cleanup.DuplicateGroups(serverId, shareId, kind);
            var dupGroups = await duplicates.LongCountAsync(ct);
            var dupCopies = dupGroups == 0 ? 0 : await duplicates.SumAsync(g => (long)g.Copies - 1, ct);
            var dupBytes = dupGroups == 0 ? 0 : await duplicates.SumAsync(g => g.Wasted, ct);

            var serverIds = all.Select(s => s.ServerId).Distinct().ToList();
            var collecting = await db.ActivitySources.AsNoTracking().Where(a => a.Enabled && serverIds.Contains(a.ServerId)).Select(a => a.ServerId).Distinct().CountAsync(ct);

            var totals = new TotalsDto(
                all.Sum(s => s.Files), all.Sum(s => s.Bytes), shares.Sum(s => s.StaleFiles), shares.Sum(s => s.StaleBytes),
                shares.Sum(s => s.StaleRestricted), shares.Sum(s => s.StaleConfidential), inUse, all.Sum(s => s.NoDate),
                dupGroups, dupCopies, dupBytes);
            return Results.Ok(new CleanupDto(
                y, cleanup.Options.StaleAfterYears, cutoff, totals, shares,
                folders.Select(f => new FolderDto(f.FolderId, f.ShareId, AccessEndpoints.UncOf(f.ShareUnc, f.FolderPath), f.Files, f.Bytes, f.Restricted, f.Confidential, f.Newest)).ToList(),
                serverIds.Count, collecting, cleanup.Options.MinDuplicateBytes, scanned.Values.Max()));
        });

        group.MapGet("/stale-files", async (
            Guid? serverId, Guid? shareId, Guid? folderId, int? years, string? sort, bool? sensitive, string? source, int? limit, int? offset,
            DataCleanup cleanup, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (Problem(years, sort, source, out var kind) is { } problem)
            {
                return problem;
            }
            if (await MissingAsync(db, serverId, shareId, ct) || (folderId is { } fo && !await db.Folders.AnyAsync(f => f.Id == fo, ct)))
            {
                return Results.NotFound();
            }
            var (take, skip) = Paging.Clamp(limit, offset);
            var q = cleanup.Stale(serverId, shareId, cleanup.Cutoff(years ?? cleanup.Options.StaleAfterYears), kind);
            if (folderId is { } folder)
            {
                q = q.Where(f => f.FolderId == folder);
            }
            if (sensitive == true)
            {
                q = q.Where(f => f.Label == Sensitivity.Restricted || f.Label == Sensitivity.Confidential);
            }
            var total = await q.CountAsync(ct);
            q = sort == "oldest"
                ? q.OrderBy(f => f.Changed).ThenByDescending(f => f.Size).ThenBy(f => f.Id)
                : q.OrderByDescending(f => f.Size).ThenBy(f => f.Changed).ThenBy(f => f.Id);
            var rows = await q.Skip(skip).Take(take).ToListAsync(ct);
            return Results.Ok(new Page<StaleFileDto>(
                rows.Select(f => new StaleFileDto(f.Id, f.ShareId, f.FolderId, DataCleanup.FolderUncOf(f), NameOf(f), f.Size, f.Changed, f.Label.ToString(),
                    ClassificationEndpoints.SourceOf(f.Kind))).ToList(),
                take, skip, total));
        });

        group.MapGet("/duplicates", async (Guid? serverId, Guid? shareId, string? source, int? limit, int? offset, DataCleanup cleanup, DataScopeDbContext db, CancellationToken ct) =>
        {
            if (Problem(null, null, source, out var kind) is { } problem)
            {
                return problem;
            }
            if (await MissingAsync(db, serverId, shareId, ct))
            {
                return Results.NotFound();
            }
            var (take, skip) = Paging.Clamp(limit, offset);
            var groups = cleanup.DuplicateGroups(serverId, shareId, kind);
            var total = await groups.CountAsync(ct);
            var copies = total == 0 ? 0 : await groups.SumAsync(g => (long)g.Copies - 1, ct);
            var wasted = total == 0 ? 0 : await groups.SumAsync(g => g.Wasted, ct);
            var page = await groups.OrderByDescending(g => g.Wasted).ThenByDescending(g => g.Copies).ThenBy(g => g.NameKey).ThenBy(g => g.Size)
                .Skip(skip).Take(take).ToListAsync(ct);
            var found = await cleanup.CopiesAsync(serverId, shareId, kind, page, ct);
            var items = page.Select(g =>
            {
                var all = found[(g.NameKey, g.Size)].ToList();
                return new DuplicateDto(
                    all.FirstOrDefault() is { } first ? NameOf(first) : g.NameKey, g.Size, g.Copies, g.Shares, g.Wasted,
                    all.Count > 1 && all.All(x => x.Modified is not null && x.Modified == all[0].Modified),
                    (all.Count == 0 ? Sensitivity.Unknown : all.Max(x => x.Label)).ToString(),
                    all.Take(DataCleanup.MaxLocations).Select(x => new CopyDto(x.Id, x.ShareId, DataCleanup.FolderUncOf(x), x.Modified, x.Label.ToString())).ToList());
            }).ToList();
            return Results.Ok(new DuplicatesDto(items, take, skip, total, copies, wasted, cleanup.Options.MinDuplicateBytes));
        });
    }

    private static string NameOf(CleanupFile f) => f.Name;

    private static IResult? Problem(int? years, string? sort, string? source, out SourceKind? kind)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ClassificationEndpoints.TryParseSource(source, out kind))
        {
            errors["source"] = [ClassificationEndpoints.SourceProblem];
        }
        if (years is < CleanupOptions.MinYears or > CleanupOptions.MaxYears)
        {
            errors["years"] = [$"Use {CleanupOptions.MinYears} to {CleanupOptions.MaxYears} years."];
        }
        if (sort is not null && !Sorts.Contains(sort))
        {
            errors["sort"] = ["Use size or oldest."];
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static async Task<bool> MissingAsync(DataScopeDbContext db, Guid? serverId, Guid? shareId, CancellationToken ct) =>
        (shareId is { } sh && !await db.Shares.AnyAsync(s => s.Id == sh && (serverId == null || s.ServerId == serverId), ct))
        || (serverId is { } sv && !await db.ManagedServers.AnyAsync(s => s.Id == sv, ct));
}
