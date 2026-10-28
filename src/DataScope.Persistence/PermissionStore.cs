using System.Data.Common;
using System.Security.Cryptography;
using DataScope.Core.Access;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DataScope.Persistence;

/// <summary>Permissions a scan read for one share or folder: the raw descriptor, or why there is none.</summary>
public sealed record DiscoveredSecurity(PermissionState State, byte[]? Descriptor);

/// <summary>A server's local groups as read by a scan.</summary>
public sealed record DiscoveredLocalGroups(LocalGroupsState State, IReadOnlyList<LocalGroup> Groups);

/// <summary>
/// Stores security descriptors once per distinct value and points shares and folders at them. Uses the caller's
/// DbContext, so writes join the caller's transaction. Keeps a per-instance cache of descriptor ids.
/// </summary>
public sealed class PermissionStore(DataScopeDbContext db, TimeProvider clock)
{
    private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the stored descriptor id for raw bytes, inserting it (and its parsed entries) the first time.
    /// Null when the bytes are not a valid descriptor.
    /// </summary>
    public async Task<long?> StoreDescriptorAsync(byte[] raw, CancellationToken ct)
    {
        if (!SecurityDescriptorCodec.TryParse(raw, out var sd) || sd is null)
        {
            return null;
        }
        var hash = SHA256.HashData(raw);
        var key = Convert.ToHexString(hash);
        if (_ids.TryGetValue(key, out var cached))
        {
            return cached;
        }
        await using (var insert = await CommandAsync("""
            INSERT INTO security_descriptors (hash, raw, owner_sid, control, dacl_present, is_protected, has_explicit_entries, is_canonical, ace_count, first_seen_at)
            VALUES (@hash, @raw, @owner, @control, @present, @protected, @explicit, @canonical, @count, @now)
            ON CONFLICT (hash) DO NOTHING
            RETURNING id
            """, ct))
        {
            insert.Parameters.Add(new NpgsqlParameter<byte[]>("hash", hash));
            insert.Parameters.Add(new NpgsqlParameter<byte[]>("raw", raw));
            insert.Parameters.Add(new NpgsqlParameter("owner", (object?)sd.Owner ?? DBNull.Value));
            insert.Parameters.Add(new NpgsqlParameter<int>("control", sd.Control));
            insert.Parameters.Add(new NpgsqlParameter<bool>("present", sd.Dacl is not null));
            insert.Parameters.Add(new NpgsqlParameter<bool>("protected", sd.IsProtected));
            insert.Parameters.Add(new NpgsqlParameter<bool>("explicit", sd.HasExplicitEntries));
            insert.Parameters.Add(new NpgsqlParameter<bool>("canonical", sd.IsCanonical));
            insert.Parameters.Add(new NpgsqlParameter<int>("count", sd.Dacl?.Count ?? 0));
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", clock.GetUtcNow()));
            if (await insert.ExecuteScalarAsync(ct) is long id)
            {
                await InsertAcesAsync(id, sd, ct);
                _ids[key] = id;
                return id;
            }
        }
        var existing = await db.SecurityDescriptors.Where(d => d.Hash == hash).Select(d => d.Id).SingleAsync(ct);
        _ids[key] = existing;
        return existing;
    }

