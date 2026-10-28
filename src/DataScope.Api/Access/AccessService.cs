using System.Collections.Concurrent;
using DataScope.Core.Access;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DataScope.Api.Access;

/// <summary>
/// Process-wide caches for access computation. Directory graphs are keyed by snapshot and by the file server's local
/// groups, descriptors by their immutable id, and per-user results by everything they depend on, so a cached answer
/// is never stale: new data gets new keys.
/// </summary>
internal sealed class AccessCache : IDisposable
{
    public MemoryCache Graphs { get; } = new(new MemoryCacheOptions { SizeLimit = 6 });
    public MemoryCache Results { get; } = new(new MemoryCacheOptions { SizeLimit = 4000 });
    public ConcurrentDictionary<long, SecurityDescriptor> Descriptors { get; } = new();

    public void Dispose()
    {
        Graphs.Dispose();
        Results.Dispose();
    }
}

/// <summary>Permissions for one place, with the ids that identify them for caching.</summary>
internal sealed record PlacePermissions(ResourcePermissions Permissions, long? ShareSecurityId, long? FolderSecurityId)
{
    public string Key => $"{Permissions.ShareState}:{ShareSecurityId}|{Permissions.FolderState}:{FolderSecurityId}";
}

/// <summary>Loads directory graphs and permissions and computes access with caching.</summary>
internal sealed class AccessService(DataScopeDbContext db, DirectoryStore directory, AccessCache cache)
{
    private readonly Dictionary<Guid, DateTimeOffset?> _localGroupsReadAt = [];
    private DirectorySnapshot? _snapshot;
    private bool _snapshotLoaded;

    public async Task<DirectorySnapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (!_snapshotLoaded)
        {
            _snapshot = await directory.LatestCompletedAsync(ct);
            _snapshotLoaded = true;
        }
        return _snapshot;
    }

    /// <summary>The latest directory snapshot as a graph, with the local groups of one file server (or none).</summary>
    public async Task<(DirectoryGraph Graph, string Key)> GraphAsync(Guid? serverId, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(ct);
        DateTimeOffset? localReadAt = null;
        // Asked once per server per request: a page over thousands of folders would otherwise ask once per folder.
        if (serverId is { } id && !_localGroupsReadAt.TryGetValue(id, out localReadAt))
        {
            localReadAt = await db.ManagedServers.Where(s => s.Id == id).Select(s => s.LocalGroupsReadAt).SingleOrDefaultAsync(ct);
            _localGroupsReadAt[id] = localReadAt;
        }
        var key = $"{snapshot?.Id}|{serverId}|{localReadAt?.UtcTicks}";
        if (cache.Graphs.TryGetValue(key, out DirectoryGraph? graph) && graph is not null)
        {
            return (graph, key);
        }
        graph = await directory.LoadGraphAsync(snapshot, serverId, ct);
        cache.Graphs.Set(key, graph, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
        return (graph, key);
    }

    public async Task<SecurityDescriptor?> DescriptorAsync(long? id, CancellationToken ct)
    {
        if (id is not { } value)
        {
            return null;
        }
        if (cache.Descriptors.TryGetValue(value, out var cached))
        {
            return cached;
        }
        var raw = await db.SecurityDescriptors.Where(d => d.Id == value).Select(d => d.Raw).SingleOrDefaultAsync(ct);
        if (raw is null || !SecurityDescriptorCodec.TryParse(raw, out var sd) || sd is null)
        {
            return null;
        }
        if (cache.Descriptors.Count > 50_000)
        {
            cache.Descriptors.Clear();
        }
        cache.Descriptors[value] = sd;
        return sd;
    }

    public async Task<PlacePermissions> PermissionsAsync(PermissionState shareState, long? shareSecurityId, PermissionState folderState, long? folderSecurityId, CancellationToken ct)
    {
        var share = shareState == PermissionState.Read ? await DescriptorAsync(shareSecurityId, ct) : null;
        var folder = folderState == PermissionState.Read ? await DescriptorAsync(folderSecurityId, ct) : null;
        var perms = new ResourcePermissions(
            share is null && shareState == PermissionState.Read ? PermissionState.Unreadable : shareState, share,
            folder is null && folderState == PermissionState.Read ? PermissionState.Unreadable : folderState, folder);
        return new PlacePermissions(perms, shareSecurityId, folderSecurityId);
    }

    /// <summary>Every user with any possible access, computed once per distinct permission set.</summary>
    public IReadOnlyList<AccessResult> WhoHasAccess(DirectoryGraph graph, string graphKey, PlacePermissions place, AccessTarget target)
    {
        var key = $"who|{graphKey}|{place.Key}|{target}";
        if (cache.Results.TryGetValue(key, out IReadOnlyList<AccessResult>? list) && list is not null)
        {
            return list;
        }
        list = AccessCalculator.WhoHasAccess(graph, place.Permissions, target);
        cache.Results.Set(key, list, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
        return list;
    }

    public AccessResult AnyDomainUser(DirectoryGraph graph, string graphKey, PlacePermissions place, AccessTarget target)
    {
        var key = $"any|{graphKey}|{place.Key}|{target}";
        if (cache.Results.TryGetValue(key, out AccessResult? result) && result is not null)
        {
            return result;
        }
        result = AccessCalculator.Evaluate(graph, graph.AnyDomainUser, place.Permissions, target);
        cache.Results.Set(key, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
        return result;
    }

    /// <summary>The share and folder rows needed to compute access for a folder, or null if it does not exist.</summary>
    public async Task<FolderContext?> FolderAsync(Guid folderId, CancellationToken ct) =>
        await (from f in db.Folders.AsNoTracking()
               join s in db.Shares on f.ShareId equals s.Id
               join m in db.ManagedServers on s.ServerId equals m.Id
               where f.Id == folderId
               select new FolderContext
               {
                   FolderId = f.Id,
                   Path = f.Path,
                   Removed = f.RemovedAt != null,
                   FolderState = f.SecurityState,
                   FolderSecurityId = f.SecurityId,
                   FolderReadAt = f.SecurityReadAt,
                   ShareId = s.Id,
                   ShareName = s.Name,
                   UncPath = s.UncPath,
                   ShareState = s.SecurityState,
                   ShareSecurityId = s.SecurityId,
                   ServerId = m.Id,
                   Hostname = m.Hostname,
                   LocalGroupsState = m.LocalGroupsState,
               }).SingleOrDefaultAsync(ct);
}

internal sealed class FolderContext
{
    public Guid FolderId { get; init; }
    public required string Path { get; init; }
    public bool Removed { get; init; }
    public PermissionState FolderState { get; init; }
    public long? FolderSecurityId { get; init; }
    public DateTimeOffset? FolderReadAt { get; init; }
    public Guid ShareId { get; init; }
    public required string ShareName { get; init; }
    public required string UncPath { get; init; }
    public PermissionState ShareState { get; init; }
    public long? ShareSecurityId { get; init; }
    public Guid ServerId { get; init; }
    public required string Hostname { get; init; }
    public LocalGroupsState LocalGroupsState { get; init; }
}
