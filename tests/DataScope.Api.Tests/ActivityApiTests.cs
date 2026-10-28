using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Tests.Activity;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

[Trait("Category", "Database")]
public sealed class ActivityApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private const string Root = @"D:\Shares\Finance";
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public ActivityApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record EventView(long Id, string Hostname, int EventId, long RecordId, DateTimeOffset Time, string? PrincipalSid, string Operation, string Label,
        string Certainty, string Outcome, Guid? ShareId, string? ShareName, string? RelativePath, string? ObjectPath, string ObjectKind, string PathSource);

    private sealed record PageView(List<EventView> Items, string? Next);

    private sealed record DetailView(EventView Event, string Explanation, List<string> Rights, long? AccessMask, string RawXml, string? OldSecurity, string? NewSecurity);

    private sealed record DayView(DateOnly Date, int Reads, int Writes, int Deletes, int PermissionChanges, int Denied);

    private sealed record SummaryView(List<DayView> Days, long Unmatched, DateTimeOffset? LastEventAt);

    private sealed record MappingView(Guid Id, string Name, string? LocalPath, string? LocalPathSource);

    private sealed record SourceView(Guid ServerId, string Hostname, bool Enabled, string State, long EventsStored, long Unmatched, List<MappingView> Shares, LogView? Log, List<GapView> Gaps);

    private sealed record LogView(long? OldestRecordId, long? NewestRecordId, long? RecordCount, DateTimeOffset? OldestEventAt, long? MaxSizeBytes, long? Behind, bool AtRisk);

    private sealed record GapView(string Reason, long FirstRecordId, long? LastRecordId, long? Records);

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin);

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static (Guid Server, Guid Share)? _seeded;

    /// <summary>One server with a day of synthetic activity, seeded once for the class. Event times are relative to now.</summary>
    private async Task<(Guid Server, Guid Share)> Seed()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded is { } done)
            {
                return done;
            }
            await using var db = _db.CreateContext();
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = "fs-activity", CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            var share = new Share
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                Name = "Finance",
                NameKey = "FINANCE",
                UncPath = @"\\fs-activity\Finance",
                Kind = ShareKind.Disk,
                Included = true,
                CreatedAt = now,
                UpdatedAt = now,
                LocalPath = Root,
                LocalPathSource = ShareLocalPathSource.Admin,
            };
            db.AddRange(server, share);
            await db.SaveChangesAsync();
            var store = new ActivityStore(db, TimeProvider.System);
            await store.SetEnabledAsync(server.Id, true, "S-1", default);
            var t = now.AddHours(-2);
            string[] events =
            [
                EventXml.ObjectAccess(1, $@"{Root}\Budget\2026.xlsx", 0x1, time: t),
                EventXml.ObjectAccess(2, $@"{Root}\Budget\2026.xlsx", 0x2, time: t.AddMinutes(1)),
                EventXml.ShareCheck(3, "Finance", Root, @"Budget\2026.xlsx", 0x1),
                EventXml.ShareConnect(4, "Finance", Root),
                EventXml.ObjectAccess(5, $@"{Root}\Pay_roll\a.csv", 0x10000, handle: 0x77, time: t.AddMinutes(2)),
                EventXml.Deleted(6, handle: 0x77, time: t.AddMinutes(2)),
                EventXml.HandleRequest(7, $@"{Root}\Budget\2026.xlsx", 0x2, denied: true, sid: EventXml.Bob, user: "bob"),
                EventXml.PermissionsChanged(8, $@"{Root}\Budget", "D:(A;;FA;;;BA)", "D:(A;;FA;;;BA)(A;;FR;;;WD)"),
                EventXml.ObjectAccess(9, @"C:\Elsewhere\x.txt", 0x1, time: t.AddMinutes(3)),
                EventXml.ObjectAccess(10, $@"{Root}\Pay%roll\b.csv", 0x1, time: t.AddMinutes(4)),
            ];
            // Events dated relative to now; share checks and others use the fixed test epoch.
            await store.IngestAsync(server.Id, ActivityStore.SecurityChannel, events.Select(x => new RawSecurityEvent(SecurityEventXml.Parse(x).RecordId, x)).ToList(), default);
            _seeded = (server.Id, share.Id);
            return _seeded.Value;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static async Task<List<EventView>> All(HttpClient client, string query)
    {
        var items = new List<EventView>();
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<PageView>($"/api/v1/activity/events?{query}&limit=2{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            items.AddRange(page!.Items);
            cursor = page.Next;
        }
        while (cursor is not null);
        return items;
    }

    [Fact]
    public async Task Activity_needs_a_role_that_may_read_evidence()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/api/v1/activity/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/activity/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("").GetAsync("/api/v1/activity/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(Roles.Auditor).GetAsync("/api/v1/activity/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(Roles.SecurityAnalyst).GetAsync("/api/v1/activity/sources")).StatusCode);
    }

    [Fact]
    public async Task Access_checks_and_share_connections_are_hidden_unless_asked_for_and_never_called_reads()
    {
        var (server, _) = await Seed();
        var normal = await All(Admin, $"serverId={server}");
        Assert.DoesNotContain(normal, e => e.Certainty is "AccessCheck" or "ShareSession");
        Assert.Equal(8, normal.Count);

        var all = await All(Admin, $"serverId={server}&includeChecks=true");
        Assert.Equal(10, all.Count);
        var check = all.Single(e => e.EventId == 5145);
        Assert.Equal(("ShareAccessCheck", "Share access check", "AccessCheck"), (check.Operation, check.Label, check.Certainty));
        Assert.DoesNotContain(all, e => e.EventId is 5145 or 5140 && e.Label.Contains("Read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pages_are_newest_first_without_gaps_or_repeats()
    {
        var (server, _) = await Seed();
        var all = await All(Admin, $"serverId={server}&includeChecks=true");
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(all.OrderByDescending(e => e.Time).ThenByDescending(e => e.Id).Select(e => e.Id), all.Select(e => e.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/activity/events?cursor=garbage")).StatusCode);
    }

    [Fact]
    public async Task Filters_narrow_the_list()
    {
        var (server, share) = await Seed();
        var deletes = await All(Admin, $"serverId={server}&operation=Deleted");
        var delete = Assert.Single(deletes);
        Assert.Equal(("Correlated", @"Pay_roll\a.csv", (Guid?)share), (delete.PathSource, delete.RelativePath, delete.ShareId));

        var denied = Assert.Single(await All(Admin, $"serverId={server}&outcome=Failure"));
        Assert.Equal(EventXml.Bob, denied.PrincipalSid);
        Assert.Single(await All(Admin, $"serverId={server}&principalSid={EventXml.Bob}"));

        // Search is literal: _ and % are not wildcards, and backslashes work.
        Assert.Equal(["Pay_roll\\a.csv"], (await All(Admin, $"serverId={server}&search={Uri.EscapeDataString(@"pay_roll\")}")).Select(e => e.RelativePath).Distinct());
        Assert.Equal(["Pay%roll\\b.csv"], (await All(Admin, $"serverId={server}&search={Uri.EscapeDataString("Pay%")}")).Select(e => e.RelativePath));

        var unmatched = Assert.Single(await All(Admin, $"serverId={server}&search=Elsewhere"));
        Assert.Null(unmatched.ShareId);
        Assert.Equal("Read or list folder", unmatched.Label);

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/activity/events?operation=Hack")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/activity/events?outcome=Maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync($"/api/v1/activity/events?search={new string('a', 201)}")).StatusCode);
    }

    [Fact]
    public async Task An_event_shows_its_rights_explanation_and_raw_text()
    {
        var (server, _) = await Seed();
        var all = await All(Admin, $"serverId={server}&includeChecks=true");

        var check = await Admin.GetFromJsonAsync<DetailView>($"/api/v1/activity/events/{all.Single(e => e.EventId == 5145).Id}");
        Assert.Contains("not proof the file was opened or read", check!.Explanation);
        Assert.Equal(["ReadData (or ListDirectory)"], check.Rights);
        Assert.StartsWith("<Event", check.RawXml);

        var delete = await Admin.GetFromJsonAsync<DetailView>($"/api/v1/activity/events/{all.Single(e => e.EventId == 4660).Id}");
        Assert.Contains("comes from event record 5", delete!.Explanation);

        var acl = await Admin.GetFromJsonAsync<DetailView>($"/api/v1/activity/events/{all.Single(e => e.EventId == 4670).Id}");
        Assert.Equal(("D:(A;;FA;;;BA)", "D:(A;;FA;;;BA)(A;;FR;;;WD)"), (acl!.OldSecurity, acl.NewSecurity));

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/v1/activity/events/999999")).StatusCode);
    }

    [Fact]
    public async Task The_summary_counts_recorded_operations_per_day()
    {
        await Seed();
        var summary = await Admin.GetFromJsonAsync<SummaryView>("/api/v1/activity/summary?days=7&offsetMinutes=0");
        Assert.Equal(7, summary!.Days.Count);
        var totals = (Reads: summary.Days.Sum(d => d.Reads), Writes: summary.Days.Sum(d => d.Writes), Deletes: summary.Days.Sum(d => d.Deletes));
        // Three reads (Budget, the unmatched path, Pay%roll); one write; the 4660. The 4663 for DELETE is not counted as a deletion.
        Assert.Equal((3, 1, 1), totals);
        Assert.True(summary.Unmatched >= 1);
    }

    [Fact]
    public async Task Turning_collection_on_is_an_audited_admin_action()
    {
        await using (var db = _db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.ManagedServers.Add(new ManagedServer { Id = Guid.NewGuid(), Hostname = "fs-activity-toggle", CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" });
            await db.SaveChangesAsync();
        }
        var sources = await Admin.GetFromJsonAsync<List<SourceView>>("/api/v1/activity/sources");
        var source = sources!.Single(s => s.Hostname == "fs-activity-toggle");
        Assert.Equal((false, "Off"), (source.Enabled, source.State));

        var url = $"/api/v1/activity/sources/{source.ServerId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Auditor).PutAsJsonAsync(url, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false).PutAsJsonAsync(url, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync($"/api/v1/activity/sources/{Guid.NewGuid()}", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PutAsJsonAsync(url, new { enabled = true })).StatusCode);

        source = (await Admin.GetFromJsonAsync<List<SourceView>>("/api/v1/activity/sources"))!.Single(s => s.Hostname == "fs-activity-toggle");
        Assert.Equal((true, "Waiting"), (source.Enabled, source.State));
        await using var check = _db.CreateContext();
        Assert.True(await check.AppAuditLog.AnyAsync(a => a.Action == "activity.enable" && a.Target == "fs-activity-toggle"));
    }

    [Fact]
    public async Task Sources_show_the_log_health_and_records_missed()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = _db.CreateContext())
        {
            db.ManagedServers.Add(new ManagedServer { Id = id, Hostname = "fs-activity-health", CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" });
            db.ActivitySources.Add(new ActivitySource
            {
                ServerId = id,
                Channel = ActivityStore.SecurityChannel,
                Enabled = true,
                State = ActivitySourceState.Ok,
                LastRecordId = 400,
                LogOldestRecordId = 1,
                LogNewestRecordId = 1000,
                LogRecordCount = 1000,
                LogOldestEventAt = now.AddDays(-3),
                LogMaxSizeBytes = 134_217_728,
                LogCheckedAt = now,
                UpdatedAt = now,
                NextReadAt = now.AddHours(1),
            });
            db.ActivityGaps.Add(new ActivityGap { ServerId = id, Channel = ActivityStore.SecurityChannel, Reason = ActivityGapReason.Overwritten, FirstRecordId = 101, LastRecordId = 150, DetectedAt = now });
            db.ActivityGaps.Add(new ActivityGap { ServerId = id, Channel = ActivityStore.SecurityChannel, Reason = ActivityGapReason.LogReset, FirstRecordId = 1001, DetectedAt = now });
            await db.SaveChangesAsync();
        }
        var source = (await Admin.GetFromJsonAsync<List<SourceView>>("/api/v1/activity/sources"))!.Single(s => s.ServerId == id);

        Assert.Equal((600L, true, 134_217_728L), (source.Log!.Behind!.Value, source.Log.AtRisk, source.Log.MaxSizeBytes!.Value));
        Assert.Equal([("LogReset", (long?)null), ("Overwritten", 50L)], source.Gaps.Select(g => (g.Reason, g.Records)));
        var summary = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity/summary?days=7&offsetMinutes=0");
        Assert.True(summary.GetProperty("gaps").GetInt32() >= 2);

        // A server never checked has no log health yet.
        Assert.All((await Admin.GetFromJsonAsync<List<SourceView>>("/api/v1/activity/sources"))!.Where(s => s.State == "Off"), s => Assert.Null(s.Log));
    }

    [Fact]
    public async Task An_admin_can_set_a_share_folder_and_only_as_a_drive_path()
    {
        var (server, share) = await Seed();
        var url = $"/api/v1/activity/shares/{share}/local-path";
        foreach (var bad in new[] { @"\\fs\Finance", "Finance", @"C:\a|b", "C:\\a\nb" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync(url, new { localPath = bad })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.SecurityAnalyst).PutAsJsonAsync(url, new { localPath = Root })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync($"/api/v1/activity/shares/{Guid.NewGuid()}/local-path", new { localPath = Root })).StatusCode);

        var res = await Admin.PutAsJsonAsync(url, new { localPath = Root + "\\" });
        res.EnsureSuccessStatusCode();
        Assert.Equal((Root, "Admin"), await res.Content.ReadFromJsonAsync<MappingView>() is { } m ? (m.LocalPath, m.LocalPathSource) : default);
        var source = (await Admin.GetFromJsonAsync<List<SourceView>>("/api/v1/activity/sources"))!.Single(s => s.ServerId == server);
        Assert.Equal(Root, source.Shares.Single(s => s.Id == share).LocalPath);
        await using var db = _db.CreateContext();
        Assert.True(await db.AppAuditLog.AnyAsync(a => a.Action == "share.local-path"));
    }
}
