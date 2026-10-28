using DataScope.Core.Access;

namespace DataScope.Persistence.Entities;

public enum DirectorySyncState
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// One read of Active Directory. Completed snapshots are never changed, so access explanations and (later) review
/// campaigns stay reproducible after the directory changes. Old snapshots are pruned.
/// </summary>
public sealed class DirectorySnapshot
{
    public Guid Id { get; set; }
    public DirectorySyncState State { get; set; }
    public string? Domain { get; set; }
    public string? DomainSid { get; set; }
    public string? NamingContext { get; set; }
    public string? ServerName { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Who asked for it; null for the automatic daily sync.</summary>
    public string? RequestedBySid { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }

    public int Users { get; set; }
    public int DisabledUsers { get; set; }
    public int Groups { get; set; }
    public int Computers { get; set; }
    public int ForeignPrincipals { get; set; }
    public int Memberships { get; set; }
    public int UnresolvedMembers { get; set; }
    public int Cycles { get; set; }
    public bool Truncated { get; set; }
    public string[] Warnings { get; set; } = [];
    public string? LastError { get; set; }
    public uint RowVersion { get; set; }
}

public sealed class DirectoryPrincipal
{
    public long Id { get; set; }
    public Guid SnapshotId { get; set; }
    public required string Sid { get; set; }
    public PrincipalKind Kind { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public required string DistinguishedName { get; set; }
    public bool Enabled { get; set; }
    public bool IsSecurityGroup { get; set; }
    public GroupScope? Scope { get; set; }
    public string? PrimaryGroupSid { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? Mail { get; set; }
    public string? Department { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? LastLogon { get; set; }
    public DateTimeOffset? WhenChanged { get; set; }
    public string[] SidHistory { get; set; } = [];
    public bool MembersIncomplete { get; set; }
    public int DirectMemberCount { get; set; }

    /// <summary>Who this user reports to (AD <c>manager</c>), when that person is in the same snapshot.</summary>
    public string? ManagerSid { get; set; }
}

/// <summary>A direct membership in one snapshot. Nesting is resolved when access is computed.</summary>
public sealed class DirectoryMembership
{
    public Guid SnapshotId { get; set; }
    public required string GroupSid { get; set; }
    public required string MemberSid { get; set; }
}

/// <summary>
/// A distinct security descriptor, stored once however many folders share it (most folders inherit identical
/// permissions). The raw bytes are kept as evidence; the entries are also stored parsed for querying.
/// </summary>
public sealed class StoredSecurityDescriptor
{
    public long Id { get; set; }

    /// <summary>SHA-256 of <see cref="Raw"/>.</summary>
    public required byte[] Hash { get; set; }

    public required byte[] Raw { get; set; }
    public string? OwnerSid { get; set; }
    public int Control { get; set; }
    public bool DaclPresent { get; set; }
    public bool IsProtected { get; set; }
    public bool HasExplicitEntries { get; set; }
    public bool IsCanonical { get; set; }
    public int AceCount { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
}

public sealed class StoredAce
{
    public long DescriptorId { get; set; }
    public int Ordinal { get; set; }
    public AceKind Kind { get; set; }
    public int RawType { get; set; }
    public int Flags { get; set; }

    /// <summary>Access mask as stored by Windows (a 32-bit unsigned value).</summary>
    public long Mask { get; set; }

    public string? Sid { get; set; }
}

/// <summary>A folder whose permissions differed from the previous scan: the start of drift reporting.</summary>
public sealed class FolderSecurityChange
{
    public long Id { get; set; }
    public Guid FolderId { get; set; }
    public Guid JobId { get; set; }
    public long? FromDescriptorId { get; set; }
    public long? ToDescriptorId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A group local to one file server, as read by the last scan of that server.</summary>
public sealed class ServerLocalGroup
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public required string Sid { get; set; }
    public required string Name { get; set; }
    public bool MembersIncomplete { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset ReadAt { get; set; }
}

public sealed class ServerLocalGroupMember
{
    public long LocalGroupId { get; set; }
    public required string MemberSid { get; set; }
    public LocalMemberKind Kind { get; set; }
}