    private async Task InsertAcesAsync(long id, SecurityDescriptor sd, CancellationToken ct)
    {
        if (sd.Dacl is not { Count: > 0 } dacl)
        {
            return;
        }
        await using var cmd = await CommandAsync("""
            INSERT INTO security_aces (descriptor_id, ordinal, kind, raw_type, flags, mask, sid)
            SELECT @id, u.ordinal, u.kind, u.raw_type, u.flags, u.mask, u.sid
            FROM unnest(@ordinals, @kinds, @types, @flags, @masks, @sids) AS u(ordinal, kind, raw_type, flags, mask, sid)
            """, ct);
        cmd.Parameters.Add(new NpgsqlParameter<long>("id", id));
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("ordinals", Enumerable.Range(0, dacl.Count).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("kinds", dacl.Select(a => a.Kind.ToString()).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("types", dacl.Select(a => (int)a.RawType).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("flags", dacl.Select(a => (int)a.Flags).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<long[]>("masks", dacl.Select(a => (long)a.Mask).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter("sids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar) { Value = dacl.Select(a => a.Sid).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Points a folder at what this scan read. A changed descriptor is logged for drift reporting; a failed read
    /// clears the old descriptor so stale permissions are never shown as current.
    /// </summary>
    public async Task RecordFolderAsync(Guid jobId, Guid folderId, DiscoveredSecurity security, CancellationToken ct)
    {
        var (state, id) = await ResolveAsync(security, ct);
        var now = clock.GetUtcNow();
        var previous = await db.Folders.Where(f => f.Id == folderId).Select(f => new { f.SecurityId, f.SecurityState }).SingleAsync(ct);
        if (previous.SecurityState == PermissionState.Read && state == PermissionState.Read && previous.SecurityId != id)
        {
            db.FolderSecurityChanges.Add(new FolderSecurityChange { FolderId = folderId, JobId = jobId, FromDescriptorId = previous.SecurityId, ToDescriptorId = id, At = now });
            await db.SaveChangesAsync(ct);
        }
        await db.Folders.Where(f => f.Id == folderId).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.SecurityId, id)
            .SetProperty(f => f.SecurityState, state)
            .SetProperty(f => f.SecurityReadAt, now), ct);
    }

    public async Task RecordShareAsync(Guid shareId, DiscoveredSecurity security, CancellationToken ct)
    {
        var (state, id) = await ResolveAsync(security, ct);
        var now = clock.GetUtcNow();
        await db.Shares.Where(s => s.Id == shareId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SecurityId, id)
            .SetProperty(x => x.SecurityState, state)
            .SetProperty(x => x.SecurityReadAt, now), ct);
    }

    /// <summary>Replaces a server's local groups with what this scan read. A failed read keeps nothing stale.</summary>
    public async Task ReplaceLocalGroupsAsync(Guid serverId, Guid jobId, DiscoveredLocalGroups read, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.ServerLocalGroups.Where(g => g.ServerId == serverId).ExecuteDeleteAsync(ct);
        if (read.State == LocalGroupsState.Read)
        {
            foreach (var group in read.Groups.Where(g => Sid.IsValid(g.Sid)).DistinctBy(g => g.Sid, StringComparer.Ordinal))
            {
                var row = new ServerLocalGroup
                {
                    ServerId = serverId,
                    Sid = group.Sid,
                    Name = group.Name.Length <= 256 ? group.Name : group.Name[..256],
                    MembersIncomplete = group.MembersIncomplete,
                    JobId = jobId,
                    ReadAt = now,
                };
                db.ServerLocalGroups.Add(row);
                await db.SaveChangesAsync(ct);
                db.ServerLocalGroupMembers.AddRange(group.Members
                    .Where(m => Sid.IsValid(m.Sid))
                    .DistinctBy(m => m.Sid, StringComparer.Ordinal)
                    .Select(m => new ServerLocalGroupMember { LocalGroupId = row.Id, MemberSid = m.Sid, Kind = m.Kind }));
            }
            await db.SaveChangesAsync(ct);
        }
        await db.ManagedServers.Where(s => s.Id == serverId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LocalGroupsState, read.State)
            .SetProperty(x => x.LocalGroupsReadAt, now), ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
    }

    private async Task<(PermissionState State, long? Id)> ResolveAsync(DiscoveredSecurity security, CancellationToken ct)
    {
        if (security.State != PermissionState.Read || security.Descriptor is null)
        {
            return (security.State == PermissionState.Read ? PermissionState.Unreadable : security.State, null);
        }
        var id = await StoreDescriptorAsync(security.Descriptor, ct);
        return id is null ? (PermissionState.Unreadable, null) : (PermissionState.Read, id);
    }

    private async Task<DbCommand> CommandAsync(string sql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return cmd;
    }
}
