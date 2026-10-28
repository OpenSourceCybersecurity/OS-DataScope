using DataScope.Core.Access;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DataScope.Persistence;

/// <summary>
/// Directory snapshots: a small queue of sync requests (claimed with a lease, like scan jobs), bulk storage of what
/// each sync read, and loading a snapshot back as a <see cref="DirectoryGraph"/>.
/// </summary>
public sealed class DirectoryStore(DataScopeDbContext db, TimeProvider clock)
{
    public const int MaxAttempts = 3;

    /// <summary>Completed snapshots kept; older ones are deleted with their principals and memberships.</summary>
    public const int KeepSnapshots = 10;

    /// <summary>Queues a sync unless one is already waiting or running, and returns the one that will run.</summary>
    public async Task<(DirectorySnapshot Snapshot, bool Created)> QueueAsync(string? requestedBySid, CancellationToken ct)
    {
        var pending = await db.DirectorySnapshots
            .Where(s => s.State == DirectorySyncState.Queued || s.State == DirectorySyncState.Running)
            .OrderBy(s => s.RequestedAt).FirstOrDefaultAsync(ct);
        if (pending is not null)
        {
            return (pending, false);
        }
        var snapshot = new DirectorySnapshot
        {
            Id = Guid.CreateVersion7(),
            State = DirectorySyncState.Queued,
            RequestedAt = clock.GetUtcNow(),
            RequestedBySid = requestedBySid,
        };
        db.DirectorySnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return (snapshot, true);
    }

    /// <summary>Queues the automatic sync when the last one is older than <paramref name="interval"/>.</summary>
    public async Task<bool> EnsureScheduledAsync(TimeSpan interval, CancellationToken ct)
    {
        var busy = await db.DirectorySnapshots.AnyAsync(s => s.State == DirectorySyncState.Queued || s.State == DirectorySyncState.Running, ct);
        if (busy)
        {
            return false;
        }
        var cutoff = clock.GetUtcNow() - interval;
        var recent = await db.DirectorySnapshots.AnyAsync(s => s.RequestedAt > cutoff, ct);
        if (recent)
        {
            return false;
        }
        await QueueAsync(null, ct);
        return true;
    }

