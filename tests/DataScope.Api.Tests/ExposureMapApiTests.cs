using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Access;
using DataScope.Core.Security;

namespace DataScope.Api.Tests;

/// <summary>
/// Sensitive Data Exposure Map (D125) on the access lab: Restricted files in HR\Payroll (HR_Managers only), HR\Benefits
/// (Everyone), Finance (share permissions unread, so only a range) and Ops\IT (folder permissions unreadable).
/// </summary>
public sealed partial class AccessApiTests
{
    private sealed record MapSummary(int SensitiveFolders, long RestrictedFiles, long ConfidentialFiles, int OpenFolders, int PossiblyOpenFolders, int UnknownFolders,
        int LimitedFolders, long OpenFiles, int People, int PeoplePossible, int Groups);
    private sealed record MapCoverage(long FilesNotRead, long FilesExcluded, int SharesNotFullyScanned, int SharesLeftOut, int SharesPermissionsNotRead,
        int FoldersUnknown, int FoldersUncertain, int DisabledAccounts, bool Truncated);
    private sealed record MapShare(Guid ShareId, string Name, bool Included, string SharePermissions, long Restricted, int SensitiveFolders, int OpenFolders,
        int PossiblyOpenFolders, int UnknownFolders, int People, int PeoplePossible);
    private sealed record MapGrant(Trustee Trustee, string Rights, int People, int PeoplePossible);
    private sealed record MapFolder(Guid FolderId, Guid ShareId, string UncPath, int Restricted, int Confidential, string Status, Range AnyDomainUser, int People,
        int PeoplePossible, List<MapGrant> Grants);
    private sealed record MapGroup(Trustee Trustee, int People, int PeoplePossible, int? MembersCertain, int? MembersPossible, int Folders, int Shares, long Restricted,
        string HighestRights);
    private sealed record MapPerson(string Sid, string Name, long Restricted, long Confidential, long PossibleFiles, int Folders, int Shares, List<string> Via);
    private sealed record ExposureMapView(MapSummary Summary, MapCoverage Coverage, List<MapShare> Shares, List<MapFolder> Folders, int FoldersTotal,
        List<MapGroup> Groups, int GroupsTotal, List<MapPerson> People, int PeopleTotal, string Minimum);
    private sealed record MapVia(Trustee Trustee, bool Certain, string Rights);
    private sealed record PersonFolder(Guid FolderId, string UncPath, int Restricted, string Status, string Rights, string UpTo, bool Certain, List<MapVia> Via);
    private sealed record PersonDetail(UserAccess Account, MapPerson? Totals, List<PersonFolder> Folders, int FoldersTotal);
    private sealed record GroupFolder(Guid FolderId, string UncPath, int Restricted, string Status, string Rights, int People, int PeoplePossible);
    private sealed record GroupDetail(Trustee Trustee, MapGroup? Totals, List<GroupFolder> Folders, int FoldersTotal);

    private static string Unc(string path) => $@"\\{Host}\{path}";

    [Fact]
    public async Task The_exposure_map_shows_where_sensitive_files_are_and_how_broadly_they_can_be_read()
    {
        var map = (await Analyst.GetFromJsonAsync<ExposureMapView>("/api/v1/exposure-map"))!;
        var folders = map.Folders.ToDictionary(f => f.UncPath);

        // Benefits is open to the whole domain, so it comes first; everyone enabled can read it, through Everyone.
        var benefits = folders[Unc(@"HR\Benefits")];
        Assert.Equal(benefits, map.Folders[0]);
        Assert.Equal(("Open", 1), (benefits.Status, benefits.Restricted));
        Assert.Equal(3, benefits.People);
        var everyone = Assert.Single(benefits.Grants, g => g.Trustee.Sid == WellKnownSid.Everyone);
        Assert.True(everyone.Trustee.Broad);
        Assert.Equal(3, everyone.People);
        Assert.Contains(benefits.Grants, g => g.Trustee.Name == "HR_Staff" && g.People == 2);

        // Payroll is limited to HR_Managers: one person, and the grant says how.
        var payroll = folders[Unc(@"HR\Payroll")];
        Assert.Equal(("Limited", 1, 0), (payroll.Status, payroll.People, payroll.PeoplePossible));
        var managers = Assert.Single(payroll.Grants);
        Assert.Equal(("HR_Managers", "Modify", 1), (managers.Trustee.Name, managers.Rights, managers.People));

        // Finance's share permissions were not read: people only might read it, never counted as certain.
        var finance = folders[Unc("Finance")];
        Assert.Equal(("PossiblyOpen", 0), (finance.Status, finance.People));
        Assert.True(finance.PeoplePossible >= 1);
        Assert.Contains(finance.AnyDomainUser.Notes, n => n.Code == nameof(AccessNoteCode.ShareNotRead));

        // Ops\IT's permissions could not be read: who can read it is unknown, and no one is listed as if it were known.
        var it = folders[Unc(@"Ops\IT")];
        Assert.Equal(("Unknown", 0, 0), (it.Status, it.People, it.PeoplePossible));
        Assert.Empty(it.Grants);
        Assert.Equal(nameof(Certainty.Unknown), it.AnyDomainUser.Certainty);
    }

