using System.Diagnostics;
using System.Text.Json;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Scanner;
using DataScope.Scanner.Connectors;
using DataScope.Scanner.Content;
using DataScope.Scanner.Security;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Worker;

/// <summary>
/// Bounds that keep one scan from exhausting the server, the database or the worker. The two rates throttle a
/// scan on a busy file server: folders listed per second in the walk, and files opened per second in the
/// classify phase. Zero means no limit.
/// </summary>
public sealed record ScanLimits(int MaxDepth, int MaxEntriesPerFolder, long MaxEntriesPerJob, TimeSpan ReportInterval, int ClassifyBatch = 100,
    int MaxFoldersPerSecond = 0, int MaxFilesReadPerSecond = 0)
{
    public static ScanLimits From(IConfiguration config)
    {
        var limits = new ScanLimits(
            config.GetValue("Scanner:MaxDepth", 64),
            config.GetValue("Scanner:MaxEntriesPerFolder", 200_000),
            config.GetValue("Scanner:MaxEntriesPerJob", 20_000_000L),
            TimeSpan.FromMilliseconds(config.GetValue("Scanner:ReportMilliseconds", 1000)),
            MaxFoldersPerSecond: config.GetValue("Scanner:MaxFoldersPerSecond", 0),
            MaxFilesReadPerSecond: config.GetValue("Scanner:MaxFilesReadPerSecond", 0));
        if (limits.MaxFoldersPerSecond < 0 || limits.MaxFilesReadPerSecond < 0)
        {
            throw new InvalidOperationException("Scanner:MaxFoldersPerSecond and Scanner:MaxFilesReadPerSecond must be 0 (no limit) or more.");
        }
        return limits;
    }
}

