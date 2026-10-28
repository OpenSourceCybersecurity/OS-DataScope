using DataScope.Core.Access;

namespace DataScope.Core.Tests.Access;

/// <summary>
/// A small synthetic domain, matching the cases in docs/05-ACCEPTANCE-TESTS.md: nested groups, a disabled user,
/// a nesting loop, a distribution group, an unresolvable foreign member and SID history.
/// </summary>
internal static class Lab
{
    public const string Domain = "S-1-5-21-1000-2000-3000";
    public const string DomainUsers = Domain + "-513";
    public const string DomainAdmins = Domain + "-512";
    public const string Alice = Domain + "-1101";   // HR
    public const string Bob = Domain + "-1102";     // Finance
    public const string Carol = Domain + "-1103";   // disabled, HR
    public const string Dave = Domain + "-1104";    // Finance reviewer (nested)
    public const string Eve = Domain + "-1105";     // nobody special
    public const string Frank = Domain + "-1106";   // in the nesting loop
    public const string Grace = Domain + "-1107";   // migrated, has SID history
    public const string Admin = Domain + "-1108";   // Domain Admins
    public const string HrRead = Domain + "-2001";
    public const string HrWrite = Domain + "-2002";
    public const string FinanceRead = Domain + "-2003";
    public const string FinanceReviewers = Domain + "-2004";
    public const string LoopA = Domain + "-2005";
    public const string LoopB = Domain + "-2006";
    public const string Newsletter = Domain + "-2007";
    public const string Partners = Domain + "-2008";
    public const string Foreign = "S-1-5-21-9000-9000-9000-4001";
    public const string GraceOld = "S-1-5-21-7000-7000-7000-1500";

    public static readonly DirectoryEntry[] Principals =
    [
        new(Alice, PrincipalKind.User, "alice", PrimaryGroupSid: DomainUsers),
        new(Bob, PrincipalKind.User, "bob", PrimaryGroupSid: DomainUsers),
        new(Carol, PrincipalKind.User, "carol", Enabled: false, PrimaryGroupSid: DomainUsers),
        new(Dave, PrincipalKind.User, "dave", PrimaryGroupSid: DomainUsers),
        new(Eve, PrincipalKind.User, "eve", PrimaryGroupSid: DomainUsers),
        new(Frank, PrincipalKind.User, "frank", PrimaryGroupSid: DomainUsers),
        new(Grace, PrincipalKind.User, "grace", PrimaryGroupSid: DomainUsers, SidHistory: [GraceOld]),
        new(Admin, PrincipalKind.User, "admin", PrimaryGroupSid: DomainUsers),
        new(DomainUsers, PrincipalKind.Group, "Domain Users"),
        new(DomainAdmins, PrincipalKind.Group, "Domain Admins"),
        new(HrRead, PrincipalKind.Group, "HR_Read"),
        new(HrWrite, PrincipalKind.Group, "HR_Write"),
        new(FinanceRead, PrincipalKind.Group, "Finance_Read"),
        new(FinanceReviewers, PrincipalKind.Group, "Finance_Reviewers"),
        new(LoopA, PrincipalKind.Group, "Loop_A"),
        new(LoopB, PrincipalKind.Group, "Loop_B"),
        new(Newsletter, PrincipalKind.Group, "Newsletter", IsSecurityGroup: false),
        new(Partners, PrincipalKind.Group, "Partners"),
        new(Foreign, PrincipalKind.ForeignSecurityPrincipal, Foreign),
        new("S-1-5-21-1000-2000-3000-3001", PrincipalKind.Computer, "FS01$", PrimaryGroupSid: Domain + "-515"),
    ];

    public static readonly MembershipEdge[] Edges =
    [
        new(HrRead, Alice), new(HrRead, Carol), new(HrWrite, Alice),
        new(FinanceRead, Bob), new(FinanceRead, FinanceReviewers), new(FinanceReviewers, Dave),
        new(LoopA, LoopB), new(LoopB, LoopA), new(LoopA, Frank),
        new(Newsletter, Eve),
        new(Partners, Foreign), new(Partners, Bob),
        new(DomainAdmins, Admin),
    ];

    public static readonly LocalGroupSet LocalGroups = new(LocalGroupsState.Read,
    [
        new(WellKnownSid.Administrators, @"BUILTIN\Administrators", [new(DomainAdmins, LocalMemberKind.Group), new("S-1-5-21-555-555-555-500", LocalMemberKind.User)]),
        new(WellKnownSid.Users, @"BUILTIN\Users", [new(WellKnownSid.AuthenticatedUsers, LocalMemberKind.WellKnownGroup), new(DomainUsers, LocalMemberKind.Group)]),
        new("S-1-5-21-555-555-555-1010", @"FS01\Auditors", [new(Eve, LocalMemberKind.User)]),
    ]);

