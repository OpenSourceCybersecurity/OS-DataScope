using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataScope.Core.Access;

namespace DataScope.Core.Governance;

/// <summary>Directory details shown to reviewers next to a person's name.</summary>
public sealed record PersonInfo(string? Department, string? Title);

/// <summary>One person reached by a permission entry, as it stood when the campaign was created.</summary>
/// <param name="Certain">False when membership could not be resolved (an unreadable group or local group).</param>
/// <param name="Chain">How the person reaches the entry's trustee, by name, person first.</param>
/// <param name="Rights">What the person can do with the folder's files (share and NTFS together), lower bound.</param>
/// <param name="AlsoVia">Other entries that would still give the person access if this one were removed.</param>
public sealed record FrozenUser(
    int Ordinal, string Sid, string Name, bool Enabled, string? Department, string? Title, bool Certain,
    IReadOnlyList<string> Chain, string Rights, string UpTo, string Certainty, string? AlsoVia);

/// <summary>One trustee in a folder's NTFS permissions, frozen for review.</summary>
/// <param name="Effect">Allow or Deny.</param>
/// <param name="RequiresDecision">Allow entries for anyone but built-in administrative identities need a decision.</param>
/// <param name="Flags">Plain markers for reviewers: Broad, DirectUser, DeletedAccount, OtherDomain, DisabledAccounts, UnresolvedMembers, CanChangePermissions, Conditional, NoMembers, NoDecisionNeeded.</param>
public sealed record FrozenItem(
    int Ordinal, string TrusteeSid, string TrusteeName, string TrusteeKind, string Effect, string Rights, string AppliesTo,
    bool Inherited, bool Broad, bool Administrative, bool RequiresDecision,
    int UsersCertain, int UsersPossible, int UsersDisabled, int UsersWithOtherAccess, bool UsersTruncated,
    IReadOnlyList<string> Flags, IReadOnlyList<FrozenUser> Users);

/// <summary>Everything about a review that is frozen when the campaign starts. Its hash is the review's evidence seal.</summary>
public sealed record FrozenReview(
    Guid CampaignId, Guid ReviewId, Guid FolderId, string Resource, string OwnerSid, string OwnerName, string? BackupOwnerSid,
    Guid? DirectorySnapshotId, DateTimeOffset? DirectorySyncedAt, string ShareState, string FolderState,
    long? ShareSecurityId, long? FolderSecurityId, DateTimeOffset FrozenAt, IReadOnlyList<string> Notes,
    IReadOnlyList<FrozenItem> Items)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>SHA-256 over a fixed serialization of every frozen field, as lower-case hex.</summary>
    public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)))).ToLowerInvariant();
}

/// <summary>
/// Turns a folder's permissions and a directory snapshot into review items: one per trustee in the NTFS permission
/// list, with the people it reaches and whether they would keep access through another entry. Pure and deterministic.
/// </summary>
public static class ReviewSnapshotBuilder
{
    public const int DefaultMaxUsersPerItem = 2000;

    /// <summary>Flag on entries for accounts an administrator listed as needing no decision. Owners are not shown them.</summary>
    public const string NoDecisionNeeded = "NoDecisionNeeded";

    /// <param name="noDecision">SIDs of users and groups whose entries need no decision (the administrators' list).</param>
    public static IReadOnlyList<FrozenItem> BuildItems(
        DirectoryGraph graph, ResourcePermissions perms, Func<string, PersonInfo?> people, int maxUsersPerItem = DefaultMaxUsersPerItem,
        IReadOnlySet<string>? noDecision = null)
    {
        if (perms.FolderState != PermissionState.Read || perms.Folder?.Dacl is not { } dacl)
        {
            return [];
        }
        var effective = AccessCalculator.WhoHasAccess(graph, perms, AccessTarget.Files).ToDictionary(r => r.UserSid, StringComparer.Ordinal);
        var drafts = dacl
            .Where(a => a.Sid is not null && a.Kind != AceKind.Other)
            .GroupBy(a => (Sid: a.Sid!, a.IsDeny))
            .Select(g => Draft(graph, g.Key.Sid, g.Key.IsDeny, g.ToList(), effective, people, maxUsersPerItem, noDecision?.Contains(g.Key.Sid) == true))
            .OrderByDescending(d => d.RequiresDecision)
            .ThenBy(d => d.Effect == "Deny")
            .ThenByDescending(d => d.Broad)
            .ThenByDescending(d => d.RightsRank)
            .ThenBy(d => d.TrusteeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.TrusteeSid, StringComparer.Ordinal)
            .ToList();
        return drafts.Select((d, i) => d.ToItem(i)).ToList();
    }

    /// <summary>Plain statements about what limits the certainty of a whole review.</summary>
    public static IReadOnlyList<string> Notes(DirectoryGraph graph, ResourcePermissions perms, bool directorySynced)
    {
        var notes = new List<string>();
        if (!directorySynced)
        {
            notes.Add("Active Directory had not been synced, so the members of groups are not known.");
        }
        if (perms.ShareState != PermissionState.Read)
        {
            notes.Add("The share permissions could not be read, so some people may have less access than shown.");
        }
        if (graph.LocalGroups.State != LocalGroupsState.Read)
        {
            notes.Add("The file server's local groups could not be read, so their members are shown as possible, not certain.");
        }
        if (perms.Folder is { IsCanonical: false })
        {
            notes.Add("The permission entries are in an unusual order, which Windows applies as stored. Ask IT to check them.");
        }
        notes.Add("Access is computed from the permissions and group memberships at the time of the campaign. It is not a record of anyone opening a file.");
        return notes;
    }

