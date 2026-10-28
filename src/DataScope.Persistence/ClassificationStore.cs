using DataScope.Core.Classification;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>A file the classify phase should read: where it is and what the walk saw.</summary>
public sealed record ClassificationCandidate(
    Guid FileId,
    Guid ShareId,
    string ShareName,
    string FolderPath,
    string Name,
    long Size,
    DateTimeOffset? ModifiedAtSource,
    int Attributes);

/// <summary>
/// The files to read from one window of the inventory, and the last file id the window covered (the resume point),
/// which can be past the last candidate or set when there are no candidates at all.
/// </summary>
public sealed record ClassificationBatch(IReadOnlyList<ClassificationCandidate> Files, Guid Through);

/// <summary>What reading and scanning one file produced.</summary>
public sealed record ClassificationOutcome(Guid FileId, ContentState State, string? Note, IReadOnlyList<Finding> Findings);

/// <summary>
/// Stores classification results. In a quick scan a file is read again only when it changed (size or modified time),
/// the policy version changed, or the last attempt failed for a reason that may pass (access denied, offline, I/O,
/// timeout). A full scan reads every file again (D130).
/// </summary>
public sealed class ClassificationStore(DataScopeDbContext db, TimeProvider clock)
{
    /// <summary>Outcomes that hold until the file or the policy changes.</summary>
    private static readonly ContentState[] Settled =
        [ContentState.Scanned, ContentState.Partial, ContentState.Unsupported, ContentState.TooLarge, ContentState.Encrypted, ContentState.Unreadable];

    /// <summary>
    /// Outcomes that say nothing about the contents. The previous result, if any, is kept and the file is retried next
    /// scan. NotRead comes back when the file could not be read on this server for now, such as an image while text
    /// recognition is turned off or its libraries are missing (D129).
    /// </summary>
    public static bool IsTransient(ContentState state) =>
        state is ContentState.AccessDenied or ContentState.Offline or ContentState.NotFound or ContentState.IoError or ContentState.TimedOut
            or ContentState.NotRead;

    /// <param name="policy">The policy version in force: the built-in version plus the custom rules' fingerprint (D133).</param>
    private IQueryable<InventoryFile> Pending(Guid jobId, Guid? after, bool everyFile, string policy)
    {
        var files = db.Files.Where(f => f.LastSeenJobId == jobId && f.RemovedAt == null);
        if (!everyFile)
        {
            files = files.Where(f => !(f.ClassifiedPolicy == policy
                && f.ClassifiedSize == f.Size
                && f.ClassifiedModifiedAt == f.ModifiedAtSource
                && Settled.Contains(f.ContentState)));
        }
        return after is { } a ? files.Where(f => f.Id.CompareTo(a) > 0) : files;
    }

    /// <param name="everyFile">A full scan: every file the job saw, changed or not (D130).</param>
    /// <param name="policy">The version files are classified with now (<see cref="CustomRuleSet.PolicyVersion"/>); files classified with another are read again.</param>
    public Task<long> CountPendingAsync(Guid jobId, Guid? after, bool everyFile, string policy, CancellationToken ct) =>
        Pending(jobId, after, everyFile, policy).LongCountAsync(ct);

