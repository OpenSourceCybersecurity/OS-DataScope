using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.ActiveDirectory;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Core.Settings;
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
using Npgsql;

namespace DataScope.Api.Tests;

/// <summary>
/// Owners and access reviews end to end on a synthetic file server and domain: the real scan and
/// directory sync, owners assigned through the API, campaigns frozen, decided and submitted by the owner, and the
/// evidence exported. Every account, SID, address and file here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class GovernanceApiTests : IClassFixture<GovernanceApiTests.Lab>
{
    private const string Host = "gov-fs01";
    private const string Domain = "S-1-5-21-40-50-60";
    private static readonly string Maria = Sid(1101), Nate = Sid(1102), Olivia = Sid(1103), Paul = Sid(1104), Quinn = Sid(1105);
    private static readonly string Tom = Sid(1106), Rita = Sid(1107), Uma = Sid(1108), Admin = Sid(1110), Vic = Sid(1111);
    private static readonly string HrStaff = Sid(1201), FinanceGroup = Sid(1202), ProjectX = Sid(1203), ProjectAdmins = Sid(1204), LoopA = Sid(1205), LoopB = Sid(1206);

    private readonly Lab _lab;

    public GovernanceApiTests(Lab lab) => _lab = lab;

    private static string Sid(int rid) => $"{Domain}-{rid}";

    public sealed class Lab : IAsyncLifetime
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ds-gov-").FullName;
        private ServiceProvider _services = null!;
        private IConfiguration _config = null!;

        public TestDatabase Database { get; } = new();
        public ApiFactory Factory { get; private set; } = null!;
        public Guid ServerId { get; private set; }
        public Dictionary<string, Guid> Shares { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> Roots { get; } = new(StringComparer.Ordinal);
        public string DirectoryFile => Path.Join(_root, "gov.test.directory.json");
        public string SecurityFile => Path.Join(_root, Host + ".security.json");
        public IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

        /// <summary>Who has Read directly on the HR share's root folder, besides the groups; changed by tests.</summary>
        public bool PaulOnHr { get; set; } = true;

        /// <summary>Extra members of the Finance group; changed by tests.</summary>
        public List<string> ExtraFinanceMembers { get; } = [];

        public async Task InitializeAsync()
        {
            await Database.InitializeAsync();
            Factory = new ApiFactory(Database);
            Write(@"HR\handbook.txt", "Welcome to the team.");
            Write(@"HR\Payroll\pay.csv", "Name,SSN\nAvery Okafor,512-48-3301\n");
            Write(@"Finance\ledger.txt", "Ledger.");
            Write(@"Public\menu.txt", "Soup.");
            Write(@"Ops\notes.txt", "Ops.");
            WritePermissions();
            WriteDirectory();
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataScope"] = Database.ConnectionString,
                ["Scanner:LeaseSeconds"] = "60",
            }).Build();
            var services = new ServiceCollection();
            services.AddDataScopePersistence(_config);
            _services = services.BuildServiceProvider();

            await using (var db = Database.CreateContext())
            {
                var now = DateTimeOffset.UtcNow;
                var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = Host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
                db.ManagedServers.Add(server);
                await db.SaveChangesAsync();
                ServerId = server.Id;
            }
            await RescanAsync();
            await SyncAsync();

            await using (var db = Database.CreateContext())
            {
                foreach (var s in await db.Shares.Where(s => s.ServerId == ServerId).ToListAsync())
                {
                    Shares[s.Name] = s.Id;
                    Roots[s.Name] = await db.Folders.Where(f => f.ShareId == s.Id && f.Path == "").Select(f => f.Id).SingleAsync();
                }
            }
            var admin = Factory.ClientAs(Roles.PlatformAdmin, Admin, @"GOV\admin");
            await Assign(admin, "HR", new { ownerSid = Maria, backupSid = Nate, reviewFrequency = "Quarterly", purpose = "HR records" });
            await Assign(admin, "Finance", new { ownerSid = Olivia, reviewFrequency = "Annual" });
            await Assign(admin, "Ops", new { ownerSid = Nate, reviewFrequency = "Quarterly" });
        }

        private async Task Assign(HttpClient admin, string share, object body)
        {
            var response = await admin.PutAsJsonAsync($"/api/v1/folders/{Roots[share]}/owner", body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            await Factory.DisposeAsync();
            await Database.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        /// <summary>Queues a scan of the lab server and runs it, reading the permissions file afresh.</summary>
        public async Task RescanAsync()
        {
            await using (var db = Database.CreateContext())
            {
                var now = DateTimeOffset.UtcNow;
                db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = ServerId, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync();
            }
            var executor = new InventoryScanExecutor(Scopes, new LabShareEnumerator(_root), new FileSystemDirectorySource(),
                new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero, 100), NullLogger<InventoryScanExecutor>.Instance,
                new FileContentReader(new ContentLimits()), new LabSecurityReader(_root));
            Assert.True(await new ScanWorker(Scopes, executor, _config, NullLogger<ScanWorker>.Instance).RunOneAsync(CancellationToken.None));
        }

        /// <summary>Queues and runs a directory sync from the lab directory file.</summary>
        public async Task SyncAsync()
        {
            await using (var scope = Scopes.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DirectoryStore>().QueueAsync("S-1", CancellationToken.None);
            }
            var sync = new DirectorySyncWorker(Scopes, _config, NullLogger<DirectorySyncWorker>.Instance, new LabDirectoryReader(DirectoryFile));
            Assert.True(await sync.RunOneAsync(CancellationToken.None));
        }

        public void Write(string relative, string content, DateTime? lastWriteUtc = null)
        {
            var path = Path.Join(_root, Host, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (lastWriteUtc is { } at)
            {
                File.SetLastWriteTimeUtc(path, at);
            }
        }

        /// <summary>Deletes a lab folder and everything in it, so the next scan marks it removed.</summary>
        public void RemoveFolder(string relative) =>
            Directory.Delete(Path.Join(_root, Host, relative.Replace('\\', Path.DirectorySeparatorChar)), recursive: true);

        public void WritePermissions()
        {
            object Ace(string sid, string rights = "Read") => new { sid, rights, deny = false, applies = "all" };
            var admins = new[] { Ace(WellKnownSid.LocalSystem, "Full"), Ace(WellKnownSid.Administrators, "Full") };
            var hr = admins.Concat([Ace(HrStaff, "Modify"), Ace(Quinn, "Full")]);
            if (PaulOnHr)
            {
                hr = hr.Concat([Ace(Paul)]);
            }
            var security = new
            {
                localGroups = Array.Empty<object>(),
                shares = new Dictionary<string, object?>
                {
                    ["HR"] = new[] { Ace(WellKnownSid.Everyone, "Full") },
                    ["Finance"] = new[] { Ace(WellKnownSid.Everyone, "Full") },
                    ["Public"] = new[] { Ace(WellKnownSid.Everyone, "Full") },
                    ["Ops"] = new[] { Ace(WellKnownSid.Everyone, "Full") },
                },
                folders = new Dictionary<string, object>
                {
                    ["HR"] = new { owner = WellKnownSid.Administrators, aces = hr },
                    ["Finance"] = new { aces = admins.Concat([Ace(FinanceGroup, "Modify")]) },
                    ["Public"] = new { aces = admins.Concat([Ace(WellKnownSid.Everyone)]) },
                    ["Ops"] = new { aces = admins },
                },
                denied = new[] { "Ops" },
            };
            File.WriteAllText(SecurityFile, JsonSerializer.Serialize(security));
        }

        /// <param name="loop">Also two groups in OU=Groups that contain each other, Loop_A (with Paul) and Loop_B, for the nesting-loop risk (D179).</param>
        public void WriteDirectory(bool loop = false)
        {
            object U(string sid, string name, string? mail = null, bool enabled = true, string? department = null, string? manager = null) =>
                new { sid, kind = "User", name, displayName = char.ToUpperInvariant(name[0]) + name[1..] + " Example", enabled, security = true, mail, department, manager, members = Array.Empty<string>() };
            object G(string sid, string name, string[] members, string? ou = null, int adminCount = 0) =>
                new { sid, kind = "Group", name, enabled = true, security = true, members, ou, adminCount };
            var domain = new
            {
                domain = "gov.test",
                domainSid = Domain,
                principals = new[]
                {
                    G(Sid(513), "Domain Users", []),
                    U(Maria, "maria", "maria@gov.test", department: "Human Resources"),
                    U(Nate, "nate", "nate@gov.test"),
                    U(Olivia, "olivia", "olivia@gov.test", department: "Finance"),
                    U(Paul, "paul", manager: Nate),
                    U(Quinn, "quinn", manager: Nate),
                    U(Tom, "tom", enabled: false),
                    U(Rita, "rita", "rita@gov.test"),
                    U(Uma, "uma"),
                    U(Admin, "admin"),
                    U(Vic, "vic"),
                    G(HrStaff, "HR_Staff", [Paul, Quinn, Tom]),
                    G(FinanceGroup, "Finance_Team", [Olivia, .. ExtraFinanceMembers]),
                    // Groups in an OU of their own, as in a real domain.
                    G(ProjectX, "Project_X", [Paul, Vic], ou: "OU=Groups"),
                    G(ProjectAdmins, "Project_Admins", [Vic], ou: "OU=Groups", adminCount: 1),
                }.Concat(loop ? new[] { G(LoopA, "Loop_A", [LoopB, Paul], ou: "OU=Groups"), G(LoopB, "Loop_B", [LoopA], ou: "OU=Groups") } : []).ToArray(),
            };
            File.WriteAllText(DirectoryFile, JsonSerializer.Serialize(domain));
        }
    }

    private sealed record PageDto<T>(List<T> Items, int Limit, int Offset, int Total);
    private sealed record OwnerView(long Id, Guid FolderId, string Resource, string OwnerSid, string OwnerName, string? OwnerEmail, string OwnerStatus, string? BackupSid, string ReviewFrequency, DateOnly? NextReviewDue, string? EndReason);
    private sealed record OwnerSummary(int Owned, int Unowned, int OrphanedOwners, int MissingBackup, int ReviewsDue);
    private sealed record Unowned(Guid FolderId, string ShareName, string Resource);
    private sealed record FolderOwner(OwnerView? Current, List<OwnerView> History, List<Suggestion> Suggestions);
    private sealed record Suggestion(string Sid, string Name, string Reason, string Basis, string? Title, string? Department);
    private sealed record TrusteeRef(string Sid, string Name);
    private sealed record ShareOwnerView(Guid Id, string Name, string? OwnerName);
    private sealed record PrincipalDetail(string DistinguishedName, TrusteeRef? Manager);
    private sealed record Skipped(Guid FolderId, string Resource, string Reason);
    private sealed record Campaign(Guid Id, string Name, string State, int Reviews, int Submitted, int DecisionsRequired, int Decided, List<Skipped> Skipped);
    private sealed record CampaignDetail(Campaign Campaign, List<ReviewSummary> Reviews);
    private sealed record ReviewSummary(Guid Id, Guid CampaignId, string Resource, string OwnerSid, string State, int ItemCount, int DecisionsRequired, int Decided, int Remove, string? DelegateName, string? MyRole, DateTimeOffset? EscalatedAt);
    private sealed record DecisionView([property: System.Text.Json.Serialization.JsonPropertyName("decision")] string Kind, string? Justification, DateOnly? ExceptionExpiresOn, string DecidedByName, string DecidedAs);
    private sealed record Item(long Id, int Ordinal, string TrusteeSid, string TrusteeName, string Effect, string Rights, bool RequiresDecision, int UsersCertain, int UsersPossible, int UsersDisabled, int UsersWithOtherAccess, List<string> Flags, DecisionView? Decision, int Revisions);
    private sealed record Event(string Kind, string ActorName, string Detail);
    private sealed record ReviewDetail(ReviewSummary Review, List<string> Notes, string EvidenceHash, bool CanDecide, bool CanDelegate, List<Item> Items, List<Event> Events, int HiddenItems);
    private sealed record Exemption(long Id, string Sid, string Name, string Kind, string Reason, string AddedByName);
    private sealed record ItemUser(string Sid, string Name, bool Enabled, string? Department, bool Certain, List<string> Chain, string? AlsoVia);
    private sealed record Verification(int Reviews, int Verified, List<JsonElement> Mismatched);
    private sealed record Me(string Sid, List<string> Roles, int OpenReviews, int Owns);
    private sealed record Owned(Guid FolderId, string Resource, string Role);

    private HttpClient AdminClient => _lab.Factory.ClientAs(Roles.PlatformAdmin, Admin, @"GOV\admin");
    private HttpClient AnalystClient => _lab.Factory.ClientAs(Roles.SecurityAnalyst, Rita, @"GOV\rita");

    /// <summary>A person with no Data Scope role: owners need none.</summary>
    private HttpClient Person(string sid, string name) => _lab.Factory.ClientAs("", sid, $@"GOV\{name}");

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Campaign> CreateCampaignAsync(string name, params string[] shares) =>
        await Ok<Campaign>(await AdminClient.PostAsJsonAsync("/api/v1/campaigns", new
        {
            name,
            dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            shareIds = shares.Select(s => _lab.Shares[s]).ToList(),
        }));

    private async Task<ReviewSummary> ReviewInAsync(Guid campaignId, string share) =>
        (await Ok<CampaignDetail>(await AdminClient.GetAsync($"/api/v1/campaigns/{campaignId}"))).Reviews.Single(r => r.Resource == $@"\\{Host}\{share}");

    private static object Decide(Item item, string decision, string? why = null, DateOnly? until = null) =>
        new { itemId = item.Id, decision, justification = why, exceptionExpiresOn = until };

    [Fact]
    public async Task Owners_are_enabled_people_from_the_directory_and_only_administrators_assign_them()
    {
        var admin = AdminClient;
        var hr = _lab.Roots["HR"];
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = HrStaff, reviewFrequency = "Quarterly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = Tom, reviewFrequency = "Quarterly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = Maria, backupSid = Maria, reviewFrequency = "Quarterly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = Maria, reviewFrequency = "Weekly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = Maria, ownerEmail = "a@b.test, c@d.test", reviewFrequency = "Quarterly" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AnalystClient.PutAsJsonAsync($"/api/v1/folders/{hr}/owner", new { ownerSid = Rita, reviewFrequency = "Quarterly" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Person(Maria, "maria").GetAsync("/api/v1/owners")).StatusCode);

        var owners = await Ok<PageDto<OwnerView>>(await AnalystClient.GetAsync("/api/v1/owners"));
        var hrOwner = owners.Items.Single(o => o.FolderId == hr);
        Assert.Equal(Maria, hrOwner.OwnerSid);
        Assert.Equal("maria@gov.test", hrOwner.OwnerEmail); // From Active Directory.
        Assert.Equal("Active", hrOwner.OwnerStatus);
        Assert.Equal(Nate, hrOwner.BackupSid);

        // The share list names each share's owner, and nobody for a share without one.
        var shareList = await Ok<PageDto<ShareOwnerView>>(await AnalystClient.GetAsync("/api/v1/shares"));
        Assert.Equal(hrOwner.OwnerName, shareList.Items.Single(x => x.Name == "HR").OwnerName);
        Assert.Null(shareList.Items.Single(x => x.Name == "Public").OwnerName);

        var unowned = await Ok<PageDto<Unowned>>(await AnalystClient.GetAsync("/api/v1/owners/unowned"));
        Assert.Equal(["Public"], unowned.Items.Select(u => u.ShareName));
        var summary = await Ok<OwnerSummary>(await AnalystClient.GetAsync("/api/v1/owners/summary"));
        Assert.Equal(1, summary.Unowned);
        Assert.True(summary.Owned >= 3);

        // Nate manages everyone enabled in HR_Staff, which has Modify, so he comes first; Quinn has Full control
        // directly. An administrator still decides.
        var folder = await Ok<FolderOwner>(await AnalystClient.GetAsync($"/api/v1/folders/{hr}/owner"));
        Assert.Equal(Nate, folder.Suggestions[0].Sid);
        Assert.Equal("Manager", folder.Suggestions[0].Basis);
        Assert.Equal("Nate Example", folder.Suggestions[0].Name);
        Assert.Contains("HR_Staff", folder.Suggestions[0].Reason, StringComparison.Ordinal);
        Assert.Contains(folder.Suggestions, s => s.Sid == Quinn && s.Basis == "DirectRights");
        var paul = await Ok<PrincipalDetail>(await AnalystClient.GetAsync($"/api/v1/directory/principals/{Paul}"));
        Assert.Equal(Nate, paul.Manager?.Sid);
        // People without a Data Scope role see neither reporting lines nor suggestions.
        Assert.Equal(HttpStatusCode.Forbidden, (await Person(Paul, "paul").GetAsync($"/api/v1/directory/principals/{Paul}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Person(Paul, "paul").GetAsync($"/api/v1/folders/{hr}/owner")).StatusCode);
        Assert.Equal(Maria, folder.Current!.OwnerSid);

        // Ending ownership needs a reason and keeps history; reassigning brings it back.
        var pub = _lab.Roots["Public"];
        await Ok<OwnerView>(await admin.PutAsJsonAsync($"/api/v1/folders/{pub}/owner", new { ownerSid = Vic, reviewFrequency = "Annual" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/folders/{pub}/owner/end", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/folders/{pub}/owner/end", new { reason = "Share is being retired." })).StatusCode);
        var ended = await Ok<FolderOwner>(await AnalystClient.GetAsync($"/api/v1/folders/{pub}/owner"));
        Assert.Null(ended.Current);
        Assert.Equal("Share is being retired.", Assert.Single(ended.History).EndReason);
    }

    [Fact]
    public async Task A_manager_completes_a_review_end_to_end_without_anything_changing_in_the_directory()
    {
        var directoryBefore = await File.ReadAllBytesAsync(_lab.DirectoryFile);
        var campaign = await CreateCampaignAsync("Q4 HR review", "HR", "Ops");
        // Ops' permissions could not be read, so it is skipped with a reason instead of reviewed on guesswork.
        Assert.Equal(1, campaign.Reviews);
        Assert.Contains(campaign.Skipped, s => s.Resource == $@"\\{Host}\Ops" && s.Reason.Contains("may not read", StringComparison.Ordinal));

        var maria = Person(Maria, "maria");
        var me = await Ok<Me>(await maria.GetAsync("/api/v1/me"));
        Assert.Empty(me.Roles);
        Assert.True(me.OpenReviews >= 1);
        var mine = await Ok<List<ReviewSummary>>(await maria.GetAsync("/api/v1/my/reviews"));
        var summary = mine.Single(r => r.CampaignId == campaign.Id);
        Assert.Equal("Owner", summary.MyRole);

        // Another manager, and a person with a reader role, cannot see or touch it.
        var olivia = Person(Olivia, "olivia");
        Assert.DoesNotContain(await Ok<List<ReviewSummary>>(await olivia.GetAsync("/api/v1/my/reviews")), r => r.Id == summary.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await olivia.GetAsync($"/api/v1/my/reviews/{summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _lab.Factory.ClientAs(Roles.Viewer, Vic, @"GOV\vic").GetAsync($"/api/v1/my/reviews/{summary.Id}")).StatusCode);

        var review = await Ok<ReviewDetail>(await maria.GetAsync($"/api/v1/my/reviews/{summary.Id}"));
        Assert.True(review.CanDecide);
        Assert.True(review.CanDelegate);
        Assert.Contains(review.Notes, n => n.Contains("not a record of anyone opening a file", StringComparison.Ordinal));
        var staff = review.Items.Single(i => i.TrusteeSid == HrStaff);
        var paul = review.Items.Single(i => i.TrusteeSid == Paul);
        var quinn = review.Items.Single(i => i.TrusteeSid == Quinn);
        Assert.False(review.Items.Single(i => i.TrusteeSid == WellKnownSid.Administrators).RequiresDecision);
        Assert.Equal(3, review.Review.DecisionsRequired);
        Assert.Equal(1, staff.UsersDisabled);
        Assert.Contains("CanChangePermissions", quinn.Flags);
        Assert.Equal(1, paul.UsersWithOtherAccess); // Paul is also in HR_Staff.

        var olivUsers = await olivia.GetAsync($"/api/v1/my/reviews/{summary.Id}/items/{staff.Id}/users");
        Assert.Equal(HttpStatusCode.NotFound, olivUsers.StatusCode);
        var users = await Ok<List<ItemUser>>(await maria.GetAsync($"/api/v1/my/reviews/{summary.Id}/items/{staff.Id}/users"));
        Assert.Equal(["paul", "quinn", "tom"], users.Select(u => u.Name));
        Assert.Equal(["paul", "HR_Staff"], users[0].Chain);
        Assert.False(users.Single(u => u.Name == "tom").Enabled);

        // Not finished: submit is refused. Bad decisions are refused with a reason.
        Assert.Equal(HttpStatusCode.Conflict, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/submit", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions", new { decisions = new[] { Decide(paul, "Remove") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions",
            new { decisions = new[] { new { itemId = review.Items.Single(i => i.TrusteeSid == WellKnownSid.Administrators).Id, decision = "Keep" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await olivia.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions", new { decisions = new[] { Decide(staff, "Keep") } })).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await Ok<ReviewDetail>(await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions", new
        {
            decisions = new[]
            {
                Decide(staff, "Keep"),
                Decide(paul, "Keep"),
                Decide(quinn, "Exception", "Runs payroll month-end until the new system is live.", today.AddDays(90)),
            },
        }));
        // A changed mind is a new revision; the earlier decision is kept.
        var decided = await Ok<ReviewDetail>(await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions", new
        {
            decisions = new[] { Decide(paul, "Remove", "Moved to Finance in September; HR_Staff covers what remains.") },
        }));
        var paulNow = decided.Items.Single(i => i.Id == paul.Id);
        Assert.Equal("Remove", paulNow.Decision!.Kind);
        Assert.Equal("Owner", paulNow.Decision.DecidedAs);
        Assert.Equal(2, paulNow.Revisions);
        Assert.Equal("InProgress", decided.Review.State);

        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/submit", new { confirm = false })).StatusCode);
        var submitted = await Ok<ReviewDetail>(await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/submit", new { confirm = true }));
        Assert.Equal("Submitted", submitted.Review.State);
        Assert.False(submitted.CanDecide);
        Assert.Equal(HttpStatusCode.Conflict, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions", new { decisions = new[] { Decide(staff, "Keep") } })).StatusCode);

        // A Remove decision is evidence for IT to act on in Windows; Data Scope keeps no task queue and changes nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await AnalystClient.GetAsync("/api/v1/remediation")).StatusCode);

        // Evidence: the frozen review still verifies, and both exports carry the decisions.
        var auditor = _lab.Factory.ClientAs(Roles.Auditor, Sid(1300), @"GOV\auditor");
        var check = await Ok<Verification>(await auditor.GetAsync($"/api/v1/campaigns/{campaign.Id}/verify"));
        Assert.Equal(1, check.Verified);
        Assert.Empty(check.Mismatched);
        var csv = await auditor.GetStringAsync($"/api/v1/campaigns/{campaign.Id}/evidence.csv");
        var paulRow = csv.Split("\r\n").Single(l => l.Contains(Paul, StringComparison.Ordinal));
        Assert.Contains("Remove", paulRow, StringComparison.Ordinal);
        Assert.Contains(",yes,", paulRow, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(await auditor.GetStringAsync($"/api/v1/campaigns/{campaign.Id}/evidence.json"));
        var exported = json.RootElement.GetProperty("reviews")[0];
        Assert.True(exported.GetProperty("hashVerified").GetBoolean());
        Assert.Equal(4, exported.GetProperty("decisions").GetArrayLength()); // Every revision.
        Assert.False(exported.TryGetProperty("tasks", out _));

        await using var db = _lab.Database.CreateContext();
        var actions = await db.AppAuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("campaign.create", actions);
        Assert.Contains("review.submit", actions);
        Assert.Contains("campaign.export", actions);
        Assert.Equal(directoryBefore, await File.ReadAllBytesAsync(_lab.DirectoryFile));
    }

    [Fact]
    public async Task Accounts_on_the_no_decision_list_need_no_decision_and_owners_do_not_see_them()
    {
        var admin = AdminClient;
        // Only administrators change the list; readers of reviews can see it; people with no role cannot.
        Assert.Equal(HttpStatusCode.Forbidden, (await AnalystClient.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Quinn, reason = "IT admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Person(Maria, "maria").GetAsync("/api/v1/review-exemptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Factory.ClientAs(Roles.PlatformAdmin, Admin, @"GOV\admin", antiForgeryHeader: false)
            .PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Quinn, reason = "IT admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = "not-a-sid", reason = "IT admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Sid(9999), reason = "IT admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Quinn, reason = "" })).StatusCode);

        var added = await Ok<Exemption>(await admin.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Quinn.ToLowerInvariant(), reason = "IT administrator; access managed by IT" }));
        Assert.Equal(Quinn, added.Sid);
        Assert.Equal("User", added.Kind);
        Assert.Equal("Quinn Example (quinn)", added.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/review-exemptions", new { sid = Quinn, reason = "Again" })).StatusCode);
        Assert.Contains(await Ok<List<Exemption>>(await AnalystClient.GetAsync("/api/v1/review-exemptions")), x => x.Id == added.Id);

        int listedRequired;
        try
        {
            var campaign = await CreateCampaignAsync("No-decision list review", "HR");
            var summary = await ReviewInAsync(campaign.Id, "HR");
            listedRequired = summary.DecisionsRequired;

            // The owner does not see the entry, only that one was left out, and cannot list its people or decide on it.
            var maria = Person(Maria, "maria");
            var mine = await Ok<ReviewDetail>(await maria.GetAsync($"/api/v1/my/reviews/{summary.Id}"));
            Assert.DoesNotContain(mine.Items, i => i.TrusteeSid == Quinn);
            Assert.Equal(1, mine.HiddenItems);
            Assert.Contains(mine.Notes, n => n.Contains("administrators manage", StringComparison.Ordinal));

            // Administrators and auditors see it, marked, with no decision needed.
            var full = await Ok<ReviewDetail>(await admin.GetAsync($"/api/v1/reviews/{summary.Id}"));
            var quinn = full.Items.Single(i => i.TrusteeSid == Quinn);
            Assert.False(quinn.RequiresDecision);
            Assert.Contains(ReviewSnapshotBuilder.NoDecisionNeeded, quinn.Flags);
            Assert.Equal(0, full.HiddenItems);
            Assert.Empty(await Ok<List<ItemUser>>(await maria.GetAsync($"/api/v1/my/reviews/{summary.Id}/items/{quinn.Id}/users")));
            Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{summary.Id}/decisions",
                new { decisions = new[] { Decide(quinn, "Keep") } })).StatusCode);

            // The seal still verifies: the list is part of what was frozen.
            var verification = await Ok<Verification>(await admin.GetAsync($"/api/v1/campaigns/{campaign.Id}/verify"));
            Assert.Empty(verification.Mismatched);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/review-exemptions/{added.Id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/review-exemptions/{added.Id}")).StatusCode);
        Assert.DoesNotContain(await Ok<List<Exemption>>(await admin.GetAsync("/api/v1/review-exemptions")), x => x.Id == added.Id);

        // Campaigns after the change ask about the account again; the change history is in the audit log.
        var later = await ReviewInAsync((await CreateCampaignAsync("After the list changed", "HR")).Id, "HR");
        Assert.Equal(listedRequired + 1, later.DecisionsRequired);
        await using var db = _lab.Database.CreateContext();
        Assert.True(await db.AppAuditLog.AnyAsync(a => a.Action == "review.exemption.add" && a.Target == "Quinn Example (quinn)"));
        Assert.True(await db.AppAuditLog.AnyAsync(a => a.Action == "review.exemption.remove" && a.Target == "Quinn Example (quinn)"));
        Assert.NotNull((await db.ReviewExemptions.SingleAsync(x => x.Id == added.Id)).EndedAt);
    }

    [Fact]
    public async Task A_frozen_review_does_not_change_when_the_directory_changes()
    {
        var campaign = await CreateCampaignAsync("Finance freeze", "Finance");
        var review = await ReviewInAsync(campaign.Id, "Finance");
        var olivia = Person(Olivia, "olivia");
        var before = await Ok<ReviewDetail>(await olivia.GetAsync($"/api/v1/my/reviews/{review.Id}"));
        var team = before.Items.Single(i => i.TrusteeSid == FinanceGroup);
        Assert.Equal(1, team.UsersCertain);

        _lab.ExtraFinanceMembers.Add(Uma);
        _lab.WriteDirectory();
        await _lab.SyncAsync();

        var after = await Ok<ReviewDetail>(await olivia.GetAsync($"/api/v1/my/reviews/{review.Id}"));
        Assert.Equal(before.EvidenceHash, after.EvidenceHash);
        Assert.Equal(1, after.Items.Single(i => i.TrusteeSid == FinanceGroup).UsersCertain);
        var users = await Ok<List<ItemUser>>(await olivia.GetAsync($"/api/v1/my/reviews/{review.Id}/items/{team.Id}/users"));
        Assert.Equal(["olivia"], users.Select(u => u.Name));
        Assert.Empty((await Ok<Verification>(await AdminClient.GetAsync($"/api/v1/campaigns/{campaign.Id}/verify"))).Mismatched);

        // A new campaign sees the change.
        var next = await CreateCampaignAsync("Finance after change", "Finance");
        var nextReview = await Ok<ReviewDetail>(await olivia.GetAsync($"/api/v1/my/reviews/{(await ReviewInAsync(next.Id, "Finance")).Id}"));
        Assert.Equal(2, nextReview.Items.Single(i => i.TrusteeSid == FinanceGroup).UsersCertain);
    }

    [Fact]
    public async Task Frozen_rows_and_decisions_cannot_be_edited_in_the_database()
    {
        var campaign = await CreateCampaignAsync("Append-only check", "Finance");
        var review = await ReviewInAsync(campaign.Id, "Finance");
        var olivia = Person(Olivia, "olivia");
        var detail = await Ok<ReviewDetail>(await olivia.GetAsync($"/api/v1/my/reviews/{review.Id}"));
        await Ok<ReviewDetail>(await olivia.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/decisions",
            new { decisions = new[] { Decide(detail.Items.First(i => i.RequiresDecision), "Keep") } }));

        // The triggers hold even for the schema owner.
        await using var db = _lab.Database.CreateOwnerContext();
        foreach (var sql in new[]
        {
            $"UPDATE review_items SET trustee_name = 'x' WHERE review_id = '{review.Id}'",
            $"DELETE FROM review_decisions WHERE review_id = '{review.Id}'",
            $"UPDATE review_events SET detail = 'x' WHERE review_id = '{review.Id}'",
            "TRUNCATE review_item_users",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }

        // The services' login isn't even allowed to try on the history tables.
        await using var runtime = _lab.Database.CreateContext();
        foreach (var sql in new[] { $"UPDATE review_events SET detail = 'x' WHERE review_id = '{review.Id}'", "TRUNCATE review_item_users" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task Owners_can_hand_a_review_to_someone_and_overdue_reviews_escalate_to_the_backup()
    {
        var campaign = await CreateCampaignAsync("Delegation and escalation", "HR");
        var review = await ReviewInAsync(campaign.Id, "HR");
        var maria = Person(Maria, "maria");
        var nate = Person(Nate, "nate");
        var uma = Person(Uma, "uma");

        // The backup owner sees nothing until the review is escalated.
        Assert.Equal(HttpStatusCode.NotFound, (await nate.GetAsync($"/api/v1/my/reviews/{review.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/delegate", new { delegateSid = Maria })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/delegate", new { delegateSid = HrStaff })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/delegate", new { delegateSid = Tom })).StatusCode);

        var delegated = await Ok<ReviewDetail>(await maria.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/delegate", new { delegateSid = Uma }));
        Assert.Equal("Uma Example (uma)", delegated.Review.DelegateName);
        Assert.Contains(delegated.Events, e => e.Kind == "Delegated");
        var asDelegate = await Ok<ReviewDetail>(await uma.GetAsync($"/api/v1/my/reviews/{review.Id}"));
        Assert.True(asDelegate.CanDecide);
        Assert.False(asDelegate.CanDelegate);
        Assert.Equal(HttpStatusCode.Forbidden, (await uma.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/delegate", new { delegateSid = Vic })).StatusCode);
        var item = asDelegate.Items.Single(i => i.TrusteeSid == HrStaff);
        var decided = await Ok<ReviewDetail>(await uma.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/decisions", new { decisions = new[] { Decide(item, "Keep") } }));
        Assert.Equal("Delegate", decided.Items.Single(i => i.Id == item.Id).Decision!.DecidedAs);

        await using (var db = _lab.Database.CreateContext())
        {
            // Uma has no email address, so her notice is recorded as skipped rather than lost silently.
            var notice = await db.Outbox.SingleAsync(m => m.ReviewId == review.Id && m.RecipientSid == Uma);
            Assert.Equal(OutboxState.Skipped, notice.State);
        }

        // Five days after the due date, the reminder pass escalates to the backup owner.
        var late = new FixedClock(new DateTimeOffset(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(35).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));
        await using (var db = _lab.Database.CreateContext())
        {
            var reminders = new ReviewReminders(db, new NotificationStore(db, late), late);
            Assert.True(await reminders.RunAsync("https://datascope.gov.test/#my-reviews", CancellationToken.None) > 0);
            Assert.Equal(0, await new ReviewReminders(db, new NotificationStore(db, late), late).RunAsync("https://datascope.gov.test/#my-reviews", CancellationToken.None));
            var mail = await db.Outbox.Where(m => m.ReviewId == review.Id).ToListAsync();
            var escalation = mail.Single(m => m.Kind == NotificationKind.ReviewEscalated);
            Assert.Equal(Nate, escalation.RecipientSid);
            Assert.Equal("nate@gov.test", escalation.RecipientEmail);
            Assert.Contains("Maria Example (maria)", escalation.Body, StringComparison.Ordinal);
            Assert.Contains(mail, m => m.Kind == NotificationKind.ReviewOverdue && m.RecipientSid == Maria);
            Assert.Contains(mail, m => m.Kind == NotificationKind.ReviewOverdue && m.RecipientSid == Uma);
        }
        var escalated = await Ok<ReviewDetail>(await nate.GetAsync($"/api/v1/my/reviews/{review.Id}"));
        Assert.Equal("Backup", escalated.Review.MyRole);
        Assert.True(escalated.CanDelegate);
        Assert.Contains(escalated.Events, e => e.Kind == "Escalated");

        // An administrator can reassign too; closing the campaign stops further decisions.
        await Ok<ReviewDetail>(await AdminClient.PostAsJsonAsync($"/api/v1/reviews/{review.Id}/delegate", new { delegateSid = Vic }));
        Assert.Equal(HttpStatusCode.NotFound, (await uma.GetAsync($"/api/v1/my/reviews/{review.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AnalystClient.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/close", new { note = "" })).StatusCode);
        var closedCampaign = await Ok<Campaign>(await AdminClient.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/close", new { note = "Quarter ended." }));
        Assert.Equal("Closed", closedCampaign.State);
        Assert.Equal(HttpStatusCode.Conflict, (await maria.PostAsJsonAsync($"/api/v1/my/reviews/{review.Id}/decisions", new { decisions = new[] { Decide(item, "Keep") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AdminClient.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/close", new { note = "" })).StatusCode);
    }

    [Fact]
    public async Task The_notification_worker_sends_each_email_once_and_retries_failures()
    {
        await CreateCampaignAsync("Email check", "Finance");
        var options = new NotificationOptions { PublicUrl = "https://datascope.gov.test:8443/", Smtp = { Host = "smtp.gov.test", From = "datascope@gov.test" } };
        var sender = new RecordingSender();
        var worker = new NotificationWorker(_lab.Scopes, options, NullLogger<NotificationWorker>.Instance, TimeProvider.System, sender);
        await worker.RunOnceAsync(CancellationToken.None);
        var sent = sender.Sent.Where(m => m.Recipient == "olivia@gov.test" && m.Subject == $@"Access review: \\{Host}\Finance").ToList();
        Assert.NotEmpty(sent);
        Assert.Contains("https://datascope.gov.test:8443/#my-reviews", sent[0].Body, StringComparison.Ordinal);
        var count = sender.Sent.Count;
        await new NotificationWorker(_lab.Scopes, options, NullLogger<NotificationWorker>.Instance, TimeProvider.System, sender).RunOnceAsync(CancellationToken.None);
        Assert.Equal(count, sender.Sent.Count);

        await using var db = _lab.Database.CreateContext();
        var store = new NotificationStore(db, TimeProvider.System);
        Assert.True(await store.EnqueueAsync(new OutgoingNotice("test:failing", NotificationKind.ReviewAssigned, null, Rita, "rita", "rita@gov.test", "S", "B"), CancellationToken.None));
        Assert.False(await store.EnqueueAsync(new OutgoingNotice("test:failing", NotificationKind.ReviewAssigned, null, Rita, "rita", "rita@gov.test", "S", "B"), CancellationToken.None));
        await new NotificationWorker(_lab.Scopes, options, NullLogger<NotificationWorker>.Instance, TimeProvider.System, new FailingSender()).RunOnceAsync(CancellationToken.None);
        var failed = await db.Outbox.AsNoTracking().SingleAsync(m => m.IdempotencyKey == "test:failing");
        Assert.Equal(OutboxState.Pending, failed.State);
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("SMTP error", failed.LastError, StringComparison.Ordinal);
        Assert.True(failed.NextAttemptAt > DateTimeOffset.UtcNow);

        // Without email set up, messages are marked skipped; the reviews still wait in Data Scope.
        Assert.True(await store.EnqueueAsync(new OutgoingNotice("test:unconfigured", NotificationKind.ReviewAssigned, null, Rita, "rita", "rita@gov.test", "S", "B"), CancellationToken.None));
        await new NotificationWorker(_lab.Scopes, new NotificationOptions(), NullLogger<NotificationWorker>.Instance, TimeProvider.System).RunOnceAsync(CancellationToken.None);
        Assert.Equal(OutboxState.Skipped, (await db.Outbox.AsNoTracking().SingleAsync(m => m.IdempotencyKey == "test:unconfigured")).State);
    }

    [Fact]
    public async Task Campaigns_need_the_right_roles()
    {
        var anonymous = _lab.Factory.ClientAs(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/my/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/campaigns")).StatusCode);
        var viewer = _lab.Factory.ClientAs(Roles.Viewer, Vic, @"GOV\vic");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/campaigns")).StatusCode);
        var auditor = _lab.Factory.ClientAs(Roles.Auditor, Sid(1300), @"GOV\auditor");
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/campaigns", new { name = "Nope", dueOn = "2026-12-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AnalystClient.PostAsJsonAsync("/api/v1/campaigns", new { name = "Nope", dueOn = "2026-12-31" })).StatusCode);
        // Without the anti-forgery header a signed-in administrator still cannot create a campaign.
        var noHeader = _lab.Factory.ClientAs(Roles.PlatformAdmin, Admin, @"GOV\admin", antiForgeryHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await noHeader.PostAsJsonAsync("/api/v1/campaigns", new { name = "Nope", dueOn = "2026-12-31" })).StatusCode);

        var bad = await AdminClient.PostAsJsonAsync("/api/v1/campaigns", new { name = "Q", dueOn = DateOnly.FromDateTime(DateTime.UtcNow) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _) || errors.TryGetProperty("dueOn", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminClient.PostAsJsonAsync("/api/v1/campaigns", new
        {
            name = "Unowned only",
            dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10),
            shareIds = new[] { Guid.NewGuid() },
        })).StatusCode);
    }

    [Fact]
    public async Task Owners_see_what_they_own()
    {
        var owned = await Ok<List<Owned>>(await Person(Nate, "nate").GetAsync("/api/v1/my/owned"));
        Assert.Contains(owned, o => o.Resource == $@"\\{Host}\HR" && o.Role == "Backup");
        Assert.Contains(owned, o => o.Resource == $@"\\{Host}\Ops" && o.Role == "Owner");
        Assert.Empty(await Ok<List<Owned>>(await Person(Paul, "paul").GetAsync("/api/v1/my/owned")));
        Assert.Equal(0, (await Ok<Me>(await Person(Paul, "paul").GetAsync("/api/v1/me"))).Owns);
    }

    [Fact]
    public async Task Owners_can_look_up_people_to_hand_a_review_to_and_nobody_else_can()
    {
        var people = await Ok<List<JsonElement>>(await Person(Maria, "maria").GetAsync("/api/v1/my/people?search=ri"));
        var names = people.Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("Rita Example (rita)", names);
        Assert.Contains("Maria Example (maria)", names);
        Assert.DoesNotContain(names, n => n!.Contains("HR_Staff", StringComparison.Ordinal));
        Assert.Empty(await Ok<List<JsonElement>>(await Person(Maria, "maria").GetAsync("/api/v1/my/people?search=tom")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Person(Maria, "maria").GetAsync("/api/v1/my/people?search=a")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Person(Paul, "paul").GetAsync("/api/v1/my/people?search=ri")).StatusCode);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SentMail(string Recipient, string Subject, string Body);

    private sealed class RecordingSender : IEmailSender
    {
        public List<SentMail> Sent { get; } = [];

        public Task SendAsync(string recipient, string subject, string body, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(new SentMail(recipient, subject, body));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSender : IEmailSender
    {
        public Task SendAsync(string recipient, string subject, string body, CancellationToken ct) =>
            throw new System.Net.Mail.SmtpException(System.Net.Mail.SmtpStatusCode.MailboxBusy, "Try later");
    }
}