    [Fact]
    public async Task The_exposure_map_totals_people_groups_shares_and_what_limits_the_answer()
    {
        var map = (await Analyst.GetFromJsonAsync<ExposureMapView>("/api/v1/exposure-map"))!;
        Assert.Equal("Confidential", map.Minimum);
        Assert.Equal(map.Folders.Count, map.FoldersTotal);
        Assert.Equal((1, 1, 1, 1), (map.Summary.OpenFolders, map.Summary.PossiblyOpenFolders, map.Summary.UnknownFolders, map.Summary.LimitedFolders));
        Assert.Equal(map.Folders.Count, map.Summary.SensitiveFolders);
        Assert.Equal(1, map.Summary.OpenFiles);

        // alice, bob and dave can each read something for certain; carol is disabled and is counted apart.
        Assert.Equal(3, map.Summary.People);
        Assert.Equal(0, map.Summary.PeoplePossible);
        Assert.Equal(1, map.Coverage.DisabledAccounts);
        Assert.DoesNotContain(map.People, p => p.Name == "carol");
        Assert.Equal(1, map.Coverage.FoldersUnknown);
        Assert.True(map.Coverage.FoldersUncertain >= 1);
        Assert.True(map.Coverage.SharesPermissionsNotRead >= 1);
        Assert.False(map.Coverage.Truncated);

        // alice reaches the most Restricted files: Payroll and Benefits for certain, Finance only possibly.
        var alice = map.People[0];
        Assert.Equal(("alice", 2L), (alice.Name, alice.Restricted));
        Assert.True(alice.PossibleFiles >= 1);
        Assert.Contains("HR_Managers", alice.Via);
        var bob = Assert.Single(map.People, p => p.Name == "bob");
        Assert.Equal(1, bob.Restricted);

        // Groups: Everyone reaches the most people; HR_Managers grants Modify on two folders; Finance only possibly.
        Assert.Equal(WellKnownSid.Everyone, map.Groups[0].Trustee.Sid);
        var managers = Assert.Single(map.Groups, g => g.Trustee.Name == "HR_Managers");
        Assert.Equal((1, 2, "Modify"), (managers.People, managers.Folders, managers.HighestRights));
        Assert.Equal(1, managers.MembersCertain);
        var fin = Assert.Single(map.Groups, g => g.Trustee.Name == "Finance");
        Assert.Equal((0, 1), (fin.People, fin.PeoplePossible));
        Assert.Equal(map.Groups.Count, map.Summary.Groups);

        // Shares: HR holds two sensitive folders, one open; Finance's share permissions are not read.
        var hr = Assert.Single(map.Shares, s => s.Name == "HR");
        Assert.Equal((2, 1, 3), (hr.SensitiveFolders, hr.OpenFolders, hr.People));
        Assert.Equal("Read", hr.SharePermissions);
        var financeShare = Assert.Single(map.Shares, s => s.Name == "Finance");
        Assert.NotEqual("Read", financeShare.SharePermissions);
        Assert.Contains(map.Shares, s => s.Name == "Public" && s.SensitiveFolders == 0);
    }

    [Fact]
    public async Task The_exposure_map_can_be_narrowed_to_a_share()
    {
        var hr = (await Analyst.GetFromJsonAsync<ExposureMapView>($"/api/v1/exposure-map?shareId={_lab.Shares["HR"]}"))!;
        Assert.All(hr.Folders, f => Assert.Equal(_lab.Shares["HR"], f.ShareId));
        Assert.Equal(2, hr.Summary.SensitiveFolders);
        Assert.Equal("HR", Assert.Single(hr.Shares).Name);

        var restricted = (await Analyst.GetFromJsonAsync<ExposureMapView>("/api/v1/exposure-map?minSensitivity=restricted"))!;
        Assert.Equal("Restricted", restricted.Minimum);
        Assert.All(restricted.Folders, f => Assert.True(f.Restricted > 0));
    }

