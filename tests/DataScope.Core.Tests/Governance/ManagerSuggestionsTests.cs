using DataScope.Core.Access;
using DataScope.Core.Governance;
using DataScope.Core.Tests.Access;

namespace DataScope.Core.Tests.Governance;

public class ManagerSuggestionsTests
{
    private const string D = "S-1-5-21-4000-5000-6000";
    private const string DomainUsers = D + "-513";
    private const string Ceo = D + "-1100";
    private const string HrHead = D + "-1101";
    private const string HrA = D + "-1102";
    private const string HrB = D + "-1103";
    private const string HrLeft = D + "-1104";     // disabled
    private const string FinA = D + "-1110";
    private const string FinB = D + "-1111";
    private const string FinHead = D + "-1112";    // manages both, but is not in the group
    private const string Loop1 = D + "-1120";
    private const string Loop2 = D + "-1121";
    private const string HrStaff = D + "-2001";
    private const string HrAll = D + "-2002";      // nests HrStaff
    private const string Finance = D + "-2003";
    private const string Leaders = D + "-2004";
    private const string Looped = D + "-2005";
    private const uint Modify = 0x1301BF;

    private static DirectoryGraph Graph() => new(
        [
            new(Ceo, PrincipalKind.User, "ceo", PrimaryGroupSid: DomainUsers),
            new(HrHead, PrincipalKind.User, "hr.head", PrimaryGroupSid: DomainUsers, ManagerSid: Ceo),
            new(HrA, PrincipalKind.User, "hr.a", PrimaryGroupSid: DomainUsers, ManagerSid: HrHead),
            new(HrB, PrincipalKind.User, "hr.b", PrimaryGroupSid: DomainUsers, ManagerSid: HrHead),
            new(HrLeft, PrincipalKind.User, "hr.left", Enabled: false, PrimaryGroupSid: DomainUsers, ManagerSid: HrHead),
            new(FinHead, PrincipalKind.User, "fin.head", PrimaryGroupSid: DomainUsers, ManagerSid: Ceo),
            new(FinA, PrincipalKind.User, "fin.a", PrimaryGroupSid: DomainUsers, ManagerSid: FinHead),
            new(FinB, PrincipalKind.User, "fin.b", PrimaryGroupSid: DomainUsers, ManagerSid: FinHead),
            new(Loop1, PrincipalKind.User, "loop.1", PrimaryGroupSid: DomainUsers, ManagerSid: Loop2),
            new(Loop2, PrincipalKind.User, "loop.2", PrimaryGroupSid: DomainUsers, ManagerSid: Loop1),
            new(DomainUsers, PrincipalKind.Group, "Domain Users"),
            new(HrStaff, PrincipalKind.Group, "HR_Staff"),
            new(HrAll, PrincipalKind.Group, "HR_All"),
            new(Finance, PrincipalKind.Group, "Finance"),
            new(Leaders, PrincipalKind.Group, "Leaders"),
            new(Looped, PrincipalKind.Group, "Looped"),
        ],
        [
            new(HrStaff, HrA), new(HrStaff, HrB), new(HrStaff, HrLeft),
            new(HrAll, HrStaff), new(HrAll, HrHead),
            new(Finance, FinA), new(Finance, FinB),
            new(Leaders, Ceo), new(Leaders, HrHead),
            new(Looped, Loop1), new(Looped, Loop2),
        ],
        null,
        D);

    [Fact]
    public void Suggests_the_member_who_manages_the_most_others_including_nested_groups()
    {
        var found = ManagerSuggestions.For(Graph(), Lab.Sd(Lab.Allow(HrAll, Modify)));
        var top = Assert.Single(found);
        Assert.Equal(HrHead, top.Sid);
        Assert.Equal(HrAll, top.GroupSid);
        Assert.Equal(2, top.Manages); // the disabled account is not counted
        Assert.Equal(3, top.Members);
        Assert.True(top.InGroup);
        Assert.Equal(Rights.Modify, top.Rights);
    }

    [Fact]
    public void Falls_back_to_the_shared_manager_when_nobody_in_the_group_manages_anyone()
    {
        var top = Assert.Single(ManagerSuggestions.For(Graph(), Lab.Sd(Lab.Allow(Finance, AccessMask.FileGenericRead))));
        Assert.Equal(FinHead, top.Sid);
        Assert.False(top.InGroup);
        Assert.Equal(Rights.Read, top.Rights);
    }

    [Fact]
    public void Ranks_by_how_many_people_the_suggestion_covers()
    {
        var found = ManagerSuggestions.For(Graph(), Lab.Sd(Lab.Allow(Leaders, Modify), Lab.Allow(HrAll, AccessMask.FileGenericRead)));
        Assert.Equal([HrHead, Ceo], found.Select(f => f.Sid));
    }

    [Fact]
    public void Skips_broad_administrative_and_deny_entries_and_people_granted_directly()
    {
        var sd = Lab.Sd(
            Lab.Allow(WellKnownSid.Everyone, Modify),
            Lab.Allow(DomainUsers, Modify),
            Lab.Allow(WellKnownSid.Administrators, AccessMask.FileAllAccess),
            Lab.Deny(Finance, Modify),
            Lab.Allow(HrA, Modify));
        Assert.Empty(ManagerSuggestions.For(Graph(), sd));
    }

    [Fact]
    public void Survives_a_reporting_loop_and_says_nothing_without_a_clear_manager()
    {
        // Each manages the other: one of them is picked, and the walk ends.
        var found = ManagerSuggestions.For(Graph(), Lab.Sd(Lab.Allow(Looped, Modify)));
        Assert.Single(found);
        Assert.Equal(1, found[0].Manages);
        Assert.Equal([Loop2], ManagerSuggestions.Chain(Graph(), Loop1));
    }

    [Fact]
    public void Says_nothing_when_the_directory_has_no_reporting_lines()
    {
        var bare = new DirectoryGraph(
            [new(HrA, PrincipalKind.User, "a"), new(HrB, PrincipalKind.User, "b"), new(HrStaff, PrincipalKind.Group, "HR_Staff")],
            [new(HrStaff, HrA), new(HrStaff, HrB)], null, D);
        Assert.Empty(ManagerSuggestions.For(bare, Lab.Sd(Lab.Allow(HrStaff, Modify))));
    }
}