    public async Task<DirectorySnapshot?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expires = now + lease;
        var queued = nameof(DirectorySyncState.Queued);
        var running = nameof(DirectorySyncState.Running);
        var claimed = await db.DirectorySnapshots
            .FromSql($"""
                UPDATE directory_snapshots SET
                    state = {running}, lease_owner = {workerId}, lease_expires_at = {expires},
                    attempts = attempts + 1, started_at = {now}
                WHERE id = (
                    SELECT id FROM directory_snapshots
                    WHERE (state = {queued} OR (state = {running} AND lease_expires_at < {now})) AND attempts < {MaxAttempts}
                    ORDER BY requested_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *, xmin
                """)
            .AsNoTracking().ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    public async Task<bool> RenewLeaseAsync(Guid id, string workerId, TimeSpan lease, CancellationToken ct)
    {
        var expires = clock.GetUtcNow() + lease;
        return await db.DirectorySnapshots
            .Where(s => s.Id == id && s.LeaseOwner == workerId && s.State == DirectorySyncState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, expires), ct) == 1;
    }

    /// <summary>Stores a completed read in one transaction, then prunes old snapshots.</summary>
    public async Task<bool> CompleteAsync(Guid id, string workerId, DirectoryReadResult result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owned = await db.DirectorySnapshots.AnyAsync(s => s.Id == id && s.LeaseOwner == workerId && s.State == DirectorySyncState.Running, ct);
        if (!owned)
        {
            return false;
        }
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var copy = await conn.BeginBinaryImportAsync("""
            COPY directory_principals (snapshot_id, sid, kind, name, display_name, distinguished_name, enabled, is_security_group,
                scope, primary_group_sid, user_principal_name, mail, department, title, last_logon, when_changed, sid_history,
                members_incomplete, direct_member_count, manager_sid) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            var sidByDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in result.Objects)
            {
                sidByDn.TryAdd(o.DistinguishedName, o.Sid);
            }
            foreach (var o in result.Objects.DistinctBy(o => o.Sid, StringComparer.Ordinal))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(o.Sid, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(o.Kind.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Cut(o.Name, 256)!, NpgsqlDbType.Varchar, ct);
                await WriteNullableAsync(copy, Cut(o.DisplayName, 256), ct);
                await copy.WriteAsync(Cut(o.DistinguishedName, 2048)!, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(o.Enabled, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(o.IsSecurityGroup, NpgsqlDbType.Boolean, ct);
                await WriteNullableAsync(copy, o.Scope?.ToString(), ct);
                await WriteNullableAsync(copy, o.PrimaryGroupSid, ct);
                await WriteNullableAsync(copy, Cut(o.UserPrincipalName, 1024), ct);
                await WriteNullableAsync(copy, Cut(o.Mail, 256), ct);
                await WriteNullableAsync(copy, Cut(o.Department, 128), ct);
                await WriteNullableAsync(copy, Cut(o.Title, 128), ct);
                await WriteNullableAsync(copy, o.LastLogon, ct);
                await WriteNullableAsync(copy, o.WhenChanged, ct);
                await copy.WriteAsync((o.SidHistory ?? []).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await copy.WriteAsync(o.MembersIncomplete, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(o.DirectMemberCount, NpgsqlDbType.Integer, ct);
                // A manager outside this snapshot (another domain, or filtered out) is dropped rather than guessed.
                await WriteNullableAsync(copy, o.ManagerDn is { } dn && sidByDn.TryGetValue(dn, out var manager) && manager != o.Sid ? manager : null, ct);
            }
            await copy.CompleteAsync(ct);
        }
        var edges = result.Edges.Distinct().ToList();
        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY directory_memberships (snapshot_id, group_sid, member_sid) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var e in edges)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(e.GroupSid, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(e.MemberSid, NpgsqlDbType.Varchar, ct);
            }
            await copy.CompleteAsync(ct);
        }

        var cycles = new DirectoryGraph(result.Objects.Select(o => o.ToEntry()), edges, null, result.DomainSid).Cycles.Count;
        var users = result.Objects.Where(o => o.Kind == PrincipalKind.User).ToList();
        var now = clock.GetUtcNow();
        await db.DirectorySnapshots.Where(s => s.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, DirectorySyncState.Completed)
            .SetProperty(x => x.CompletedAt, now)
            .SetProperty(x => x.LeaseOwner, (string?)null)
            .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(x => x.Domain, Cut(result.DomainName, 253))
            .SetProperty(x => x.DomainSid, result.DomainSid)
            .SetProperty(x => x.NamingContext, Cut(result.NamingContext, 512))
            .SetProperty(x => x.ServerName, Cut(result.ServerName, 253))
            .SetProperty(x => x.Users, users.Count)
            .SetProperty(x => x.DisabledUsers, users.Count(u => !u.Enabled))
            .SetProperty(x => x.Groups, result.Objects.Count(o => o.Kind == PrincipalKind.Group))
            .SetProperty(x => x.Computers, result.Objects.Count(o => o.Kind == PrincipalKind.Computer))
            .SetProperty(x => x.ForeignPrincipals, result.Objects.Count(o => o.Kind == PrincipalKind.ForeignSecurityPrincipal))
            .SetProperty(x => x.Memberships, edges.Count)
            .SetProperty(x => x.UnresolvedMembers, result.UnresolvedMembers)
            .SetProperty(x => x.Cycles, cycles)
            .SetProperty(x => x.Truncated, result.Truncated)
            .SetProperty(x => x.Warnings, result.Warnings.Take(20).Select(w => Cut(w, 500)!).ToArray())
            .SetProperty(x => x.LastError, (string?)null), ct);
        await tx.CommitAsync(ct);
        await PruneAsync(ct);
        return true;
    }

    /// <summary>Records a failed attempt. It is retried until <see cref="MaxAttempts"/>, then marked Failed.</summary>
    public async Task FailAsync(Guid id, string workerId, string error, bool retry, CancellationToken ct)
    {
        var snapshot = await db.DirectorySnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null)
        {
            return;
        }
        var state = retry && snapshot.Attempts < MaxAttempts ? DirectorySyncState.Queued : DirectorySyncState.Failed;
        var now = clock.GetUtcNow();
        await db.DirectorySnapshots.Where(s => s.Id == id && s.LeaseOwner == workerId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, state)
            .SetProperty(x => x.LeaseOwner, (string?)null)
            .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(x => x.CompletedAt, state == DirectorySyncState.Failed ? now : null)
            .SetProperty(x => x.LastError, Cut(error, 2000)), ct);
    }

    public async Task PruneAsync(CancellationToken ct)
    {
        var keep = await db.DirectorySnapshots.Where(s => s.State == DirectorySyncState.Completed)
            .OrderByDescending(s => s.CompletedAt).Take(KeepSnapshots).Select(s => s.Id).ToListAsync(ct);
        if (keep.Count < KeepSnapshots)
        {
            return;
        }
        var oldestKept = await db.DirectorySnapshots.Where(s => keep.Contains(s.Id)).MinAsync(s => s.CompletedAt, ct);
        await db.DirectorySnapshots
            .Where(s => (s.State == DirectorySyncState.Completed || s.State == DirectorySyncState.Failed) && !keep.Contains(s.Id) && s.RequestedAt < oldestKept)
            .ExecuteDeleteAsync(ct);
    }

    public Task<DirectorySnapshot?> LatestCompletedAsync(CancellationToken ct) =>
        db.DirectorySnapshots.AsNoTracking().Where(s => s.State == DirectorySyncState.Completed)
            .OrderByDescending(s => s.CompletedAt).FirstOrDefaultAsync(ct);

    /// <summary>Loads a snapshot and a server's local groups as an access graph.</summary>
    public async Task<DirectoryGraph> LoadGraphAsync(DirectorySnapshot? snapshot, Guid? serverId, CancellationToken ct)
    {
        List<DirectoryEntry> principals = [];
        List<MembershipEdge> edges = [];
        if (snapshot is not null)
        {
            var rows = await db.DirectoryPrincipals.AsNoTracking().Where(p => p.SnapshotId == snapshot.Id)
                .Select(p => new { p.Sid, p.Kind, p.Name, p.Enabled, p.IsSecurityGroup, p.PrimaryGroupSid, p.MembersIncomplete, p.SidHistory, p.ManagerSid })
                .ToListAsync(ct);
            principals = rows.Select(p => new DirectoryEntry(p.Sid, p.Kind, p.Name, p.Enabled, p.IsSecurityGroup, p.PrimaryGroupSid, p.MembersIncomplete, p.SidHistory, p.ManagerSid)).ToList();
            edges = await db.DirectoryMemberships.AsNoTracking().Where(m => m.SnapshotId == snapshot.Id)
                .Select(m => new MembershipEdge(m.GroupSid, m.MemberSid)).ToListAsync(ct);
        }
        return new DirectoryGraph(principals, edges, await LoadLocalGroupsAsync(serverId, ct), snapshot?.DomainSid);
    }

    public async Task<LocalGroupSet> LoadLocalGroupsAsync(Guid? serverId, CancellationToken ct)
    {
        if (serverId is null)
        {
            return LocalGroupSet.Unknown;
        }
        var state = await db.ManagedServers.Where(s => s.Id == serverId).Select(s => s.LocalGroupsState).SingleOrDefaultAsync(ct);
        if (state != LocalGroupsState.Read)
        {
            return new LocalGroupSet(state, []);
        }
        var groups = await db.ServerLocalGroups.AsNoTracking().Where(g => g.ServerId == serverId).ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var members = await db.ServerLocalGroupMembers.AsNoTracking().Where(m => ids.Contains(m.LocalGroupId)).ToListAsync(ct);
        var byGroup = members.ToLookup(m => m.LocalGroupId);
        return new LocalGroupSet(LocalGroupsState.Read, groups
            .Select(g => new LocalGroup(g.Sid, g.Name, byGroup[g.Id].Select(m => new LocalMember(m.MemberSid, m.Kind)).ToList(), g.MembersIncomplete))
            .ToList());
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, CancellationToken ct)
    {
        if (value is null)
        {
            await copy.WriteNullAsync(ct);
        }
        else
        {
            await copy.WriteAsync(value, ct);
        }
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