    public static DirectoryGraph Graph(LocalGroupSet? local = null) => new(Principals, Edges, local ?? LocalGroups, Domain);

    public const AceInheritance Both = AceInheritance.ObjectInherit | AceInheritance.ContainerInherit;

    public static Ace Allow(string sid, uint mask, AceInheritance flags = Both) => new(AceKind.Allow, 0, flags, mask, sid);

    public static Ace Deny(string sid, uint mask, AceInheritance flags = Both) => new(AceKind.Deny, 1, flags, mask, sid);

    public static SecurityDescriptor Sd(params Ace[] aces) => new(0, WellKnownSid.Administrators, null, aces);

    public static SecurityDescriptor EveryoneFull => Sd(Allow(WellKnownSid.Everyone, AccessMask.FileAllAccess));

    public static ResourcePermissions Perms(SecurityDescriptor folder, SecurityDescriptor? share = null) =>
        new(PermissionState.Read, share ?? EveryoneFull, PermissionState.Read, folder);
}

public class AccessCalculatorTests
{
    private static readonly DirectoryGraph Graph = Lab.Graph();

    private static AccessResult Files(string user, ResourcePermissions perms) => AccessCalculator.Evaluate(Graph, user, perms, AccessTarget.Files);

    private static readonly uint Modify = 0x1301BF;

