using DataScope.Core.Access;

namespace DataScope.Scanner.Security;

/// <summary>What reading one object's permissions returned: the raw self-relative descriptor, or why there is none.</summary>
/// <param name="Note">Fixed text for people; never a path or an exception message.</param>
public sealed record SecurityRead(PermissionState State, byte[]? Descriptor, string? Note = null)
{
    public static SecurityRead Denied(string note) => new(PermissionState.AccessDenied, null, note);

    public static SecurityRead Failed(string note) => new(PermissionState.Unreadable, null, note);
}

public sealed record LocalGroupsRead(LocalGroupsState State, IReadOnlyList<LocalGroup> Groups, string? Note = null);

/// <summary>
/// Reads permissions (owner and DACL, never the audit SACL) and a file server's local groups.
/// Implementations only read: nothing here can change permissions, ownership or group membership.
/// </summary>
public interface ISecurityReader
{
    /// <summary>Share permissions from Advanced Sharing. Windows only returns these to administrators of the file server.</summary>
    SecurityRead ReadShare(string hostname, string shareName);

    /// <summary>NTFS permissions of a folder, given the same full path the scanner lists.</summary>
    SecurityRead ReadFolder(string fullPath);

    LocalGroupsRead ReadLocalGroups(string hostname);
}
