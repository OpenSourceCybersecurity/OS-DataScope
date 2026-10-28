using DataScope.Api.Endpoints;
using DataScope.Core.Activity;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Cleanup;

// The queries here run in PostgreSQL, where ToUpper() becomes upper(); no .NET culture is involved.
#pragma warning disable CA1304, CA1311

/// <summary>Settings for stale and duplicate data (D119), from the <c>Cleanup</c> section.</summary>
public sealed class CleanupOptions
{
    public const string Section = "Cleanup";

    public const int MinYears = 1;
    /// <summary>Long enough for retention holds such as 25 years for some records.</summary>
    public const int MaxYears = 30;

    /// <summary>Files nobody has changed for this long are stale, unless file activity shows them in use.</summary>
    public int StaleAfterYears { get; set; } = 3;

    /// <summary>Smaller files are left out of duplicates: they cost almost nothing and are mostly shortcuts and settings.</summary>
    public long MinDuplicateBytes { get; set; } = 1024;

    public bool IsValid => StaleAfterYears is >= MinYears and <= MaxYears && MinDuplicateBytes >= 1;
}

/// <summary>A current file in scope, with what stale and duplicate checks need.</summary>
internal sealed class CleanupFile
{
    public Guid Id { get; init; }
    public Guid ServerId { get; init; }
    public Guid ShareId { get; init; }
    public Guid FolderId { get; init; }
    public string ShareUnc { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string Name { get; init; } = "";
    public string NameKey { get; init; } = "";

    /// <summary>The path inside the share, upper-cased, as file activity events give it.</summary>
    public string PathKey { get; init; } = "";

    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }

    /// <summary>The later of last modified and created: a copy keeps the original's modified time but is new.</summary>
    public DateTimeOffset? Changed { get; init; }

    public Sensitivity Label { get; init; }
    public int Attributes { get; init; }

    /// <summary>The kind of source the file is on (D155).</summary>
    public SourceKind Kind { get; init; }
}

/// <summary>
/// Stale and duplicate data (D119), worked out from the latest scan and file activity on each request. Read-only:
/// Data Scope reports what could be archived or removed; it never moves or deletes a file.
/// </summary>
internal sealed class DataCleanup(DataScopeDbContext db, IOptionsMonitor<CleanupOptions> options, TimeProvider clock)
{
    /// <summary>Folders the summary lists, most stale data first.</summary>
    public const int MaxFolders = 50;

    /// <summary>Copies listed for one duplicate group; the count covers them all.</summary>
    public const int MaxLocations = 20;

    private const int HiddenOrSystem = (int)(FileAttributes.Hidden | FileAttributes.System);

    /// <summary>Read once per request, so a change on System settings applies to the next one (D123).</summary>
    public CleanupOptions Options { get; } = options.CurrentValue;

    public DateTimeOffset Cutoff(int years) => clock.GetUtcNow().AddYears(-years);

    /// <summary>Current files on shares in scope that still exist; with <paramref name="kind"/>, only that source's (D155).</summary>
    public IQueryable<CleanupFile> Files(Guid? serverId, Guid? shareId, SourceKind? kind = null) =>
        from f in db.Files.AsNoTracking()
        join fo in db.Folders on f.FolderId equals fo.Id
        join s in db.Shares on f.ShareId equals s.Id
        where f.RemovedAt == null && s.State != ShareState.Removed && s.Kind == ShareKind.Disk
            && (serverId == null || s.ServerId == serverId) && (shareId == null || s.Id == shareId)
            && (kind == null || s.Server!.Kind == kind)
        select new CleanupFile
        {
            Id = f.Id,
            ServerId = s.ServerId,
            ShareId = s.Id,
            FolderId = fo.Id,
            ShareUnc = s.UncPath,
            FolderPath = fo.Path,
            Name = f.Name,
            NameKey = f.NameKey,
            PathKey = (fo.Path == "" ? f.Name : fo.Path + "\\" + f.Name).ToUpper(),
            Size = f.Size,
            Modified = f.ModifiedAtSource,
            Changed = f.CreatedAtSource == null || f.ModifiedAtSource > f.CreatedAtSource ? f.ModifiedAtSource : f.CreatedAtSource,
            Label = f.OverrideSensitivity ?? f.Sensitivity,
            Attributes = f.Attributes,
            Kind = s.Server!.Kind,
        };

