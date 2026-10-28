namespace DataScope.Core.Access;

/// <summary>Whether Data Scope has a resource's permissions.</summary>
public enum PermissionState
{
    NotRead,
    Read,

    /// <summary>The scan account was not allowed to read them.</summary>
    AccessDenied,

    /// <summary>Read, but the data was damaged or could not be interpreted.</summary>
    Unreadable,
}

/// <summary>Which objects an evaluation is about.</summary>
public enum AccessTarget
{
    /// <summary>A share's own permissions: every entry applies.</summary>
    Share,

    /// <summary>The folder itself (listing it, changing its permissions): entries that are not inherit-only.</summary>
    Folder,

    /// <summary>Files directly in the folder that inherit its permissions: entries marked to pass to files.</summary>
    Files,
}

public enum Certainty
{
    /// <summary>Computed from complete data, the way Windows evaluates it. Not a live Windows access check.</summary>
    Likely,

    /// <summary>Some input was missing or could not be evaluated; the real answer is between the two bounds.</summary>
    Partial,

    /// <summary>The permissions themselves could not be read.</summary>
    Unknown,
}

public enum AccessNoteCode
{
    ShareNotRead,
    FolderNotRead,
    ShareLimits,
    FolderLimits,
    DenyApplied,
    AccountDisabled,
    UnresolvedMembership,
    LocalGroupsUnknown,
    ConditionalEntry,
    NonCanonicalOrder,
    NoDacl,
    CreatorOwner,
    OwnerCanChangePermissions,
    FilesMayDiffer,
    UnknownUser,
}

public sealed record AccessNote(AccessNoteCode Code, string Text);

/// <summary>One entry that changed the outcome for a user.</summary>
/// <param name="Index">Position in the ACL (0-based, stored order).</param>
/// <param name="Chain">How the user reaches the entry's trustee, user first. Empty for a "Maybe" match.</param>
/// <param name="Bits">Rights this entry granted (allow) or removed (deny) for the user.</param>
public sealed record AccessReason(AccessTarget Source, int Index, Ace Ace, Membership Match, IReadOnlyList<string> Chain, uint Bits)
{
    public Rights Rights => AccessMask.ToRights(Bits);
}

/// <summary>
/// One user's access to a resource as a range: <see cref="Rights"/> is what Data Scope can show they have,
/// <see cref="UpTo"/> what they might have. They are equal when <see cref="Certainty"/> is Likely.
/// </summary>
public sealed record AccessResult(
    string UserSid,
    Rights Rights,
    Rights UpTo,
    uint Mask,
    uint UpperMask,
    Certainty Certainty,
    Rights? ShareRights,
    Rights? FolderRights,
    IReadOnlyList<AccessReason> Reasons,
    IReadOnlyList<AccessNote> Notes)
{
    /// <summary>Data Scope can show the account reads file contents. A write-only right, as on a drop box folder, is not reading.</summary>
    public bool CanRead => (Mask & AccessMask.ReadData) != 0;

    /// <summary>The account can or might read file contents.</summary>
    public bool MightRead => (UpperMask & AccessMask.ReadData) != 0;
}

/// <summary>Share and NTFS permissions for one place on a share, as captured by a scan.</summary>
public sealed record ResourcePermissions(PermissionState ShareState, SecurityDescriptor? Share, PermissionState FolderState, SecurityDescriptor? Folder);

/// <summary>
/// Effective access over SMB: the share permission and the NTFS permission are each evaluated the way Windows does
/// (entries in stored order, a right decided by the first entry that mentions it), and the user gets only what both
/// allow. Missing data widens the range instead of guessing. Never a substitute for a Windows access check.
/// </summary>
public static class AccessCalculator
{
    /// <summary>Result of evaluating one ACL for one token.</summary>
    public sealed record AclEvaluation(uint Lower, uint Upper, IReadOnlyList<AccessReason> Reasons, bool UsedConditional, bool UsedMaybe, bool CreatorOwner);

    public static AclEvaluation EvaluateAcl(SecurityDescriptor sd, AccessTarget target, Func<string, Membership> contains, Func<string, IReadOnlyList<string>> chain)
    {
        if (sd.Dacl is null)
        {
            return new AclEvaluation(AccessMask.FileAllAccess, AccessMask.FileAllAccess, [], false, false, false);
        }
        uint lowerGranted = 0, lowerDenied = 0, upperGranted = 0, upperDenied = 0;
        bool usedConditional = false, usedMaybe = false, creatorOwner = false;
        var reasons = new List<AccessReason>();
        for (var i = 0; i < sd.Dacl.Count; i++)
        {
            var ace = sd.Dacl[i];
            if (ace.Kind == AceKind.Other || ace.Sid is null || !Applies(ace, target))
            {
                continue;
            }
            if (WellKnownSid.IsCreatorPlaceholder(ace.Sid))
            {
                // Inherited by each new file as an entry for whoever created it. Varies per file; reported, not evaluated.
                creatorOwner |= target == AccessTarget.Files && ace.Sid == WellKnownSid.CreatorOwner;
                continue;
            }
            var match = contains(ace.Sid);
            if (match == Membership.No)
            {
                continue;
            }
            if (ace.IsConditional)
            {
                usedConditional = true;
                match = Membership.Maybe;
            }
            usedMaybe |= match == Membership.Maybe;
            var mask = AccessMask.Normalize(ace.Mask);
            uint bits;
            if (ace.IsAllow)
            {
                if (match == Membership.Yes)
                {
                    lowerGranted |= mask & ~lowerDenied;
                }
                bits = mask & ~upperDenied & ~upperGranted;
                upperGranted |= mask & ~upperDenied;
            }
            else
            {
                bits = mask & ~lowerGranted & ~lowerDenied;
                lowerDenied |= mask & ~lowerGranted;
                if (match == Membership.Yes)
                {
                    upperDenied |= mask & ~upperGranted;
                }
            }
            if (bits != 0)
            {
                reasons.Add(new AccessReason(target, i, ace, match, match == Membership.Yes ? chain(ace.Sid) : [], bits));
            }
        }
        return new AclEvaluation(lowerGranted, upperGranted, reasons, usedConditional, usedMaybe, creatorOwner);
    }