    [Fact]
    public void Grants_through_a_direct_group_with_its_chain()
    {
        var result = Files(Lab.Alice, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead))));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Equal(Certainty.Likely, result.Certainty);
        var reason = Assert.Single(result.Reasons, r => r.Source == AccessTarget.Files);
        Assert.Equal([Lab.Alice, Lab.HrRead], reason.Chain);
        Assert.Equal(Rights.None, Files(Lab.Bob, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead)))).UpTo);
    }

    [Fact]
    public void Follows_nested_groups()
    {
        var result = Files(Lab.Dave, Lab.Perms(Lab.Sd(Lab.Allow(Lab.FinanceRead, AccessMask.FileGenericRead))));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Equal([Lab.Dave, Lab.FinanceReviewers, Lab.FinanceRead], result.Reasons.Single(r => r.Source == AccessTarget.Files).Chain);
    }

    [Fact]
    public void Uses_the_primary_group_and_implicit_groups()
    {
        var viaDomainUsers = Files(Lab.Eve, Lab.Perms(Lab.Sd(Lab.Allow(Lab.DomainUsers, AccessMask.FileGenericRead))));
        Assert.Equal([Lab.Eve, Lab.DomainUsers], viaDomainUsers.Reasons.Single(r => r.Source == AccessTarget.Files).Chain);
        var viaAuthUsers = Files(Lab.Eve, Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.AuthenticatedUsers, Modify))));
        Assert.Equal(Rights.Modify, viaAuthUsers.Rights);
    }

    [Fact]
    public void Distribution_groups_grant_nothing()
    {
        var result = Files(Lab.Eve, Lab.Perms(Lab.Sd(Lab.Allow(Lab.Newsletter, AccessMask.FileAllAccess))));
        Assert.Equal(Rights.None, result.UpTo);
        Assert.Equal(Certainty.Likely, result.Certainty);
    }

    [Fact]
    public void A_deny_removes_rights()
    {
        var folder = Lab.Sd(Lab.Deny(Lab.Alice, AccessMask.WriteData | AccessMask.AppendData | AccessMask.Delete),
            Lab.Allow(Lab.HrWrite, Modify, Lab.Both | AceInheritance.Inherited));
        var result = Files(Lab.Alice, Lab.Perms(folder));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.DenyApplied);
        Assert.Contains(result.Reasons, r => r.Ace.IsDeny && r.Chain.SequenceEqual([Lab.Alice]));
    }

    [Fact]
    public void Entries_apply_in_stored_order_like_windows()
    {
        // Allow before deny: Windows grants, because the allow decides the bits first.
        var folder = Lab.Sd(Lab.Allow(Lab.HrWrite, Modify), Lab.Deny(Lab.Alice, AccessMask.WriteData));
        var result = Files(Lab.Alice, Lab.Perms(folder));
        Assert.Equal(Rights.Modify, result.Rights);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.NonCanonicalOrder);
        Assert.DoesNotContain(result.Reasons, r => r.Ace.IsDeny);
    }

    [Fact]
    public void Separates_folder_entries_from_entries_passed_to_files()
    {
        var folder = Lab.Sd(
            Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead, AceInheritance.ContainerInherit),
            Lab.Allow(Lab.HrWrite, Modify, AceInheritance.ObjectInherit | AceInheritance.InheritOnly));
        var perms = Lab.Perms(folder);
        Assert.Equal(Rights.Read, AccessCalculator.Evaluate(Graph, Lab.Alice, perms, AccessTarget.Folder).Rights);
        Assert.Equal(Rights.Modify, Files(Lab.Alice, perms).Rights);
        Assert.Equal(Rights.None, Files(Lab.Carol, perms).UpTo);
    }

    [Fact]
    public void Share_and_ntfs_both_limit_access()
    {
        var folder = Lab.Sd(Lab.Allow(Lab.HrWrite, Modify));
        var readShare = Lab.Sd(Lab.Allow(WellKnownSid.Everyone, AccessMask.ShareRead));
        var shareLimited = Files(Lab.Alice, Lab.Perms(folder, readShare));
        Assert.Equal(Rights.Read, shareLimited.Rights);
        Assert.Equal(Rights.Read, shareLimited.ShareRights);
        Assert.Equal(Rights.Modify, shareLimited.FolderRights);
        Assert.Contains(shareLimited.Notes, n => n.Code == AccessNoteCode.ShareLimits);

        var ntfsLimited = Files(Lab.Alice, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead))));
        Assert.Equal(Rights.Read, ntfsLimited.Rights);
        Assert.Contains(ntfsLimited.Notes, n => n.Code == AccessNoteCode.FolderLimits);

        var noShareAccess = Files(Lab.Alice, Lab.Perms(folder, Lab.Sd(Lab.Allow(Lab.FinanceRead, AccessMask.ShareFull))));
        Assert.Equal(Rights.None, noShareAccess.UpTo);
    }

    [Fact]
    public void A_user_restricted_by_the_share_is_not_shown_with_ntfs_rights()
    {
        // Acceptance A8: NTFS full control, share denies writes for this user.
        var folder = Lab.Sd(Lab.Allow(Lab.Alice, AccessMask.FileAllAccess));
        var share = Lab.Sd(Lab.Deny(Lab.HrRead, AccessMask.WriteData | AccessMask.AppendData | AccessMask.Delete | AccessMask.WriteDac | AccessMask.WriteOwner),
            Lab.Allow(WellKnownSid.Everyone, AccessMask.ShareFull));
        var result = Files(Lab.Alice, Lab.Perms(folder, share));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Equal(Rights.Read, result.UpTo);
    }

    [Fact]
    public void Unread_share_permissions_give_an_upper_bound_only()
    {
        var folder = Lab.Sd(Lab.Allow(Lab.HrWrite, Modify));
        var result = AccessCalculator.Evaluate(Graph, Lab.Alice, new ResourcePermissions(PermissionState.AccessDenied, null, PermissionState.Read, folder), AccessTarget.Files);
        Assert.Equal(Certainty.Partial, result.Certainty);
        Assert.Equal(Rights.None, result.Rights);
        Assert.Equal(Rights.Modify, result.UpTo);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.ShareNotRead);

        // No NTFS access is certain even without the share permissions.
        var none = AccessCalculator.Evaluate(Graph, Lab.Bob, new ResourcePermissions(PermissionState.AccessDenied, null, PermissionState.Read, folder), AccessTarget.Files);
        Assert.Equal(Certainty.Likely, none.Certainty);
        Assert.Equal(Rights.None, none.UpTo);
    }

    [Fact]
    public void Unread_folder_permissions_are_unknown_not_none()
    {
        var result = AccessCalculator.Evaluate(Graph, Lab.Alice, new ResourcePermissions(PermissionState.Read, Lab.EveryoneFull, PermissionState.AccessDenied, null), AccessTarget.Files);
        Assert.Equal(Certainty.Unknown, result.Certainty);
        Assert.Equal(Rights.FullControl, result.UpTo);
    }

    [Fact]
    public void Disabled_accounts_keep_latent_access_with_a_note()
    {
        var result = Files(Lab.Carol, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead))));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.AccountDisabled);
    }

    [Fact]
    public void Nesting_loops_terminate_and_are_reported()
    {
        var result = Files(Lab.Frank, Lab.Perms(Lab.Sd(Lab.Allow(Lab.LoopB, AccessMask.FileGenericRead))));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Equal([Lab.Frank, Lab.LoopA, Lab.LoopB], result.Reasons.Single(r => r.Source == AccessTarget.Files).Chain);
        var cycle = Assert.Single(Graph.Cycles);
        Assert.Equal([Lab.LoopA, Lab.LoopB], cycle);
    }

    [Fact]
    public void Unresolved_members_make_access_partial_not_none()
    {
        // Partners contains an account from another domain that Data Scope cannot expand.
        var perms = Lab.Perms(Lab.Sd(Lab.Allow(Lab.Partners, Modify)));
        var outsider = Files(Lab.Eve, perms);
        Assert.Equal(Certainty.Partial, outsider.Certainty);
        Assert.Equal(Rights.None, outsider.Rights);
        Assert.Equal(Rights.Modify, outsider.UpTo);
        Assert.Contains(outsider.Notes, n => n.Code == AccessNoteCode.UnresolvedMembership);

        var member = Files(Lab.Bob, perms);
        Assert.Equal(Certainty.Likely, member.Certainty);
        Assert.Equal(Rights.Modify, member.Rights);
        Assert.Contains(Lab.Partners, Graph.UncertainGroups);
    }

    [Fact]
    public void Uses_the_file_servers_local_groups()
    {
        var builtinUsers = Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.Users, AccessMask.FileGenericRead)));
        var eve = Files(Lab.Eve, builtinUsers);
        Assert.Equal(Rights.Read, eve.Rights);
        Assert.Equal(Certainty.Likely, eve.Certainty);
        Assert.Equal(WellKnownSid.Users, eve.Reasons.Single(r => r.Source == AccessTarget.Files).Chain[^1]);

        var admins = Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.Administrators, AccessMask.FileAllAccess)));
        Assert.Equal(Rights.FullControl, Files(Lab.Admin, admins).Rights);
        Assert.Equal([Lab.Admin, Lab.DomainAdmins, WellKnownSid.Administrators], Files(Lab.Admin, admins).Reasons.Single(r => r.Source == AccessTarget.Files).Chain);
        Assert.Equal(Rights.None, Files(Lab.Eve, admins).UpTo);

        var localAuditors = Lab.Perms(Lab.Sd(Lab.Allow("S-1-5-21-555-555-555-1010", AccessMask.FileGenericRead)));
        Assert.Equal(Rights.Read, Files(Lab.Eve, localAuditors).Rights);
        Assert.Equal(Rights.None, Files(Lab.Bob, localAuditors).UpTo);
    }

    [Fact]
    public void Unknown_local_groups_make_builtin_entries_partial()
    {
        var graph = Lab.Graph(new LocalGroupSet(LocalGroupsState.AccessDenied, []));
        var perms = Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.Users, AccessMask.FileGenericRead), Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead)));
        var eve = AccessCalculator.Evaluate(graph, Lab.Eve, perms, AccessTarget.Files);
        Assert.Equal(Certainty.Partial, eve.Certainty);
        Assert.Equal(Rights.Read, eve.UpTo);
        Assert.Contains(eve.Notes, n => n.Code == AccessNoteCode.LocalGroupsUnknown);

        // A certain grant elsewhere is still certain.
        var alice = AccessCalculator.Evaluate(graph, Lab.Alice, perms, AccessTarget.Files);
        Assert.Equal(Certainty.Likely, alice.Certainty);
        Assert.Equal(Rights.Read, alice.Rights);
    }

    [Fact]
    public void Sid_history_counts()
    {
        var result = Files(Lab.Grace, Lab.Perms(Lab.Sd(Lab.Allow(Lab.GraceOld, Modify))));
        Assert.Equal(Rights.Modify, result.Rights);
        Assert.Equal([Lab.Grace, Lab.GraceOld], result.Reasons.Single(r => r.Source == AccessTarget.Files).Chain);
    }

    [Fact]
    public void Orphaned_entries_match_nobody()
    {
        var deleted = Lab.Domain + "-1999";
        var result = Files(Lab.Eve, Lab.Perms(Lab.Sd(Lab.Allow(deleted, AccessMask.FileAllAccess))));
        Assert.Equal(Rights.None, result.UpTo);
        Assert.Equal(Certainty.Likely, result.Certainty);
        Assert.False(Graph.IsKnown(deleted));
        Assert.StartsWith("Deleted account", Graph.NameOf(deleted), StringComparison.Ordinal);
    }

    [Fact]
    public void Conditional_entries_are_never_certain()
    {
        var folder = Lab.Sd(new Ace(AceKind.ConditionalAllow, 9, Lab.Both, Modify, Lab.HrRead));
        var result = Files(Lab.Alice, Lab.Perms(folder));
        Assert.Equal(Certainty.Partial, result.Certainty);
        Assert.Equal(Rights.Modify, result.UpTo);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.ConditionalEntry);
    }

    [Fact]
    public void No_dacl_means_everyone_and_an_empty_dacl_means_nobody()
    {
        var open = Files(Lab.Eve, Lab.Perms(new SecurityDescriptor(0, null, null, null)));
        Assert.Equal(Rights.FullControl, open.Rights);
        Assert.Contains(open.Notes, n => n.Code == AccessNoteCode.NoDacl);
        Assert.Equal(Rights.None, Files(Lab.Eve, Lab.Perms(Lab.Sd())).UpTo);
    }

    [Fact]
    public void Owners_can_change_folder_permissions_unless_owner_rights_says_otherwise()
    {
        var owned = new SecurityDescriptor(0, Lab.Alice, null, [Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead)]);
        var asFolder = AccessCalculator.Evaluate(Graph, Lab.Alice, Lab.Perms(owned), AccessTarget.Folder);
        Assert.Equal(Rights.FullControl, asFolder.Rights);
        Assert.Contains(asFolder.Notes, n => n.Code == AccessNoteCode.OwnerCanChangePermissions);

        // Over a read-only share the owner cannot change anything.
        var readShare = Lab.Sd(Lab.Allow(WellKnownSid.Everyone, AccessMask.ShareRead));
        Assert.Equal(Rights.Read, AccessCalculator.Evaluate(Graph, Lab.Alice, Lab.Perms(owned, readShare), AccessTarget.Folder).Rights);

        var restricted = owned with { Dacl = [.. owned.Dacl!, Lab.Allow(WellKnownSid.OwnerRights, AccessMask.FileGenericRead)] };
        Assert.Equal(Rights.Read, AccessCalculator.Evaluate(Graph, Lab.Alice, Lab.Perms(restricted), AccessTarget.Folder).Rights);
    }

    [Fact]
    public void Creator_owner_is_reported_for_files_not_evaluated()
    {
        var folder = Lab.Sd(Lab.Allow(WellKnownSid.CreatorOwner, AccessMask.FileAllAccess, Lab.Both | AceInheritance.InheritOnly),
            Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead));
        var result = Files(Lab.Alice, Lab.Perms(folder));
        Assert.Equal(Rights.Read, result.Rights);
        Assert.Contains(result.Notes, n => n.Code == AccessNoteCode.CreatorOwner);
    }

    [Fact]
    public void Generic_rights_are_expanded()
    {
        var result = Files(Lab.Alice, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.GenericRead | AccessMask.GenericWrite))));
        Assert.Equal(Rights.Write, result.Rights);
        Assert.Equal(Rights.FullControl, Files(Lab.Alice, Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.GenericAll)))).Rights);
    }

    [Fact]
    public void Lists_who_has_access_most_first()
    {
        var folder = Lab.Sd(Lab.Allow(Lab.HrWrite, Modify), Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead), Lab.Allow(Lab.Partners, AccessMask.FileGenericRead));
        var list = AccessCalculator.WhoHasAccess(Graph, Lab.Perms(folder), AccessTarget.Files);
        Assert.Equal(Lab.Alice, list[0].UserSid);
        Assert.Contains(list, r => r.UserSid == Lab.Carol && r.Rights == Rights.Read);
        Assert.Contains(list, r => r.UserSid == Lab.Bob && r.Rights == Rights.Read && r.Certainty == Certainty.Likely);
        // Everyone else might be in Partners through the unresolved member: listed as possible, never as certain.
        Assert.Contains(list, r => r.UserSid == Lab.Eve && r.Rights == Rights.None && r.UpTo == Rights.Read);
        Assert.DoesNotContain(list, r => r.UserSid == "S-1-5-21-1000-2000-3000-3001");
    }

    [Fact]
    public void Counts_group_members()
    {
        Assert.Equal((2, 0), Graph.CountMembers(Lab.HrRead));
        Assert.Equal((8, 0), Graph.CountMembers(Lab.DomainUsers));
        Assert.Equal((1, 7), Graph.CountMembers(Lab.Partners));
    }
}