    /// <summary>
    /// Files Windows recorded someone opening, changing or running since the cutoff, as share and upper-cased path. Only
    /// events that prove the operation count (4663, Performed and Success); access checks and handle requests do not (D97).
    /// </summary>
    private IQueryable<UsedFile> UsedSince(DateTimeOffset cutoff) =>
        db.FileAuditEvents.AsNoTracking()
            .Where(e => e.EventTime >= cutoff && e.ShareId != null && e.RelativePath != null
                && e.ObjectKind == ActivityObjectKind.File && e.Certainty == ActivityCertainty.Performed && e.Outcome == ActivityOutcome.Success
                && (e.Operation == ActivityOperation.Read || e.Operation == ActivityOperation.Write || e.Operation == ActivityOperation.Execute))
            .Select(e => new UsedFile { ShareId = e.ShareId!.Value, PathKey = e.RelativePath!.ToUpper() });

    /// <summary>Files not changed since the cutoff, whether or not file activity shows them in use.</summary>
    public IQueryable<CleanupFile> Old(Guid? serverId, Guid? shareId, DateTimeOffset cutoff, SourceKind? kind = null) =>
        Files(serverId, shareId, kind).Where(f => f.Changed != null && f.Changed < cutoff);

    /// <summary>Old files that file activity shows were opened, changed or run since the cutoff.</summary>
    public IQueryable<CleanupFile> InUse(Guid? serverId, Guid? shareId, DateTimeOffset cutoff, SourceKind? kind = null)
    {
        var used = UsedSince(cutoff);
        return Old(serverId, shareId, cutoff, kind).Where(f => used.Any(u => u.ShareId == f.ShareId && u.PathKey == f.PathKey));
    }

    /// <summary>Old files with no recorded use since the cutoff: candidates to archive or delete.</summary>
    public IQueryable<CleanupFile> Stale(Guid? serverId, Guid? shareId, DateTimeOffset cutoff, SourceKind? kind = null)
    {
        var used = UsedSince(cutoff);
        return Old(serverId, shareId, cutoff, kind).Where(f => !used.Any(u => u.ShareId == f.ShareId && u.PathKey == f.PathKey));
    }

    /// <summary>Files that count for duplicates: big enough, and not hidden or system files such as Thumbs.db.</summary>
    private IQueryable<CleanupFile> DuplicateCandidates(Guid? serverId, Guid? shareId, SourceKind? kind) =>
        Files(serverId, shareId, kind).Where(f => f.Size >= Options.MinDuplicateBytes && (f.Attributes & HiddenOrSystem) == 0);

    /// <summary>Groups of files with the same name and size, largest waste first.</summary>
    public IQueryable<DuplicateKey> DuplicateGroups(Guid? serverId, Guid? shareId, SourceKind? kind = null) =>
        DuplicateCandidates(serverId, shareId, kind)
            .GroupBy(f => new { f.NameKey, f.Size })
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateKey
            {
                NameKey = g.Key.NameKey,
                Size = g.Key.Size,
                Copies = g.Count(),
                Shares = g.Select(x => x.ShareId).Distinct().Count(),
                Wasted = (g.Count() - 1) * g.Key.Size,
            });

    /// <summary>Every copy in the given groups, in scope.</summary>
    public async Task<ILookup<(string NameKey, long Size), CleanupFile>> CopiesAsync(Guid? serverId, Guid? shareId, SourceKind? kind, IReadOnlyCollection<DuplicateKey> groups, CancellationToken ct)
    {
        var sizes = groups.Select(g => g.Size).Distinct().ToList();
        var names = groups.Select(g => g.NameKey).Distinct().ToList();
        var wanted = groups.Select(g => (g.NameKey, g.Size)).ToHashSet();
        var files = await DuplicateCandidates(serverId, shareId, kind).Where(f => sizes.Contains(f.Size) && names.Contains(f.NameKey))
            .OrderBy(f => f.ShareUnc).ThenBy(f => f.FolderPath).ToListAsync(ct);
        return files.Where(f => wanted.Contains((f.NameKey, f.Size))).ToLookup(f => (f.NameKey, f.Size));
    }

    public static string UncOf(CleanupFile f) => AccessEndpoints.UncOf(f.ShareUnc, f.FolderPath, f.Name);

    public static string FolderUncOf(CleanupFile f) => AccessEndpoints.UncOf(f.ShareUnc, f.FolderPath);
}

internal sealed class UsedFile
{
    public Guid ShareId { get; init; }
    public string PathKey { get; init; } = "";
}

/// <summary>A group of files with the same name and size.</summary>
internal sealed class DuplicateKey
{
    public string NameKey { get; init; } = "";
    public long Size { get; init; }

    /// <summary>Files in the group, in scope.</summary>
    public int Copies { get; init; }

    /// <summary>Shares the copies are in.</summary>
    public int Shares { get; init; }

    /// <summary>Space the extra copies take: every copy but one.</summary>
    public long Wasted { get; init; }
}