    [Fact]
    public async Task A_person_drill_down_says_which_entries_let_them_read()
    {
        var alice = (await Analyst.GetFromJsonAsync<PersonDetail>($"/api/v1/exposure-map/people/{Alice}"))!;
        Assert.Equal("alice", alice.Account.Name);
        Assert.Equal(alice.Folders.Count, alice.FoldersTotal);
        Assert.Equal(2, alice.Totals!.Restricted);
        var payroll = Assert.Single(alice.Folders, f => f.UncPath == Unc(@"HR\Payroll"));
        Assert.True(payroll.Certain);
        var via = Assert.Single(payroll.Via);
        Assert.Equal(("HR_Managers", true), (via.Trustee.Name, via.Certain));
        var finance = Assert.Single(alice.Folders, f => f.UncPath == Unc("Finance"));
        Assert.False(finance.Certain);
        Assert.Equal("No access", finance.Rights);
        Assert.Equal(Unc(@"HR\Benefits"), alice.Folders[0].UncPath == Unc(@"HR\Payroll") ? alice.Folders[1].UncPath : alice.Folders[0].UncPath);

        // A disabled account can't sign in, so it has nothing here; an unknown SID has nothing either.
        var carol = (await Analyst.GetFromJsonAsync<PersonDetail>($"/api/v1/exposure-map/people/{Carol}"))!;
        Assert.Empty(carol.Folders);
        Assert.False(carol.Account.Enabled);
        var nobody = (await Analyst.GetFromJsonAsync<PersonDetail>($"/api/v1/exposure-map/people/{Sid(4242)}"))!;
        Assert.Empty(nobody.Folders);
        Assert.Null(nobody.Totals);
    }

    [Fact]
    public async Task A_group_drill_down_lists_the_sensitive_folders_it_opens()
    {
        var staff = (await Analyst.GetFromJsonAsync<GroupDetail>($"/api/v1/exposure-map/groups/{HrStaff}"))!;
        Assert.Equal("HR_Staff", staff.Trustee.Name);
        var benefits = Assert.Single(staff.Folders);
        Assert.Equal((Unc(@"HR\Benefits"), 2, "Read"), (benefits.UncPath, benefits.People, benefits.Rights));
        Assert.Equal(2, staff.Totals!.People);

        var everyone = (await Analyst.GetFromJsonAsync<GroupDetail>($"/api/v1/exposure-map/groups/{WellKnownSid.Everyone}"))!;
        Assert.True(everyone.Trustee.Broad);
        Assert.Contains(everyone.Folders, f => f.UncPath == Unc(@"HR\Benefits") && f.Status == "Open");

        var none = (await Analyst.GetFromJsonAsync<GroupDetail>($"/api/v1/exposure-map/groups/{LoopA}"))!;
        Assert.Empty(none.Folders);
        Assert.Null(none.Totals);
    }

    [Theory]
    [InlineData("/api/v1/exposure-map?minSensitivity=Internal")]
    [InlineData("/api/v1/exposure-map?minSensitivity=everything")]
    [InlineData("/api/v1/exposure-map/people/not-a-sid")]
    [InlineData("/api/v1/exposure-map/people/S-1-5-21-10-20-30-1201")]
    [InlineData("/api/v1/exposure-map/groups/not-a-sid")]
    [InlineData("/api/v1/exposure-map/groups/S-1-5-21-10-20-30-1201?minSensitivity=Public")]
    public async Task Bad_exposure_map_requests_are_rejected(string url)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Analyst.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData(Roles.PlatformAdmin)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Viewer)]
    public async Task Posture_readers_can_see_the_exposure_map(string role)
    {
        Assert.Equal(HttpStatusCode.OK, (await _lab.Factory.ClientAs(role).GetAsync("/api/v1/exposure-map")).StatusCode);
    }

    [Fact]
    public async Task Others_cannot_see_the_exposure_map()
    {
        foreach (var url in new[] { "/api/v1/exposure-map", $"/api/v1/exposure-map/people/{Alice}", $"/api/v1/exposure-map/groups/{HrStaff}" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _lab.Factory.ClientAs(null).GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.DataOwner).GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task The_exposure_map_can_be_narrowed_to_a_compliance_standard()
    {
        var admin = _lab.Factory.ClientAs(Roles.PlatformAdmin);
        var on = await admin.PutAsJsonAsync("/api/v1/compliance/standards/pci-dss", new { enabled = true, reason = "Finance takes card payments." });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        var map = (await Analyst.GetFromJsonAsync<ExposureMapView>("/api/v1/exposure-map?standard=pci-dss"))!;
        Assert.Equal([Unc("Finance")], map.Folders.Select(f => f.UncPath));
        Assert.Equal(1, map.Summary.SensitiveFolders);
        // What limits the map still covers every file not read, whatever the standard.
        var all = (await Analyst.GetFromJsonAsync<ExposureMapView>("/api/v1/exposure-map"))!;
        Assert.Equal(all.Coverage.FilesNotRead, map.Coverage.FilesNotRead);
        Assert.True(all.Folders.Count > 1);
    }
}