    /// <summary>
    /// The files to classify among the next <paramref name="window"/> files of the inventory, in id order after
    /// <paramref name="after"/>, so a resumed job carries on where it stopped. Null when no files are left. In a full
    /// scan (<paramref name="everyFile"/>) that is every file the job saw.
    /// </summary>
    /// <remarks>
    /// The window is taken by primary key first and only then filtered, so every batch costs the same however large
    /// the inventory is. Asking directly for "the next pending files of this job" relies on the planner knowing how
    /// many files carry the new job id, and right after a walk the table statistics still say none do, which turned
    /// each batch into a scan of the whole table (measured in the scale benchmark, docs/BENCHMARKS.md).
    /// </remarks>
    public async Task<ClassificationBatch?> NextBatchAsync(Guid jobId, Guid? after, int window, bool everyFile, string policy, CancellationToken ct)
    {
        // Deliberately unfiltered: any condition here lets the planner trade the primary key walk for a scan and sort.
        // Windows over other servers' files cost one index read each and yield no candidates.
        var scan = db.Files.AsQueryable();
        if (after is { } a)
        {
            scan = scan.Where(f => f.Id.CompareTo(a) > 0);
        }
        var ids = await scan.OrderBy(f => f.Id).Select(f => f.Id).Take(window).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return null;
        }
        // Three lookups by key rather than one join: with stale statistics the planner joins by scanning every folder
        // once per file.
        var files = await Pending(jobId, null, everyFile, policy).Where(f => ids.Contains(f.Id)).OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.ShareId, f.FolderId, f.Name, f.Size, f.ModifiedAtSource, f.Attributes })
            .ToListAsync(ct);
        if (files.Count == 0)
        {
            return new ClassificationBatch([], ids[^1]);
        }
        var folderIds = files.Select(f => f.FolderId).Distinct().ToList();
        var folders = await db.Folders.Where(d => folderIds.Contains(d.Id)).Select(d => new { d.Id, d.Path }).ToDictionaryAsync(d => d.Id, d => d.Path, ct);
        var shareIds = files.Select(f => f.ShareId).Distinct().ToList();
        var shares = await db.Shares.Where(s => shareIds.Contains(s.Id)).Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var candidates = files
            .Where(f => folders.ContainsKey(f.FolderId) && shares.ContainsKey(f.ShareId))
            .Select(f => new ClassificationCandidate(f.Id, f.ShareId, shares[f.ShareId], folders[f.FolderId], f.Name, f.Size, f.ModifiedAtSource, f.Attributes))
            .ToList();
        return new ClassificationBatch(candidates, ids[^1]);
    }

    /// <summary>Saves a batch of outcomes in one transaction, stamped with the policy version they were found with.</summary>
    public async Task SaveAsync(Guid jobId, IReadOnlyList<ClassificationOutcome> outcomes, string policy, CancellationToken ct)
    {
        if (outcomes.Count == 0)
        {
            return;
        }
        var now = clock.GetUtcNow();
        var ids = outcomes.Select(o => o.FileId).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var files = await db.Files.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var replaced = outcomes.Where(o => !IsTransient(o.State)).Select(o => o.FileId).ToList();
        await db.Findings.Where(f => replaced.Contains(f.FileId)).ExecuteDeleteAsync(ct);

        foreach (var o in outcomes)
        {
            if (!files.TryGetValue(o.FileId, out var file))
            {
                continue;
            }
            file.ContentState = o.State;
            file.ContentNote = Truncate(o.Note, 500);
            if (IsTransient(o.State))
            {
                continue;
            }
            var read = o.State is ContentState.Scanned or ContentState.Partial;
            file.Sensitivity = read ? ClassificationPolicy.Classify(o.Findings) : Sensitivity.Unknown;
            file.ClassifiedAt = now;
            file.ClassifiedJobId = jobId;
            file.ClassifiedPolicy = policy;
            file.ClassifiedSize = file.Size;
            file.ClassifiedModifiedAt = file.ModifiedAtSource;
            if (!read)
            {
                continue;
            }
            foreach (var f in o.Findings)
            {
                db.Findings.Add(new ClassificationFinding
                {
                    FileId = o.FileId,
                    JobId = jobId,
                    PolicyVersion = policy,
                    RuleId = f.RuleId,
                    Category = f.Category,
                    Confidence = f.Confidence,
                    MatchCount = f.Count,
                    Evidence = [.. f.Evidence.Select(e => Truncate(e, 400)!)],
                    DiscoveredAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // One context serves the whole classify phase. Without this every saved file stays tracked, and each save
        // checks all of them for changes, so batches got slower as the scan went on (found by the scale benchmark).
        db.ChangeTracker.Clear();
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