    private sealed record DraftItem(
        string TrusteeSid, string TrusteeName, string TrusteeKind, string Effect, Rights RightsValue, string AppliesTo, bool Inherited,
        bool Broad, bool Administrative, bool RequiresDecision, int Certain, int Possible, int Disabled, int WithOther, bool Truncated,
        IReadOnlyList<string> Flags, IReadOnlyList<FrozenUser> Users)
    {
        public int RightsRank => (int)RightsValue;

        public FrozenItem ToItem(int ordinal) => new(
            ordinal, TrusteeSid, TrusteeName, TrusteeKind, Effect, AccessMask.Label(RightsValue), AppliesTo, Inherited, Broad,
            Administrative, RequiresDecision, Certain, Possible, Disabled, WithOther, Truncated, Flags, Users);
    }

    private static DraftItem Draft(
        DirectoryGraph graph, string sid, bool deny, List<Ace> aces, Dictionary<string, AccessResult> effective,
        Func<string, PersonInfo?> people, int maxUsers, bool listed)
    {
        var trustee = TrusteeInfo.Describe(graph, sid);
        var rights = aces.Max(a => AccessMask.ToRights(a.Mask));
        var appliesTo = string.Join("; ", aces.Select(a => a.AppliesTo).Distinct(StringComparer.Ordinal));
        var inherited = aces.All(a => a.IsInherited);
        var placeholder = WellKnownSid.IsCreatorPlaceholder(sid) || sid == WellKnownSid.OwnerRights;
        var requiresDecision = !deny && rights > Rights.None && !trustee.Administrative && !placeholder && !listed;

        var users = new List<(FrozenUser User, bool Disabled, bool Other)>();
        int certain = 0, possible = 0;
        if (!deny && !placeholder)
        {
            foreach (var user in graph.Users)
            {
                var token = graph.TokenFor(user.Sid);
                var membership = token.Contains(sid);
                if (membership == Membership.No)
                {
                    continue;
                }
                if (membership == Membership.Yes)
                {
                    certain++;
                }
                else
                {
                    possible++;
                }
                effective.TryGetValue(user.Sid, out var result);
                var also = result?.Reasons
                    .Where(r => r.Source != AccessTarget.Share && r.Ace.IsAllow && r.Ace.Sid is { } other && other != sid)
                    .Select(r => r.Ace.Sid == user.Sid ? "a direct entry" : graph.NameOf(r.Ace.Sid!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
                var info = people(user.Sid);
                users.Add((new FrozenUser(
                    0, user.Sid, user.Name, user.Enabled, info?.Department, info?.Title, membership == Membership.Yes,
                    token.ChainTo(sid).Select(graph.NameOf).ToList(),
                    AccessMask.Label(result?.Rights ?? Rights.None), AccessMask.Label(result?.UpTo ?? Rights.None),
                    (result?.Certainty ?? Certainty.Likely).ToString(),
                    also is { Count: > 0 } ? string.Join(", ", also) : null), !user.Enabled, also is { Count: > 0 }));
            }
        }
        var ordered = users
            .OrderByDescending(u => u.User.Certain)
            .ThenBy(u => u.User.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.User.Sid, StringComparer.Ordinal)
            .ToList();
        var kept = ordered.Take(maxUsers).Select((u, i) => u.User with { Ordinal = i }).ToList();

        var flags = new List<string>();
        void Flag(bool condition, string flag)
        {
            if (condition)
            {
                flags.Add(flag);
            }
        }
        var disabled = users.Count(u => u.Disabled);
        Flag(trustee.Broad && !deny, "Broad");
        Flag(trustee.Kind == TrusteeKind.User, "DirectUser");
        Flag(trustee.Kind == TrusteeKind.Deleted, "DeletedAccount");
        Flag(trustee.Kind is TrusteeKind.Unknown or TrusteeKind.ForeignSecurityPrincipal, "OtherDomain");
        Flag(disabled > 0, "DisabledAccounts");
        Flag(possible > 0, "UnresolvedMembers");
        Flag(!deny && rights == Rights.FullControl && !trustee.Administrative && !placeholder, "CanChangePermissions");
        Flag(aces.Any(a => a.IsConditional), "Conditional");
        Flag(requiresDecision && certain + possible == 0, "NoMembers");
        Flag(listed, NoDecisionNeeded);

        return new DraftItem(
            sid, trustee.Name, trustee.Kind.ToString(), deny ? "Deny" : "Allow", rights, appliesTo, inherited, trustee.Broad,
            trustee.Administrative, requiresDecision, certain, possible, disabled, users.Count(u => u.Other),
            ordered.Count > maxUsers, flags, kept);
    }
}