public class AnyDomainUserTests
{
    [Fact]
    public void Has_only_what_every_domain_account_gets()
    {
        var graph = Lab.Graph();
        var any = graph.AnyDomainUser;
        Assert.Equal(Membership.Yes, any.Contains(Lab.DomainUsers));
        Assert.Equal(Membership.Yes, any.Contains(WellKnownSid.AuthenticatedUsers));
        Assert.Equal(Membership.Yes, any.Contains(WellKnownSid.Users));
        Assert.Equal(Membership.No, any.Contains(Lab.HrRead));
        Assert.Equal(Membership.No, any.Contains(WellKnownSid.Administrators));
        Assert.Equal(Membership.Maybe, any.Contains(Lab.Partners));
        Assert.Equal(Lab.Domain + "-0", any.UserSid);
    }

    [Fact]
    public void Shows_open_access()
    {
        var graph = Lab.Graph();
        var open = Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.Users, AccessMask.FileGenericRead)));
        var result = AccessCalculator.Evaluate(graph, graph.AnyDomainUser, open, AccessTarget.Files);
        Assert.Equal(Rights.Read, result.Rights);
        Assert.DoesNotContain(result.Notes, n => n.Code == AccessNoteCode.UnknownUser);
        var closed = Lab.Perms(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead)));
        Assert.Equal(Rights.None, AccessCalculator.Evaluate(graph, graph.AnyDomainUser, closed, AccessTarget.Files).UpTo);
    }

    [Fact]
    public void Read_grants_list_every_entry_that_lets_an_account_read_not_only_the_first()
    {
        var folder = Lab.Sd(
            Lab.Allow(WellKnownSid.Everyone, AccessMask.FileGenericRead),
            Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead, Lab.Both | AceInheritance.Inherited),
            Lab.Allow(Lab.HrWrite, 0x1301BF),
            Lab.Allow(Lab.FinanceRead, AccessMask.FileGenericRead),
            Lab.Allow(Lab.Alice, AccessMask.FileGenericRead, AceInheritance.ContainerInherit),
            Lab.Allow(Lab.Alice, AccessMask.WriteData, Lab.Both),
            Lab.Deny(Lab.Alice, AccessMask.FileGenericRead));
        var grants = AccessCalculator.ReadGrants(folder, AccessTarget.Files, Lab.Graph().TokenFor(Lab.Alice));

        // Everyone, HR_Read and HR_Write all let alice read files; Finance_Read doesn't include her, the entry naming
        // her directly applies only to subfolders, a write-only entry is not read, and deny entries are never grants.
        Assert.Equal(string.Join(' ', WellKnownSid.Everyone, Lab.HrRead, Lab.HrWrite), string.Join(' ', grants.Select(g => g.Ace.Sid)));
        Assert.All(grants, g => Assert.Equal(Membership.Yes, g.Match));
    }

    [Fact]
    public void Read_grants_through_uncertain_membership_or_conditions_are_only_possible()
    {
        var folder = Lab.Sd(
            Lab.Allow(Lab.Partners, AccessMask.FileGenericRead),
            new Ace(AceKind.ConditionalAllow, 9, Lab.Both, AccessMask.FileGenericRead, Lab.FinanceRead));
        var grants = AccessCalculator.ReadGrants(folder, AccessTarget.Files, Lab.Graph().TokenFor(Lab.Bob));
        Assert.Contains(grants, g => g.Ace.Sid == Lab.FinanceRead && g.Match == Membership.Maybe);
        Assert.Empty(AccessCalculator.ReadGrants(Lab.Sd(Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead)), AccessTarget.Files, Lab.Graph().TokenFor(Lab.Bob)));
    }

    [Fact]
    public void A_write_only_entry_is_not_reading()
    {
        // A drop box: everyone can add files but not read them. Rights says Write; it must not count as reading.
        var graph = Lab.Graph();
        var dropBox = Lab.Perms(Lab.Sd(Lab.Allow(WellKnownSid.Everyone, AccessMask.WriteData | AccessMask.AppendData)));
        var result = AccessCalculator.Evaluate(graph, Lab.Alice, dropBox, AccessTarget.Files);
        Assert.Equal(Rights.Write, result.Rights);
        Assert.False(result.CanRead);
        Assert.False(result.MightRead);
        Assert.True(AccessCalculator.Evaluate(graph, Lab.Alice, Lab.Perms(Lab.EveryoneFull), AccessTarget.Files).CanRead);
    }
}
