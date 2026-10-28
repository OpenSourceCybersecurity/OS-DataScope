using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Activity;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;

namespace DataScope.Api.Tests;

/// <summary>
/// Stale and duplicate data (D119) over a synthetic file server: old and new files, one old file someone opened
/// recently, copies across two shares, and files that must never count. Every name and date here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class CleanupApiTests : IClassFixture<CleanupApiTests.Lab>, IDisposable
{
    private readonly Lab _lab;
    private readonly ApiFactory _factory;

    public CleanupApiTests(Lab lab)
    {
        _lab = lab;
        _factory = new ApiFactory(lab.Database);
    }

    public void Dispose() => _factory.Dispose();

    public sealed class Lab : IAsyncLifetime
    {
        public TestDatabase Database { get; } = new();
        public Guid ServerId { get; } = Guid.NewGuid();
        public Guid FinanceId { get; } = Guid.NewGuid();
        public Guid HrId { get; } = Guid.NewGuid();
        public Guid ArchiveId { get; } = Guid.NewGuid();
        public Guid OtherServerId { get; } = Guid.NewGuid();
        public Guid OtherShareId { get; } = Guid.NewGuid();

        public async Task InitializeAsync()
        {
            await Database.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset Years(double y) => now.AddDays(-365.25 * y);
            await using var db = Database.CreateContext();
            db.ManagedServers.Add(new ManagedServer { Id = ServerId, Hostname = "fs-clean.lab.test", State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" });
            db.ManagedServers.Add(new ManagedServer { Id = OtherServerId, Hostname = "fs-other.lab.test", State = EnrollmentState.Enrolled, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" });
            Share Share(Guid id, Guid server, string name) => new()
            {
                Id = id,
                ServerId = server,
                Name = name,
                NameKey = name.ToUpperInvariant(),
                UncPath = $@"\\{(server == ServerId ? "fs-clean" : "fs-other")}\{name}",
                Kind = ShareKind.Disk,
                Included = true,
                State = ShareState.Scanned,
                LastScannedAt = now.AddHours(-1),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Shares.AddRange(Share(FinanceId, ServerId, "Finance"), Share(HrId, ServerId, "HR"), Share(OtherShareId, OtherServerId, "Finance"));
            Folder Folder(Guid id, Guid share, string path) => new()
            {
                Id = id,
                ShareId = share,
                Path = path,
                PathHash = InventoryStore.PathHash(path),
                Depth = path.Length == 0 ? 0 : path.Split('\\').Length,
            };
            var financeRoot = Guid.NewGuid();
            var current = Guid.NewGuid();
            var hrRoot = Guid.NewGuid();
            var otherRoot = Guid.NewGuid();
            db.Folders.AddRange(Folder(financeRoot, FinanceId, ""), Folder(ArchiveId, FinanceId, "Archive"), Folder(current, FinanceId, "Current"),
                Folder(hrRoot, HrId, ""), Folder(otherRoot, OtherShareId, ""));

            InventoryFile File(Guid share, Guid folder, string name, long size, DateTimeOffset? modified, DateTimeOffset? created = null,
                Sensitivity label = Sensitivity.Internal, FileAttributes attributes = FileAttributes.Archive) => new()
                {
                    Id = Guid.NewGuid(),
                    ShareId = share,
                    FolderId = folder,
                    Name = name,
                    NameKey = name.ToUpperInvariant(),
                    Extension = Path.GetExtension(name).TrimStart('.'),
                    Size = size,
                    ModifiedAtSource = modified,
                    CreatedAtSource = created ?? modified,
                    Sensitivity = label,
                    Attributes = (int)attributes,
                };
            var handbookTime = Years(1);
            db.Files.AddRange(
                File(FinanceId, ArchiveId, "payroll-2019.xlsx", 50_000, Years(5), label: Sensitivity.Restricted),
                File(FinanceId, ArchiveId, "old-notes.txt", 2_000, Years(3.5)),
                File(FinanceId, ArchiveId, "vendor-list.csv", 8_000, Years(4.5), label: Sensitivity.Confidential),
                // Opened last week, which file activity recorded: in use, not stale.
                File(FinanceId, ArchiveId, "Read-Often.docx", 90_000, Years(5)),
                // Only an access check was recorded, which proves nothing: still stale.
                File(FinanceId, ArchiveId, "checked-only.docx", 3_000, Years(5)),
                // An old file copied here last month keeps its modified time but is new here.
                File(FinanceId, ArchiveId, "copied.pdf", 70_000, Years(5), created: now.AddDays(-30)),
                File(FinanceId, ArchiveId, "no-date.bin", 1_000, null),
                File(FinanceId, current, "budget.xlsx", 40_000, now.AddDays(-10), label: Sensitivity.Restricted),
                // Copies of one handbook on two shares, a same-name file of another size, and files duplicates ignore.
                File(FinanceId, current, "Handbook.pdf", 200_000, handbookTime),
                File(HrId, hrRoot, "handbook.pdf", 200_000, handbookTime),
                File(FinanceId, ArchiveId, "handbook.pdf", 150_000, Years(2)),
                File(FinanceId, current, "Thumbs.db", 5_000, now, attributes: FileAttributes.Hidden | FileAttributes.System),
                File(HrId, hrRoot, "Thumbs.db", 5_000, now, attributes: FileAttributes.Hidden | FileAttributes.System),
                File(FinanceId, current, "tiny.txt", 10, now),
                File(HrId, hrRoot, "tiny.txt", 10, now),
                // Another server's old file and copy, outside the scope these tests use.
                File(OtherShareId, otherRoot, "Handbook.pdf", 200_000, handbookTime),
                File(OtherShareId, otherRoot, "other-old.txt", 4_000, Years(6)));
            var gone = File(FinanceId, ArchiveId, "deleted-old.txt", 9_000, Years(6));
            gone.RemovedAt = now.AddDays(-2);
            db.Files.Add(gone);

            FileAuditEvent Event(long record, string path, ActivityCertainty certainty, ActivityOperation operation) => new()
            {
                ServerId = ServerId,
                Channel = "Security",
                RecordId = record,
                EventId = certainty == ActivityCertainty.Performed ? 4663 : 5145,
                EventTime = now.AddDays(-7),
                IngestedAt = now.AddDays(-7),
                ShareId = FinanceId,
                RelativePath = path,
                ObjectKind = ActivityObjectKind.File,
                Operation = operation,
                Certainty = certainty,
                Outcome = ActivityOutcome.Success,
                RawXml = "<Event/>",
            };
            db.FileAuditEvents.AddRange(
                Event(1, @"ARCHIVE\read-often.DOCX", ActivityCertainty.Performed, ActivityOperation.Read),
                Event(2, @"Archive\checked-only.docx", ActivityCertainty.AccessCheck, ActivityOperation.Read));
            await db.SaveChangesAsync();
        }

        public Task DisposeAsync() => Database.DisposeAsync();
    }

    private sealed record Totals(
        long Files, long Bytes, long StaleFiles, long StaleBytes, long StaleRestricted, long StaleConfidential, long InUse, long NoDate,
        long DuplicateGroups, long DuplicateCopies, long DuplicateBytes);

    private sealed record ShareRow(Guid ShareId, string UncPath, long Files, long StaleFiles, long StaleBytes, long StaleRestricted);

    private sealed record FolderRow(Guid FolderId, string UncPath, long StaleFiles, long StaleBytes, long Restricted, long Confidential);

    private sealed record Summary(int Years, int DefaultYears, Totals Totals, List<ShareRow> Shares, List<FolderRow> Folders, int Servers, int ActivityServers);

    private sealed record StaleFile(Guid FileId, string Path, string Name, long Size, DateTimeOffset? LastChanged, string Label);

    private sealed record Page<T>(List<T> Items, int Total);

    private sealed record Copy(Guid ShareId, string Path, string Label);

    private sealed record Group(string Name, long Size, int Copies, int Shares, long Wasted, bool SameModified, List<Copy> Locations);

    private sealed record Duplicates(List<Group> Items, int Total, long Copies, long Wasted);

    private sealed record RiskRow(string Id, string Rule, string Severity, int? Restricted, int? Confidential, int? Count, Guid? FolderId);

    private sealed record RiskList(List<RiskRow> Items);

    private HttpClient Viewer => _factory.ClientAs(Roles.Viewer);

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Old_files_nobody_used_are_stale_and_recent_use_or_a_new_copy_is_not()
    {
        var s = await Ok<Summary>(await Viewer.GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}"));
        Assert.Equal((3, 3), (s.Years, s.DefaultYears));
        // payroll-2019, old-notes, vendor-list and checked-only.
        Assert.Equal((4L, 63_000L, 1L, 1L), (s.Totals.StaleFiles, s.Totals.StaleBytes, s.Totals.StaleRestricted, s.Totals.StaleConfidential));
        Assert.Equal((1L, 1L), (s.Totals.InUse, s.Totals.NoDate));
        Assert.Equal(15L, s.Totals.Files); // removed files and the other server's are not counted
        Assert.Equal((1, 0), (s.Servers, s.ActivityServers));

        var finance = s.Shares[0];
        Assert.Equal((_lab.FinanceId, 4L, 1L), (finance.ShareId, finance.StaleFiles, finance.StaleRestricted));
        Assert.Equal(0L, Assert.Single(s.Shares, x => x.ShareId == _lab.HrId).StaleFiles);
        var archive = Assert.Single(s.Folders);
        Assert.Equal((_lab.ArchiveId, @"\\fs-clean\Finance\Archive", 4L, 1L, 1L), (archive.FolderId, archive.UncPath, archive.StaleFiles, archive.Restricted, archive.Confidential));

        // With a longer threshold only the 4.5 and 5 year old files count; with 6 years none do.
        Assert.Equal(3L, (await Ok<Summary>(await Viewer.GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}&years=4"))).Totals.StaleFiles);
        Assert.Equal(0L, (await Ok<Summary>(await Viewer.GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}&years=6"))).Totals.StaleFiles);
    }

    [Fact]
    public async Task Stale_files_are_listed_largest_or_oldest_first_and_by_folder()
    {
        var bySize = await Ok<Page<StaleFile>>(await Viewer.GetAsync($"/api/v1/cleanup/stale-files?serverId={_lab.ServerId}"));
        Assert.Equal(4, bySize.Total);
        Assert.Equal(["payroll-2019.xlsx", "vendor-list.csv", "checked-only.docx", "old-notes.txt"], bySize.Items.Select(f => f.Name));
        Assert.Equal((@"\\fs-clean\Finance\Archive", "Restricted"), (bySize.Items[0].Path, bySize.Items[0].Label));

        var oldest = await Ok<Page<StaleFile>>(await Viewer.GetAsync($"/api/v1/cleanup/stale-files?shareId={_lab.FinanceId}&sort=oldest&limit=2"));
        Assert.Equal((4, 2), (oldest.Total, oldest.Items.Count));
        Assert.Equal(["payroll-2019.xlsx", "checked-only.docx"], oldest.Items.Select(f => f.Name));

        var inFolder = await Ok<Page<StaleFile>>(await Viewer.GetAsync($"/api/v1/cleanup/stale-files?serverId={_lab.ServerId}&folderId={_lab.ArchiveId}&offset=3"));
        Assert.Equal((4, "old-notes.txt"), (inFolder.Total, Assert.Single(inFolder.Items).Name));

        // Only the restricted and confidential ones, for "Sensitive and stale".
        var sensitive = await Ok<Page<StaleFile>>(await Viewer.GetAsync($"/api/v1/cleanup/stale-files?serverId={_lab.ServerId}&sensitive=true"));
        Assert.Equal(2, sensitive.Total);
        Assert.Equal("payroll-2019.xlsx", sensitive.Items[0].Name);
        Assert.All(sensitive.Items, f => Assert.True(f.Label is "Restricted" or "Confidential", f.Label));
        Assert.Equal(4, (await Ok<Page<StaleFile>>(await Viewer.GetAsync($"/api/v1/cleanup/stale-files?serverId={_lab.ServerId}&sensitive=false"))).Total);
    }

    [Fact]
    public async Task Thresholds_go_up_to_30_years_for_long_retention_holds()
    {
        var thirty = await Ok<Summary>(await Viewer.GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}&years=30"));
        Assert.Equal(0L, thirty.Totals.StaleFiles);
        Assert.Equal(HttpStatusCode.OK, (await Viewer.GetAsync($"/api/v1/cleanup/stale-files?serverId={_lab.ServerId}&years=25")).StatusCode);
    }

    [Fact]
    public async Task Copies_with_the_same_name_and_size_are_grouped_across_shares()
    {
        var d = await Ok<Duplicates>(await Viewer.GetAsync($"/api/v1/cleanup/duplicates?serverId={_lab.ServerId}"));
        var g = Assert.Single(d.Items);
        Assert.Equal((1, 1L, 200_000L), (d.Total, d.Copies, d.Wasted));
        Assert.Equal((200_000L, 2, 2, 200_000L, true), (g.Size, g.Copies, g.Shares, g.Wasted, g.SameModified));
        Assert.Equal([@"\\fs-clean\Finance\Current", @"\\fs-clean\HR"], g.Locations.Select(l => l.Path));
        Assert.Equal("handbook.pdf", g.Name, ignoreCase: true);

        // Across every server the other server's copy joins the group; within one share there are no copies.
        var everywhere = await Ok<Duplicates>(await Viewer.GetAsync("/api/v1/cleanup/duplicates"));
        Assert.Equal(3, Assert.Single(everywhere.Items, x => x.Size == 200_000).Copies);
        Assert.Equal(0, (await Ok<Duplicates>(await Viewer.GetAsync($"/api/v1/cleanup/duplicates?shareId={_lab.FinanceId}"))).Total);
        var summary = await Ok<Summary>(await Viewer.GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}"));
        Assert.Equal((1L, 1L, 200_000L), (summary.Totals.DuplicateGroups, summary.Totals.DuplicateCopies, summary.Totals.DuplicateBytes));
    }

    [Fact]
    public async Task Old_sensitive_files_are_a_risk_on_their_share()
    {
        var risks = await Ok<RiskList>(await Viewer.GetAsync($"/api/v1/risks?serverId={_lab.ServerId}&rule=stale-sensitive"));
        var r = Assert.Single(risks.Items);
        Assert.Equal(($"stale-sensitive:{_lab.FinanceId}", "Medium", 1, 1, 2), (r.Id, r.Severity, r.Restricted, r.Confidential, r.Count));
        Assert.NotNull(r.FolderId);
    }

    [Fact]
    public async Task Reports_list_stale_files_and_every_copy()
    {
        var stale = await Viewer.GetStringAsync($"/api/v1/reports/stale-data?serverId={_lab.ServerId}");
        Assert.Contains("payroll-2019.xlsx", stale, StringComparison.Ordinal);
        Assert.Contains("checked-only.docx", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-Often.docx", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("copied.pdf", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("other-old.txt", stale, StringComparison.Ordinal);

        var copies = await Viewer.GetStringAsync($"/api/v1/reports/duplicate-files?serverId={_lab.ServerId}");
        Assert.Contains(@"\\fs-clean\Finance\Current\Handbook.pdf", copies, StringComparison.Ordinal);
        Assert.Contains(@"\\fs-clean\HR\handbook.pdf", copies, StringComparison.Ordinal);
        Assert.DoesNotContain("Thumbs.db", copies, StringComparison.Ordinal);
        Assert.DoesNotContain("tiny.txt", copies, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_values_and_unknown_places_are_refused()
    {
        foreach (var query in new[] { "years=0", "years=31", "years=abc" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Viewer.GetAsync($"/api/v1/cleanup?{query}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Viewer.GetAsync("/api/v1/cleanup/stale-files?sort=name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync($"/api/v1/cleanup?shareId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync($"/api/v1/cleanup?serverId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync($"/api/v1/cleanup/duplicates?serverId={_lab.OtherServerId}&shareId={_lab.FinanceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync($"/api/v1/cleanup/stale-files?folderId={Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.PlatformAdmin)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.Viewer)]
    public async Task Everyone_who_reads_posture_can_see_it(string role) =>
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(role).GetAsync($"/api/v1/cleanup?serverId={_lab.ServerId}")).StatusCode);

    [Fact]
    public async Task People_with_no_role_or_not_signed_in_cannot()
    {
        var noRole = _factory.ClientAs("");
        foreach (var path in new[] { "/api/v1/cleanup", "/api/v1/cleanup/stale-files", "/api/v1/cleanup/duplicates", "/api/v1/reports/stale-data", "/api/v1/reports/duplicate-files" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync(path)).StatusCode);
        }
    }
}