/// <summary>
/// A scan in two phases. Walk: lists the server's shares, then walks every included share folder by folder and
/// records folder and file metadata, never following junctions or symbolic links. Classify: reads the contents of
/// new and changed files (every file in a full scan, D130) read-only, through bounded readers, and stores possible
/// sensitive-data matches with redacted evidence. Anything either phase cannot cover is recorded, never treated as clean.
/// Listing and reading go through <see cref="FileShareConnector"/>, built from the share enumerator and directory source.
/// </summary>
public sealed partial class InventoryScanExecutor(
    IServiceScopeFactory scopes,
    IShareEnumerator shareEnumerator,
    IDirectorySource directories,
    ScanLimits limits,
    ILogger<InventoryScanExecutor> log,
    IContentReader? contentReader = null,
    ISecurityReader? securityReader = null,
    TimeProvider? time = null,
    Func<ScanLimits>? currentLimits = null,
    FullScanContentReader? fullScanReader = null) : IScanExecutor
{
    private readonly FileShareConnector _files = new(shareEnumerator, directories);

    /// <summary>The throttles as they are now: changed on System settings, they apply when a scan starts or resumes (D123).</summary>
    private ScanLimits Throttles => currentLimits?.Invoke() ?? limits;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const string WalkCheckpoint = """{"phase":"walk"}""";

    public async Task<ScanControl> RunAsync(ScanContext context, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataScopeDbContext>();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryStore>();
        var jobId = context.JobId;
        var server = await db.ScanJobs.Where(j => j.Id == jobId)
            .Join(db.ManagedServers, j => j.ServerId, s => s.Id, (j, s) => new { s.Id })
            .SingleAsync(ct);
        var serverId = server.Id;
        var connector = _files;
        var security = securityReader;

        // Shares are listed on every start, including resumes, so the walk uses current access paths
        // and notices shares that disappeared while the job was paused.
        IReadOnlyList<ShareInfo> found;
        try
        {
            found = await connector.ListLocationsAsync(context.Hostname, ct);
        }
        catch (ShareEnumerationException ex)
        {
            await inventory.MarkServerErrorAsync(serverId, ex.Message, ct);
            throw;
        }
        await inventory.SyncSharesAsync(serverId, context.Hostname, found.Select(s => new DiscoveredShare(s.Name, s.Remark, s.Kind)).ToList(), ct);
        var roots = found.GroupBy(s => ShareName.Key(s.Name)).ToDictionary(g => g.Key, g => g.First().AccessPath);
        if (security is not null)
        {
            await ReadServerSecurityAsync(db, inventory, serverId, jobId, context.Hostname, security, ct);
        }

        // Exclusions are read on every start too, so one added or turned off while a scan was paused applies when it resumes (D124).
        var scanScopes = await scope.ServiceProvider.GetRequiredService<ScanScopeStore>().ScopesAsync(serverId, ct);

        var checkpoint = Checkpoint.Parse(context.Checkpoint);
        if (checkpoint?.Phase == Checkpoint.Classify)
        {
            return await ClassifyAsync(context, serverId, connector, roots, scanScopes, checkpoint.After, ct);
        }

        var resuming = checkpoint?.Phase == Checkpoint.Walk;
        if (!resuming)
        {
            var seeded = await inventory.SeedAsync(jobId, serverId, ct);
            LogSeeded(jobId, seeded);
            var control = await context.Report(0, seeded, WalkCheckpoint, ct);
            if (control != ScanControl.Continue)
            {
                return control;
            }
        }

        var (walked, pending) = await inventory.ProgressAsync(jobId, ct);
        var entries = walked + (resuming ? await inventory.EntriesSeenAsync(jobId, ct) : 0);
        var sinceReport = Stopwatch.StartNew();
        var unreadable = new UnreadableFolders();
        var pacer = new Pacer(Throttles.MaxFoldersPerSecond, _time);

        while (await inventory.NextAsync(jobId, ct) is { } folder)
        {
            ct.ThrowIfCancellationRequested();
            await pacer.WaitAsync(ct);
            if (!roots.TryGetValue(ShareName.Key(folder.ShareName), out var root))
            {
                var dropped = await inventory.DropShareFrontierAsync(jobId, folder.ShareId, ct);
                await inventory.AddGapAsync(jobId, folder.ShareId, "", GapKind.ShareUnavailable,
                    $"The share disappeared during the scan; {dropped} folders were not listed.", ct);
                continue;
            }
            if (entries >= limits.MaxEntriesPerJob)
            {
                var dropped = await inventory.ClearFrontierAsync(jobId, ct);
                await inventory.AddGapAsync(jobId, folder.ShareId, folder.Path, GapKind.JobEntryLimit,
                    $"Stopped after {limits.MaxEntriesPerJob:N0} entries; {dropped:N0} folders were not listed.", ct);
                break;
            }

            var shareScope = scanScopes.GetValueOrDefault(folder.ShareId) ?? ShareScope.Everything;
            if (folder.Depth > 0 && shareScope.Skipped(folder.Path) is { } skippedRule)
            {
                // Queued before the exclusion was added: leave it unlisted, and say so.
                await inventory.DropFolderAsync(folder.Seq, ct);
                await inventory.AddGapAsync(jobId, folder.ShareId, folder.Path, GapKind.Excluded, ExcludedMessage(skippedRule), ct);
                continue;
            }

            var listing = await connector.ListFolderAsync(root, folder.Path, limits.MaxEntriesPerFolder, ct);
            var result = Interpret(folder, listing);
            var holdBack = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sub in result.Subfolders)
            {
                var childPath = PathText.Child(folder.Path, sub.Name);
                if (shareScope.Skipped(childPath) is { } rule)
                {
                    holdBack.Add(sub.Name);
                    result.Gaps.Add(new DiscoveredGap(childPath, GapKind.Excluded, ExcludedMessage(rule)));
                }
            }
            var folderSecurity = security is null || listing.Gap == GapKind.NotFound
                ? null
                : ReadFolderSecurity(security, FileShareConnector.FullPath(root, folder.Path), unreadable);
            await inventory.CompleteFolderAsync(jobId, folder, result.Subfolders, result.Files, result.Gaps,
                listedCompletely: listing.Gap is null, enqueueSubfolders: folder.Depth < limits.MaxDepth, ct, folderSecurity, holdBack);
            walked++;
            entries += 1 + result.Subfolders.Count + result.Files.Count;

            if (sinceReport.Elapsed >= limits.ReportInterval)
            {
                sinceReport.Restart();
                (_, pending) = await inventory.ProgressAsync(jobId, ct);
                var control = await context.Report(walked, walked + pending, WalkCheckpoint, ct);
                if (control != ScanControl.Continue)
                {
                    return control;
                }
            }
        }

        if (unreadable.Count > 0)
        {
            LogFoldersUnreadable(jobId, unreadable.Count, unreadable.FirstNote ?? "");
        }
        await inventory.FinalizeAsync(jobId, serverId, ct);
        if (contentReader is null)
        {
            await inventory.RecordHistoryAsync(jobId, ct);
            var final = await context.Report(walked, walked, WalkCheckpoint, ct);
            // A pause or cancel that arrives after the last folder is moot: the scan is complete.
            return final == ScanControl.LeaseLost ? final : ScanControl.Continue;
        }
        return await ClassifyAsync(context, serverId, connector, roots, scanScopes, after: null, ct);
    }

    /// <summary>The gap text for an excluded folder: the admin's reason, so people see why it is not covered.</summary>
    private static string ExcludedMessage(ScanExclusionRule rule) => $"Excluded from scans by an administrator: {rule.Reason}";

    /// <summary>
    /// Reads the server's local groups and the permissions of every included share. Both are often refused to a
    /// least-privilege scan account; the refusal is stored and access results show the resulting uncertainty.
    /// </summary>
    private async Task ReadServerSecurityAsync(DataScopeDbContext db, InventoryStore inventory, Guid serverId, Guid jobId, string hostname, ISecurityReader reader, CancellationToken ct)
    {
        var local = Guarded(() => reader.ReadLocalGroups(hostname), new LocalGroupsRead(DataScope.Core.Access.LocalGroupsState.Failed, []));
        await inventory.Permissions.ReplaceLocalGroupsAsync(serverId, jobId, new DiscoveredLocalGroups(local.State, local.Groups), ct);
        var shares = await db.Shares.AsNoTracking()
            .Where(s => s.ServerId == serverId && s.Included && s.Kind == ShareKind.Disk && s.State != Persistence.Entities.ShareState.Removed)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        foreach (var share in shares)
        {
            var read = Guarded(() => reader.ReadShare(hostname, share.Name), SecurityRead.Failed("Reading share permissions failed."));
            await inventory.Permissions.RecordShareAsync(share.Id, new DiscoveredSecurity(read.State, read.Descriptor), ct);
        }
        LogSecurity(jobId, local.State.ToString(), shares.Count);
    }

    private DiscoveredSecurity ReadFolderSecurity(ISecurityReader reader, string path, UnreadableFolders unreadable)
    {
        var read = Guarded(() => reader.ReadFolder(path), SecurityRead.Failed("Reading permissions failed."));
        if (read.State == DataScope.Core.Access.PermissionState.Unreadable)
        {
            unreadable.Count++;
            unreadable.FirstNote ??= read.Note;
        }
        return new DiscoveredSecurity(read.State, read.Descriptor);
    }

    /// <summary>Folders whose permissions failed to read for a reason other than a refusal, reported once per walk.</summary>
    private sealed class UnreadableFolders
    {
        public int Count { get; set; }
        public string? FirstNote { get; set; }
    }

    /// <summary>A permission-reading bug must not stop the inventory; it is logged by type only.</summary>
    private T Guarded<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSecurityFailed(ex.GetType().Name);
            return fallback;
        }
    }

    /// <summary>
    /// Reads every new or changed file seen by this job, or every file in a full scan with the full-scan limits, in id
    /// order, saving results a batch at a time.
    /// The checkpoint carries the last file id, so a paused or restarted job carries on from there.
    /// </summary>
    private async Task<ScanControl> ClassifyAsync(ScanContext context, Guid serverId, IScanConnector connector, Dictionary<string, string> roots, Dictionary<Guid, ShareScope> scanScopes, Guid? after, CancellationToken ct)
    {
        if (contentReader is null)
        {
            return ScanControl.Continue;
        }
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ClassificationStore>();
        var jobId = context.JobId;
        // Files read before an exclusion covered them are recorded as excluded now, not left with the old result.
        var rules = await scope.ServiceProvider.GetRequiredService<ScanScopeStore>().RulesAsync(scanScopes.Keys, ct);
        await scope.ServiceProvider.GetRequiredService<ScanScopeStore>().RecheckExcludedAsync(jobId, rules, ct);
        var full = context.Kind == ScanKind.Full;
        var reader = full ? fullScanReader?.Reader ?? contentReader : contentReader;
        // Custom detection rules are read on every start too: a change applies when a paused scan resumes, and files
        // classified with other rules are read again (D133).
        var custom = await CustomRuleStore.CurrentAsync(scope.ServiceProvider.GetRequiredService<DataScopeDbContext>(), ct);
        var total = await store.CountPendingAsync(jobId, after, full, custom.PolicyVersion, ct);
        LogClassifying(jobId, total);
        var control = await context.Report(0, total, Checkpoint.Format(Checkpoint.Classify, after), ct);
        if (control != ScanControl.Continue)
        {
            return control;
        }

        long done = 0;
        var sinceReport = Stopwatch.StartNew();
        var pacer = new Pacer(Throttles.MaxFilesReadPerSecond, _time);
        while (await store.NextBatchAsync(jobId, after, limits.ClassifyBatch, full, custom.PolicyVersion, ct) is { } batch)
        {
            var outcomes = new List<ClassificationOutcome>(batch.Files.Count);
            foreach (var candidate in batch.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (scanScopes.GetValueOrDefault(candidate.ShareId)?.ContentExcluded(candidate.FolderPath, candidate.Name) is { } rule)
                {
                    // Never opened: listed in the inventory, recorded as not read, and never counted as clean.
                    outcomes.Add(new ClassificationOutcome(candidate.FileId, ContentState.Excluded, $"Contents not read: excluded by an administrator. {rule.Reason}", []));
                }
                else if (roots.TryGetValue(ShareName.Key(candidate.ShareName), out var root))
                {
                    outcomes.Add(await ClassifyFileAsync(connector, candidate, root, reader, pacer, custom, ct));
                }
            }
            await store.SaveAsync(jobId, outcomes, custom.PolicyVersion, ct);
            after = batch.Through;
            done += batch.Files.Count;

            if (sinceReport.Elapsed >= limits.ReportInterval)
            {
                sinceReport.Restart();
                control = await context.Report(done, Math.Max(total, done), Checkpoint.Format(Checkpoint.Classify, after), ct);
                if (control != ScanControl.Continue)
                {
                    return control;
                }
            }
        }

        await scope.ServiceProvider.GetRequiredService<InventoryStore>().RecordHistoryAsync(jobId, ct);
        var final = await context.Report(done, done, Checkpoint.Format(Checkpoint.Classify, after), ct);
        return final == ScanControl.LeaseLost ? final : ScanControl.Continue;
    }

    private async Task<ClassificationOutcome> ClassifyFileAsync(IScanConnector connector, ClassificationCandidate file, string root, IContentReader reader, Pacer pacer, CustomRuleSet custom,
        CancellationToken ct)
    {
        ContentExtraction extraction;
        try
        {
            // Only files that are actually opened count against the read rate; skipped ones cost the server nothing.
            var skipped = reader.Precheck(file.Name, file.Size, (FileAttributes)file.Attributes);
            if (skipped is null)
            {
                await pacer.WaitAsync(ct);
            }
            extraction = skipped ?? await connector.ReadItemAsync(reader, root, file.FolderPath, file.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reader bug must not stop the scan. The message is not logged: parser messages can quote contents.
            LogReadFailed(file.FileId, ex.GetType().Name);
            return new ClassificationOutcome(file.FileId, ContentState.Unreadable, "Data Scope could not read this file because of an internal error.", []);
        }
        if (extraction.Text is not { } text)
        {
            return new ClassificationOutcome(file.FileId, extraction.State, extraction.Note, []);
        }
        // Text longer than one scan, such as an Outlook mailbox's messages, is scanned in pieces (D134).
        var scan = TextScanner.ScanLong(text, custom);
        if (scan.Truncated && extraction.State == ContentState.Scanned)
        {
            return new ClassificationOutcome(file.FileId, ContentState.Partial, $"Only the first {TextScanner.MaxLongScanChars:N0} characters were scanned.", scan.Findings);
        }
        return new ClassificationOutcome(file.FileId, extraction.State, extraction.Note, scan.Findings);
    }

    /// <summary>Resume point stored on the job: which phase, and for classify the last file id done.</summary>
    internal sealed record Checkpoint(string Phase, Guid? After)
    {
        public const string Walk = "walk";
        public const string Classify = "classify";

        public static Checkpoint? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var phase = doc.RootElement.TryGetProperty("phase", out var p) ? p.GetString() : null;
                Guid? after = doc.RootElement.TryGetProperty("after", out var a) && a.TryGetGuid(out var g) ? g : null;
                return phase is Walk or Classify ? new Checkpoint(phase, after) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Format(string phase, Guid? after) =>
            JsonSerializer.Serialize(new Dictionary<string, string?> { ["phase"] = phase, ["after"] = after?.ToString() });
    }

    private sealed record Interpreted(List<DiscoveredFolder> Subfolders, List<DiscoveredFile> Files, List<DiscoveredGap> Gaps);

    private Interpreted Interpret(FrontierFolder folder, DirectoryListing listing)
    {
        var subfolders = new List<DiscoveredFolder>();
        var files = new List<DiscoveredFile>();
        var gaps = new List<DiscoveredGap>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (listing.Gap is { } gap)
        {
            // A share root that cannot be listed at all means the whole share is uncovered.
            var kind = folder.Depth == 0 && listing.Entries.Count == 0 && gap is GapKind.AccessDenied or GapKind.NotFound or GapKind.IoError
                ? GapKind.ShareUnavailable
                : gap;
            gaps.Add(new DiscoveredGap(folder.Path, kind, listing.GapMessage ?? kind.ToString()));
        }

        foreach (var entry in listing.Entries)
        {
            var name = PathText.Sanitize(entry.Name, out var lossy);
            var path = PathText.Child(folder.Path, name);
            if (lossy)
            {
                gaps.Add(new DiscoveredGap(path, GapKind.UnrepresentableName, "Name contains characters that cannot be stored exactly; recorded with replacement characters."));
            }
            if (!seen.Add(name.ToUpperInvariant()))
            {
                gaps.Add(new DiscoveredGap(path, GapKind.UnrepresentableName, "Another entry in this folder differs only by letter case; only the first was recorded."));
                continue;
            }
            if (entry.IsDirectory && entry.IsReparsePoint)
            {
                // Junctions and directory symlinks can loop or point off the share. Files that are reparse
                // points (deduplicated, cloud placeholders) are safe to record because they are never opened.
                gaps.Add(new DiscoveredGap(path, GapKind.ReparsePointSkipped, "Junction, symbolic link or mount point; not followed."));
                continue;
            }
            if (entry.IsDirectory)
            {
                subfolders.Add(new DiscoveredFolder(name, entry.ModifiedUtc, (int)entry.Attributes));
            }
            else
            {
                files.Add(new DiscoveredFile(name, entry.Length, entry.CreatedUtc, entry.ModifiedUtc, (int)entry.Attributes));
            }
        }

        if (folder.Depth >= limits.MaxDepth && subfolders.Count > 0)
        {
            gaps.Add(new DiscoveredGap(folder.Path, GapKind.DepthLimit, $"{subfolders.Count} subfolders are deeper than {limits.MaxDepth} levels and were not listed."));
        }
        return new Interpreted(subfolders, files, gaps);
    }

    [LoggerMessage(LogLevel.Information, "Scan job {JobId}: walking {Shares} shares")]
    private partial void LogSeeded(Guid jobId, int shares);

    [LoggerMessage(LogLevel.Information, "Scan job {JobId}: checking {Files} new or changed files for sensitive data")]
    private partial void LogClassifying(Guid jobId, long files);

    [LoggerMessage(LogLevel.Information, "Scan job {JobId}: local groups {LocalGroups}; read permissions for {Shares} shares")]
    private partial void LogSecurity(Guid jobId, string localGroups, int shares);

    [LoggerMessage(LogLevel.Warning, "Scan job {JobId}: the permissions of {Folders} folders could not be read. First reason: {Reason}")]
    private partial void LogFoldersUnreadable(Guid jobId, int folders, string reason);



    [LoggerMessage(LogLevel.Warning, "Reading permissions failed unexpectedly ({ExceptionType})")]
    private partial void LogSecurityFailed(string exceptionType);

    [LoggerMessage(LogLevel.Warning, "Reading file {FileId} failed unexpectedly ({ExceptionType})")]
    private partial void LogReadFailed(Guid fileId, string exceptionType);
}