    /// <summary>
    /// Every allow entry in an ACL that gives <paramref name="token"/>'s account read access to the target, including
    /// ones another entry already covers: the entries that would each still let it read if the others were removed.
    /// <see cref="Membership.Maybe"/> when membership is uncertain or the entry is conditional. Says nothing about
    /// deny entries or the share permission; use <see cref="Evaluate(DirectoryGraph, UserToken, ResourcePermissions, AccessTarget)"/> for the outcome.
    /// </summary>
    public static IReadOnlyList<(Ace Ace, Membership Match)> ReadGrants(SecurityDescriptor sd, AccessTarget target, UserToken token)
    {
        var grants = new List<(Ace, Membership)>();
        foreach (var ace in sd.Dacl ?? [])
        {
            if (!ace.IsAllow || ace.Sid is null || !Applies(ace, target) || WellKnownSid.IsCreatorPlaceholder(ace.Sid)
                || (AccessMask.Normalize(ace.Mask) & AccessMask.ReadData) == 0)
            {
                continue;
            }
            var match = token.Contains(ace.Sid);
            if (match != Membership.No)
            {
                grants.Add((ace, ace.IsConditional ? Membership.Maybe : match));
            }
        }
        return grants;
    }

    private static bool Applies(Ace ace, AccessTarget target) => target switch
    {
        AccessTarget.Share => true,
        AccessTarget.Folder => ace.AppliesToObject,
        _ => ace.AppliesToChildFiles,
    };

    /// <summary>
    /// A user's access over the network to a folder (<see cref="AccessTarget.Folder"/>) or to the files in it that
    /// inherit its permissions (<see cref="AccessTarget.Files"/>).
    /// </summary>
    public static AccessResult Evaluate(DirectoryGraph graph, string userSid, ResourcePermissions perms, AccessTarget target) =>
        Evaluate(graph, graph.TokenFor(userSid), perms, target);

    /// <summary>Evaluates a prepared token, such as <see cref="DirectoryGraph.AnyDomainUser"/>.</summary>
    public static AccessResult Evaluate(DirectoryGraph graph, UserToken token, ResourcePermissions perms, AccessTarget target)
    {
        if (target == AccessTarget.Share)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Evaluate a folder or its files; share permissions are applied automatically.");
        }
        var userSid = token.UserSid;
        var notes = new List<AccessNote>();
        var reasons = new List<AccessReason>();
        var user = graph.Find(userSid);
        if (user is null && userSid != graph.AnyDomainUserSid)
        {
            notes.Add(new(AccessNoteCode.UnknownUser, "This account is not in the latest directory sync, so only its built-in groups were considered."));
        }
        else if (user is { Enabled: false })
        {
            notes.Add(new(AccessNoteCode.AccountDisabled, "The account is disabled. It cannot sign in now, but this access returns if it is re-enabled."));
        }

        if (perms.FolderState != PermissionState.Read || perms.Folder is null)
        {
            notes.Add(new(AccessNoteCode.FolderNotRead, "Data Scope could not read this folder's NTFS permissions, so it cannot say who has access."));
            return new AccessResult(userSid, Rights.None, Rights.FullControl, 0, AccessMask.FileAllAccess, Certainty.Unknown, null, null, [], notes);
        }

        var folder = EvaluateAcl(perms.Folder, target, token.Contains, token.ChainTo);
        uint folderLower = folder.Lower, folderUpper = folder.Upper;
        reasons.AddRange(folder.Reasons);

        // The owner can always read and change the permissions, unless an OWNER RIGHTS entry says otherwise.
        if (target == AccessTarget.Folder && perms.Folder.Owner is { } owner && perms.Folder.Dacl is not null
            && !perms.Folder.Dacl.Any(a => a.Sid == WellKnownSid.OwnerRights))
        {
            var isOwner = token.Contains(owner);
            const uint ownerBits = AccessMask.ReadControl | AccessMask.WriteDac;
            if (isOwner != Membership.No)
            {
                folderUpper |= ownerBits;
                if (isOwner == Membership.Yes)
                {
                    folderLower |= ownerBits;
                }
                notes.Add(new(AccessNoteCode.OwnerCanChangePermissions, $"{(isOwner == Membership.Yes ? "This account" : "This account may")} own{(isOwner == Membership.Yes ? "s" : "")} the folder (through {graph.NameOf(owner)}), and an owner can always change its permissions."));
            }
        }

