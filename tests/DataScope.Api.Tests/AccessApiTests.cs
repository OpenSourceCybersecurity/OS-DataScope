using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.ActiveDirectory;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Scanner.Content;
using DataScope.Scanner.Security;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>
/// Access end to end: a synthetic file server with permissions, a synthetic domain, the real scan and directory
/// sync, then the API's answers. Every account, SID and file here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed partial class AccessApiTests : IClassFixture<AccessApiTests.Lab>
{
    private const string Host = "acc-fs01";
    private const string Domain = "S-1-5-21-10-20-30";
    private static readonly string Alice = Sid(1101), Bob = Sid(1102), Carol = Sid(1103), Dave = Sid(1104);
    private static readonly string HrStaff = Sid(1201), HrManagers = Sid(1202), Fin = Sid(1203), Partners = Sid(1204), LoopA = Sid(1205), LoopB = Sid(1206);
    private static readonly string Orphan = Sid(1999);

    private readonly Lab _lab;

    public AccessApiTests(Lab lab) => _lab = lab;

    private static string Sid(int rid) => $"{Domain}-{rid}";

    /// <summary>Builds the lab once per class: files and permissions on disk, a scan, and a directory sync.</summary>
    public sealed class Lab : IAsyncLifetime
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ds-access-").FullName;

        public TestDatabase Database { get; } = new();
        public ApiFactory Factory { get; private set; } = null!;
        public Guid ServerId { get; private set; }
        public Dictionary<string, Guid> Folders { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> Shares { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> Files { get; } = new(StringComparer.Ordinal);

        public async Task InitializeAsync()
        {
            await Database.InitializeAsync();
            Factory = new ApiFactory(Database);
            WriteFiles();
            WritePermissions();
            var directoryFile = WriteDirectory();

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataScope"] = Database.ConnectionString,
                ["Scanner:LeaseSeconds"] = "60",
            }).Build();
            var services = new ServiceCollection();
            services.AddDataScopePersistence(config);
            await using var sp = services.BuildServiceProvider();
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();

            ServerId = await QueueScanAsync();
            var executor = new InventoryScanExecutor(scopes, new LabShareEnumerator(_root), new FileSystemDirectorySource(),
                new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero, 100), NullLogger<InventoryScanExecutor>.Instance,
                new FileContentReader(new ContentLimits()), new LabSecurityReader(_root));
            Assert.True(await new ScanWorker(scopes, executor, config, NullLogger<ScanWorker>.Instance).RunOneAsync(CancellationToken.None));

            var sync = new DirectorySyncWorker(scopes, config, NullLogger<DirectorySyncWorker>.Instance, new LabDirectoryReader(directoryFile));
            Assert.True(await sync.RunOneAsync(CancellationToken.None));

            await using var db = Database.CreateContext();
            foreach (var s in await db.Shares.Where(s => s.ServerId == ServerId).ToListAsync())
            {
                Shares[s.Name] = s.Id;
            }
            foreach (var f in await (from f in db.Folders join s in db.Shares on f.ShareId equals s.Id select new { f.Id, s.Name, f.Path }).ToListAsync())
            {
                Folders[f.Path.Length == 0 ? f.Name : $"{f.Name}\\{f.Path}"] = f.Id;
            }
            foreach (var f in await db.Files.ToListAsync())
            {
                Files[f.Name] = f.Id;
            }
        }

        public async Task DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Database.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        private void Write(string relative, string content)
        {
            var path = Path.Join(_root, Host, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private void WriteFiles()
        {
            Write(@"HR\Payroll\payroll.csv", "Name,SSN\nAvery Okafor,512-48-3307\n");
            Write(@"HR\Benefits\fmla.csv", "Name,SSN\nRiley Tanaka,512-48-3308\n");
            Write(@"HR\Employees\handbook.txt", "Welcome to the team.");
            Write(@"Finance\cards.txt", "Customer paid by Visa 4111 1111 1111 1111 exp 04/28");
            Write(@"Finance\AP\vendors.txt", "Vendor list.");
            Write(@"Public\menu.txt", "Soup and salad.");
            Write(@"Ops\Projects\A\notes.txt", "Project A.");
            Write(@"Ops\Projects\B\notes.txt", "Project B.");
            Write(@"Ops\IT\staff.csv", "Name,SSN\nQuinn Novak,512-48-3309\n");
        }

        private void WritePermissions()
        {
            object Ace(string sid, string rights = "Read", bool deny = false, string applies = "all") => new { sid, rights, deny, applies };
            var admins = new[] { Ace(WellKnownSid.LocalSystem, "Full"), Ace(WellKnownSid.Administrators, "Full") };
            var security = new
            {
                localGroups = new[] { new { sid = WellKnownSid.Users, name = @"BUILTIN\Users", members = new[] { new { sid = Sid(513), kind = "Group" } } } },
                shares = new Dictionary<string, object?>
                {
                    ["HR"] = new[] { Ace(WellKnownSid.AuthenticatedUsers, "Change") },
                    ["Finance"] = null,
                    ["Public"] = new[] { Ace(WellKnownSid.Everyone) },
                    ["Ops"] = new[] { Ace(WellKnownSid.Everyone, "Full") },
                },
                folders = new Dictionary<string, object>
                {
                    ["HR"] = new { owner = WellKnownSid.Administrators, aces = admins.Concat([Ace(HrStaff), Ace(HrManagers, "Modify")]) },
                    ["HR/Payroll"] = new { @protected = true, aces = admins.Concat([Ace(HrManagers, "Modify"), Ace(Orphan)]) },
                    ["HR/Employees"] = new { aces = new[] { Ace(Dave, deny: true) } },
                    ["HR/Benefits"] = new { aces = new[] { Ace(WellKnownSid.Everyone) } },
                    ["Finance"] = new { aces = admins.Concat([Ace(Fin, "Modify"), Ace(WellKnownSid.Users)]) },
                    ["Finance/AP"] = new { @protected = true, aces = admins.Concat([Ace(Fin, "Modify"), Ace(Partners)]) },
                    ["Public"] = new { aces = admins.Concat([Ace(WellKnownSid.Everyone)]) },
                    ["Ops"] = new { aces = admins.Concat([Ace(Sid(513))]) },
                },
                denied = new[] { "Ops/IT" },
            };
            File.WriteAllText(Path.Join(_root, Host + ".security.json"), JsonSerializer.Serialize(security));
        }

        private string WriteDirectory()
        {
            object P(string sid, string kind, string name, string[]? members = null, bool enabled = true, bool security = true) =>
                new { sid, kind, name, enabled, security, members = members ?? [] };
            var domain = new
            {
                domain = "access.test",
                domainSid = Domain,
                principals = new[]
                {
                    P(Sid(513), "Group", "Domain Users"),
                    P(Alice, "User", "alice"),
                    P(Bob, "User", "bob"),
                    P(Carol, "User", "carol", enabled: false),
                    P(Dave, "User", "dave"),
                    P(HrStaff, "Group", "HR_Staff", [Alice, Carol, Dave]),
                    P(HrManagers, "Group", "HR_Managers", [Alice]),
                    P(Fin, "Group", "Finance", [Bob]),
                    P(Partners, "Group", "Partners", [Bob, "S-1-5-21-9000-9000-9000-4001"]),
                    P(LoopA, "Group", "Loop_A", [LoopB, Alice]),
                    P(LoopB, "Group", "Loop_B", [LoopA]),
                    P(Sid(1300), "Group", "Newsletter", [Bob], security: false),
                },
            };
            var path = Path.Join(_root, "access.test.directory.json");
            File.WriteAllText(path, JsonSerializer.Serialize(domain));
            return path;
        }

        private async Task<Guid> QueueScanAsync()
        {
            await using var db = Database.CreateContext();
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = Host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            db.ManagedServers.Add(server);
            db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            return server.Id;
        }
    }

    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);
    private sealed record Trustee(string Sid, string Name, string Kind, bool Broad, bool Administrative, bool? Enabled);
    private sealed record AceView(int Index, string Effect, Trustee? Trustee, string Rights, string AppliesTo, bool Inherited, int? MembersCertain, int? MembersPossible);
    private sealed record PermissionSet(string State, Trustee? Owner, bool? Protected, List<AceView> Entries);
    private sealed record Note(string Code, string Text);
    private sealed record Range(string Rights, string UpTo, string Certainty, List<Note> Notes);
    private sealed record UserAccess(string Sid, string Name, bool Enabled, string Rights, string UpTo, string Certainty, string? Via);
    private sealed record Reason(string Source, int Index, string Effect, Trustee? Trustee, string Match, List<string> Chain, string Rights);
    private sealed record Explain(UserAccess User, string Target, List<Reason> Reasons, List<Note> Notes);
    private sealed record DirectoryRef(Guid? SnapshotId, string? Domain, string LocalGroups);
    private sealed record ShareAccess(string Name, PermissionSet Share, Guid? RootFolderId, Range? OpenAccess, Dictionary<string, long> FolderPermissionStates, long Boundaries);
    private sealed record Boundary(Guid FolderId, string Path, string State, bool? Protected, bool? Explicit, Range OpenAccess);
    private sealed record FolderAccess(string UncPath, PermissionSet Share, PermissionSet Folder, Range OpenFolder, Range OpenFiles, int UsersCertain, int UsersPossible, DirectoryRef Directory);
    private sealed record FileAccess(string UncPath, Range OpenAccess, int UsersCertain, int UsersPossible, List<UserAccess> Users);
    private sealed record ExposureItem(string UncPath, string Status, Range OpenAccess, int RestrictedFiles, int ConfidentialFiles);
    private sealed record Exposure(PageDto<ExposureItem> Folders, int FoldersChecked, int Open, int PossiblyOpen, int Unknown, long SensitiveFilesOpen, bool Truncated);
    private sealed record ReachItem(string UncPath, string Rights, string UpTo, string Certainty);
    private sealed record Reach(UserAccess User, PageDto<ReachItem> Places, long FoldersWithAccess, long FoldersCertain);
    private sealed record Snapshot(Guid Id, string State, string? Domain, int Users, int DisabledUsers, int Groups, int UnresolvedMembers, int Cycles);
    private sealed record DirectoryStatus(Snapshot? Latest, Snapshot? Pending, Snapshot? LastFailure);
    private sealed record Principal(string Sid, string Kind, string Name, bool Enabled);
    private sealed record PrincipalDetail(Principal Principal, bool MembersIncomplete, List<Trustee> DirectGroups, List<Trustee> MemberOf, List<Trustee> MaybeMemberOf, List<Trustee> Members, int? UsersCertain, int? UsersPossible, bool InCycle);

    private HttpClient Analyst => _lab.Factory.ClientAs(Roles.SecurityAnalyst);

    private Guid Folder(string path) => _lab.Folders[path];

    [Fact]
    public async Task The_scan_stored_permissions_and_the_sync_stored_the_directory()
    {
        var status = await Analyst.GetFromJsonAsync<DirectoryStatus>("/api/v1/directory");
        Assert.NotNull(status!.Latest);
        Assert.Equal("Completed", status.Latest.State);
        Assert.Equal("access.test", status.Latest.Domain);
        Assert.Equal(4, status.Latest.Users);
        Assert.Equal(1, status.Latest.DisabledUsers);
        Assert.Equal(1, status.Latest.UnresolvedMembers);
        Assert.Equal(1, status.Latest.Cycles);
        Assert.Null(status.Pending);

        await using var db = _lab.Database.CreateContext();
        var server = await db.ManagedServers.SingleAsync(s => s.Id == _lab.ServerId);
        Assert.Equal(LocalGroupsState.Read, server.LocalGroupsState);
        var finance = await db.Shares.SingleAsync(s => s.Id == _lab.Shares["Finance"]);
        Assert.Equal(PermissionState.AccessDenied, finance.SecurityState);
        var it = await db.Folders.SingleAsync(f => f.Id == _lab.Folders[@"Ops\IT"]);
        Assert.Equal(PermissionState.AccessDenied, it.SecurityState);
        // Most folders inherit identical permissions, so descriptors are shared rather than stored per folder.
        var ids = await db.Folders.Where(f => f.SecurityId != null).Select(f => f.SecurityId).ToListAsync();
        Assert.True(ids.Distinct().Count() < ids.Count);
        Assert.Equal(
            await db.Folders.Where(f => f.Id == _lab.Folders[@"Ops\Projects\A"]).Select(f => f.SecurityId).SingleAsync(),
            await db.Folders.Where(f => f.Id == _lab.Folders[@"Ops\Projects\B"]).Select(f => f.SecurityId).SingleAsync());
    }

    [Fact]
    public async Task Share_view_shows_share_permissions_and_where_folder_permissions_change()
    {
        var hr = await Analyst.GetFromJsonAsync<ShareAccess>($"/api/v1/access/shares/{_lab.Shares["HR"]}");
        Assert.Equal("Read", hr!.Share.State);
        var entry = Assert.Single(hr.Share.Entries);
        Assert.Equal("Authenticated Users", entry.Trustee!.Name);
        Assert.Equal("Change", entry.Rights);
        Assert.Equal(4, hr.Boundaries);
        Assert.Equal("No access", hr.OpenAccess!.Rights);

        var boundaries = await Analyst.GetFromJsonAsync<PageDto<Boundary>>($"/api/v1/access/shares/{_lab.Shares["HR"]}/boundaries");
        Assert.Equal(["", "Benefits", "Employees", "Payroll"], boundaries!.Items.Select(b => b.Path));
        Assert.True(boundaries.Items.Single(b => b.Path == "Payroll").Protected);
        Assert.Equal("Read", boundaries.Items.Single(b => b.Path == "Benefits").OpenAccess.Rights);

        var finance = await Analyst.GetFromJsonAsync<ShareAccess>($"/api/v1/access/shares/{_lab.Shares["Finance"]}");
        Assert.Equal("AccessDenied", finance!.Share.State);
        Assert.Empty(finance.Share.Entries);
        Assert.Equal("Partial", finance.OpenAccess!.Certainty);
        Assert.Equal("Read", finance.OpenAccess.UpTo);
    }

    [Fact]
    public async Task Folder_view_lists_entries_with_names_kinds_and_member_counts()
    {
        var payroll = await Analyst.GetFromJsonAsync<FolderAccess>($"/api/v1/access/folders/{Folder(@"HR\Payroll")}");
        Assert.Equal($@"\\{Host}\HR\Payroll", payroll!.UncPath);
        Assert.True(payroll.Folder.Protected);
        var managers = payroll.Folder.Entries.Single(e => e.Trustee?.Sid == HrManagers);
        Assert.Equal("Modify", managers.Rights);
        Assert.Equal(1, managers.MembersCertain);
        Assert.Equal("Deleted", payroll.Folder.Entries.Single(e => e.Trustee?.Sid == Orphan).Trustee!.Kind);
        Assert.True(payroll.Folder.Entries.Single(e => e.Trustee?.Sid == WellKnownSid.Administrators).Trustee!.Administrative);
        Assert.Equal("No access", payroll.OpenFiles.Rights);
        Assert.Equal("Likely", payroll.OpenFiles.Certainty);
        Assert.Equal(1, payroll.UsersCertain);
        Assert.Equal("Read", payroll.Directory.LocalGroups);
    }

    [Fact]
    public async Task Who_has_access_applies_nesting_share_limits_denies_and_disabled_accounts()
    {
        var root = await Analyst.GetFromJsonAsync<PageDto<UserAccess>>($"/api/v1/access/folders/{Folder("HR")}/users");
        var byName = root!.Items.ToDictionary(u => u.Name);
        Assert.Equal(["alice", "carol", "dave"], byName.Keys.Order());
        Assert.Equal("Modify", byName["alice"].Rights);
        Assert.Equal("Read", byName["carol"].Rights);
        Assert.False(byName["carol"].Enabled);
        Assert.Contains("HR_Managers", byName["alice"].Via, StringComparison.Ordinal);

        var employees = await Analyst.GetFromJsonAsync<PageDto<UserAccess>>($"/api/v1/access/folders/{Folder(@"HR\Employees")}/users");
        Assert.DoesNotContain(employees!.Items, u => u.Name == "dave");

        var explain = await Analyst.GetFromJsonAsync<Explain>($"/api/v1/access/folders/{Folder(@"HR\Employees")}/explain?sid={Dave}");
        Assert.Equal("No access", explain!.User.Rights);
        Assert.Contains(explain.Notes, n => n.Code == "DenyApplied");
        var deny = explain.Reasons.Single(r => r.Effect == "Deny");
        Assert.Equal("NTFS", deny.Source);
        Assert.Equal("Yes", deny.Match);

        var carol = await Analyst.GetFromJsonAsync<Explain>($"/api/v1/access/folders/{Folder("HR")}/explain?sid={Carol}&target=Folder");
        Assert.Contains(carol!.Notes, n => n.Code == "AccountDisabled");
        Assert.Equal("Folder", carol.Target);
        var staff = carol.Reasons.Single(r => r.Trustee?.Sid == HrStaff);
        Assert.Equal(["carol", "HR_Staff"], staff.Chain);
    }

    [Fact]
    public async Task Unread_share_permissions_and_unresolved_members_widen_the_range_instead_of_guessing()
    {
        var bob = await Analyst.GetFromJsonAsync<Explain>($"/api/v1/access/folders/{Folder(@"Finance\AP")}/explain?sid={Bob}");
        Assert.Equal("No access", bob!.User.Rights);
        Assert.Equal("Modify", bob.User.UpTo);
        Assert.Equal("Partial", bob.User.Certainty);
        Assert.Contains(bob.Notes, n => n.Code == "ShareNotRead");

        var it = await Analyst.GetFromJsonAsync<FolderAccess>($"/api/v1/access/folders/{Folder(@"Ops\IT")}");
        Assert.Equal("AccessDenied", it!.Folder.State);
        Assert.Equal("Unknown", it.OpenFiles.Certainty);
        Assert.Equal("Full control", it.OpenFiles.UpTo);
    }

    [Fact]
    public async Task Exposure_finds_sensitive_files_open_to_any_domain_user_and_reports_uncertainty()
    {
        var exposure = await Analyst.GetFromJsonAsync<Exposure>("/api/v1/access/exposure");
        var items = exposure!.Folders.Items.ToDictionary(i => i.UncPath);
        Assert.Equal("Open", items[$@"\\{Host}\HR\Benefits"].Status);
        Assert.Equal(1, items[$@"\\{Host}\HR\Benefits"].RestrictedFiles);
        Assert.Equal("PossiblyOpen", items[$@"\\{Host}\Finance"].Status);
        Assert.Equal("Unknown", items[$@"\\{Host}\Ops\IT"].Status);
        Assert.False(items.ContainsKey($@"\\{Host}\HR\Payroll"));
        Assert.Equal($@"\\{Host}\HR\Benefits", exposure.Folders.Items[0].UncPath);
        Assert.Equal(1, exposure.Open);
        Assert.Equal(1, exposure.SensitiveFilesOpen);
        Assert.False(exposure.Truncated);

        var open = await Analyst.GetFromJsonAsync<Exposure>("/api/v1/access/exposure?status=Open");
        Assert.Single(open!.Folders.Items);

        var any = await Analyst.GetFromJsonAsync<Explain>($"/api/v1/access/folders/{Folder(@"HR\Benefits")}/explain?sid=any");
        Assert.Equal("Read", any!.User.Rights);
        Assert.Contains(any.Reasons, r => r.Trustee?.Sid == WellKnownSid.Everyone && r.Effect == "Allow");
    }

    [Fact]
    public async Task File_access_answers_who_can_open_a_file()
    {
        var fmla = await Analyst.GetFromJsonAsync<FileAccess>($"/api/v1/files/{_lab.Files["fmla.csv"]}/access");
        Assert.Equal($@"\\{Host}\HR\Benefits\fmla.csv", fmla!.UncPath);
        Assert.Equal("Read", fmla.OpenAccess.Rights);
        Assert.Contains(fmla.Users, u => u.Name == "bob" && u.Rights == "Read");

        var payroll = await Analyst.GetFromJsonAsync<FileAccess>($"/api/v1/files/{_lab.Files["payroll.csv"]}/access");
        Assert.Equal("alice", Assert.Single(payroll!.Users).Name);
        Assert.Contains(payroll.OpenAccess.Notes, n => n.Code == "FilesMayDiffer");
    }

    [Fact]
    public async Task Reach_lists_where_a_user_can_open_files()
    {
        var reach = await Analyst.GetFromJsonAsync<Reach>($"/api/v1/access/principals/{Alice}/reach");
        var places = reach!.Places.Items.ToDictionary(p => p.UncPath);
        Assert.Equal("Modify", places[$@"\\{Host}\HR\Payroll"].Rights);
        Assert.Equal("Read", places[$@"\\{Host}\Public"].Rights);
        Assert.Equal("Partial", places[$@"\\{Host}\Finance"].Certainty);
        Assert.False(places.ContainsKey($@"\\{Host}\Ops\IT"));
        Assert.True(reach.FoldersWithAccess >= reach.FoldersCertain);

        var any = await Analyst.GetFromJsonAsync<Reach>("/api/v1/access/principals/any/reach");
        Assert.Equal("Any domain user", any!.User.Name);
        Assert.DoesNotContain(any.Places.Items, p => p.UncPath.EndsWith("Payroll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Directory_browsing_shows_nesting_cycles_and_uncertain_groups()
    {
        var search = await Analyst.GetFromJsonAsync<PageDto<Principal>>("/api/v1/directory/principals?search=ALI");
        Assert.Equal("alice", Assert.Single(search!.Items).Name);
        var users = await Analyst.GetFromJsonAsync<PageDto<Principal>>("/api/v1/directory/principals?kind=user&enabled=false");
        Assert.Equal("carol", Assert.Single(users!.Items).Name);

        var alice = await Analyst.GetFromJsonAsync<PrincipalDetail>($"/api/v1/directory/principals/{Alice}");
        Assert.Equal(["Domain Users", "HR_Managers", "HR_Staff", "Loop_A", "Loop_B"], alice!.MemberOf.Select(g => g.Name));
        Assert.False(alice.InCycle);

        var bob = await Analyst.GetFromJsonAsync<PrincipalDetail>($"/api/v1/directory/principals/{Bob}");
        Assert.DoesNotContain(bob!.MemberOf, g => g.Name == "Newsletter");

        var loop = await Analyst.GetFromJsonAsync<PrincipalDetail>($"/api/v1/directory/principals/{LoopA}");
        Assert.True(loop!.InCycle);
        Assert.Equal(1, loop.UsersCertain);

        var partners = await Analyst.GetFromJsonAsync<PrincipalDetail>($"/api/v1/directory/principals/{Partners}");
        Assert.True(partners!.MembersIncomplete);
        Assert.Equal(1, partners.UsersCertain);
    }

    [Theory]
    [InlineData("/api/v1/access/folders/{folder}/explain?sid=not-a-sid")]
    [InlineData("/api/v1/access/folders/{folder}/explain")]
    [InlineData("/api/v1/access/folders/{folder}/users?target=Share")]
    [InlineData("/api/v1/access/folders/{folder}/users?minRights=Everything")]
    [InlineData("/api/v1/access/exposure?minSensitivity=Public")]
    [InlineData("/api/v1/access/exposure?status=Closed")]
    [InlineData("/api/v1/directory/principals?kind=Printer")]
    [InlineData("/api/v1/directory/principals/not-a-sid")]
    [InlineData("/api/v1/access/principals/S-1-5-21-10-20-30-1201/reach")]
    [InlineData("/api/v1/access/folders/{folder}/explain?sid=S-1-5-21-10-20-30-1201")]
    public async Task Bad_requests_are_rejected(string url)
    {
        var response = await Analyst.GetAsync(url.Replace("{folder}", Folder("HR").ToString(), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/access/folders/{missing}")]
    [InlineData("/api/v1/access/folders/{missing}/users")]
    [InlineData("/api/v1/access/shares/{missing}")]
    [InlineData("/api/v1/files/{missing}/access")]
    [InlineData("/api/v1/directory/principals/S-1-5-21-10-20-30-4242")]
    public async Task Unknown_ids_are_not_found(string url)
    {
        var response = await Analyst.GetAsync(url.Replace("{missing}", Guid.NewGuid().ToString(), StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/directory")]
    [InlineData("/api/v1/directory/principals")]
    [InlineData("/api/v1/access/exposure")]
    [InlineData("/api/v1/access/principals/any/reach")]
    public async Task Access_views_need_a_signed_in_user_with_a_role(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _lab.Factory.ClientAs(null).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.DataOwner).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _lab.Factory.ClientAs(Roles.Viewer).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Only_platform_admins_can_request_a_sync_and_the_request_is_audited()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.SecurityAnalyst).PostAsync("/api/v1/directory/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false).PostAsync("/api/v1/directory/sync", null)).StatusCode);

        var admin = _lab.Factory.ClientAs(Roles.PlatformAdmin, sid: "S-1-5-21-1-2-3-500", name: @"NW\admin");
        var first = await admin.PostAsync("/api/v1/directory/sync", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var queued = await first.Content.ReadFromJsonAsync<Snapshot>();
        Assert.Equal("Queued", queued!.State);
        // Asking again while one is waiting returns the same request rather than queuing another.
        var second = await (await admin.PostAsync("/api/v1/directory/sync", null)).Content.ReadFromJsonAsync<Snapshot>();
        Assert.Equal(queued.Id, second!.Id);

        await using var db = _lab.Database.CreateContext();
        var entry = Assert.Single(await db.AppAuditLog.Where(a => a.Action == "directory.sync.request").ToListAsync());
        Assert.Equal("S-1-5-21-1-2-3-500", entry.ActorSid);
        var status = await Analyst.GetFromJsonAsync<DirectoryStatus>("/api/v1/directory");
        Assert.Equal(queued.Id, status!.Pending!.Id);
        Assert.NotNull(status.Latest);

        await db.DirectorySnapshots.Where(s => s.Id == queued.Id).ExecuteDeleteAsync();
    }
}
