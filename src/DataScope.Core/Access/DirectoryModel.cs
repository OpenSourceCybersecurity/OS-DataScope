namespace DataScope.Core.Access;

/// <summary>One user, group, computer or foreign security principal read from the directory.</summary>
/// <param name="ManagerDn">The AD <c>manager</c> attribute: the distinguished name of the person this user reports to.</param>
public sealed record DirectoryObject(
    string Sid,
    PrincipalKind Kind,
    string Name,
    string DistinguishedName,
    string? DisplayName = null,
    bool Enabled = true,
    bool IsSecurityGroup = true,
    GroupScope? Scope = null,
    string? PrimaryGroupSid = null,
    string? UserPrincipalName = null,
    string? Mail = null,
    string? Department = null,
    string? Title = null,
    DateTimeOffset? LastLogon = null,
    DateTimeOffset? WhenChanged = null,
    IReadOnlyList<string>? SidHistory = null,
    bool MembersIncomplete = false,
    int DirectMemberCount = 0,
    string? ManagerDn = null)
{
    public DirectoryEntry ToEntry() => new(Sid, Kind, Name, Enabled, IsSecurityGroup, PrimaryGroupSid, MembersIncomplete, SidHistory);
}

/// <summary>Everything one directory read returned.</summary>
/// <param name="UnresolvedMembers">Group members that could not be matched to an object, for example in another domain.</param>
/// <param name="Truncated">The object limit was reached; the snapshot is incomplete.</param>
public sealed record DirectoryReadResult(
    string DomainName,
    string DomainSid,
    string NamingContext,
    string? ServerName,
    IReadOnlyList<DirectoryObject> Objects,
    IReadOnlyList<MembershipEdge> Edges,
    IReadOnlyList<string> Warnings,
    int UnresolvedMembers,
    bool Truncated);