        uint lower = folderLower, upper = folderUpper;
        Rights? shareRights = null;
        if (perms.ShareState == PermissionState.Read && perms.Share is not null)
        {
            var share = EvaluateAcl(perms.Share, AccessTarget.Share, token.Contains, token.ChainTo);
            lower &= share.Lower;
            upper &= share.Upper;
            shareRights = AccessMask.ToRights(share.Upper);
            reasons.InsertRange(0, share.Reasons);
            if (share.UsedConditional)
            {
                notes.Add(new(AccessNoteCode.ConditionalEntry, "A share permission entry has a condition Data Scope cannot evaluate."));
            }
            if (AccessMask.ToRights(share.Upper) < AccessMask.ToRights(folder.Upper) && share.Upper != 0)
            {
                notes.Add(new(AccessNoteCode.ShareLimits, $"NTFS allows {AccessMask.Label(AccessMask.ToRights(folder.Upper))}, but the share permission limits this to {AccessMask.Label(AccessMask.ToRights(upper))}."));
            }
            else if (AccessMask.ToRights(folder.Upper) < AccessMask.ToRights(share.Upper) && folder.Upper != 0)
            {
                notes.Add(new(AccessNoteCode.FolderLimits, $"The share allows {AccessMask.Label(AccessMask.ToRights(share.Upper))}, but NTFS limits this to {AccessMask.Label(AccessMask.ToRights(upper))}."));
            }
        }
        else
        {
            // Without the share permission, NTFS is only an upper bound: the share could allow less, or nothing.
            lower = 0;
            notes.Add(new(AccessNoteCode.ShareNotRead, upper == 0
                ? "The share permissions could not be read, but NTFS gives this account no access here anyway."
                : $"The share permissions could not be read, so this is an upper limit: NTFS allows {AccessMask.Label(AccessMask.ToRights(upper))}, and the share may allow less."));
        }

        if (folder.UsedConditional)
        {
            notes.Add(new(AccessNoteCode.ConditionalEntry, "An NTFS entry has a condition (Dynamic Access Control) that Data Scope cannot evaluate."));
        }
        if (perms.Folder.Dacl is null)
        {
            notes.Add(new(AccessNoteCode.NoDacl, "This folder has no NTFS permission list at all, which Windows treats as full access for everyone."));
        }
        else if (!perms.Folder.IsCanonical)
        {
            notes.Add(new(AccessNoteCode.NonCanonicalOrder, "The permission entries are in an unusual order. Windows applies them in that order, and so does Data Scope."));
        }
        if (reasons.Any(r => r.Match == Membership.Maybe && !r.Ace.IsConditional))
        {
            notes.Add(graph.LocalGroups.State == LocalGroupsState.Read
                ? new(AccessNoteCode.UnresolvedMembership, "Some group membership on the way to this access could not be resolved, so the account may have more or less access than shown.")
                : new(AccessNoteCode.LocalGroupsUnknown, "Access depends on groups local to the file server, which Data Scope could not read. The account may have more or less access than shown."));
        }
        if (reasons.Any(r => r.Ace.IsDeny && r.Match == Membership.Yes))
        {
            notes.Add(new(AccessNoteCode.DenyApplied, "A deny entry removes some rights for this account."));
        }
        if (target == AccessTarget.Files)
        {
            if (folder.CreatorOwner)
            {
                notes.Add(new(AccessNoteCode.CreatorOwner, "Whoever creates a file here also gets rights on that file (CREATOR OWNER)."));
            }
            notes.Add(new(AccessNoteCode.FilesMayDiffer, "This is for files that inherit the folder's permissions. A file with its own permissions can differ."));
        }

        // Entries Data Scope could not match for certain only matter if they changed a right; then the bounds differ.
        var certainty = lower == upper ? Certainty.Likely : Certainty.Partial;
        var rights = AccessMask.ToRights(lower);
        var upTo = AccessMask.ToRights(upper);
        return new AccessResult(userSid, rights, upTo, lower, upper, certainty, shareRights, AccessMask.ToRights(folderUpper), reasons, notes);
    }

    /// <summary>Every user who has, or might have, any access, most access first.</summary>
    public static IReadOnlyList<AccessResult> WhoHasAccess(DirectoryGraph graph, ResourcePermissions perms, AccessTarget target) =>
        graph.Users
            .Select(u => Evaluate(graph, u.Sid, perms, target))
            .Where(r => r.UpTo > Rights.None)
            .OrderByDescending(r => r.Rights)
            .ThenByDescending(r => r.UpTo)
            .ThenBy(r => graph.NameOf(r.UserSid), StringComparer.OrdinalIgnoreCase)
            .ToList();
}
