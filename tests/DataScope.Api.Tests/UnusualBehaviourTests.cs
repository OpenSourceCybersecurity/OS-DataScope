using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Security;
using DataScope.Core.Activity;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// Unusual behaviour over stored activity (D166): four weeks of a person's ordinary office-hours work, then a night of
/// activity in sensitive folders. Every person, folder and event here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class UnusualBehaviourTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    // A Wednesday afternoon; the four weeks before hold twenty weekdays.
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private const string Alice = "S-1-5-21-1-2-3-1104", Bob = "S-1-5-21-1-2-3-1105", Carol = "S-1-5-21-1-2-3-1106";
    private static readonly ActivityAlertOptions Options = new() { EmailTo = ["soc@northwind.test"] };
    private long _record;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seeded(Guid Server, Guid Share);

    /// <summary>A file server with one share: Projects is ordinary; Finance, HR, Board and Legal hold Confidential files.</summary>
    private async Task<Seeded> FileServer(string host)
    {
        await using var db = database.CreateContext();
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = Now, UpdatedAt = Now, CreatedBySid = "S-1" };
        var share = new Share { Id = Guid.NewGuid(), ServerId = server.Id, Name = "Company", NameKey = "COMPANY", UncPath = $"\\\\{host}\\Company", Included = true, State = ShareState.Scanned };
        db.Add(server);
        db.Add(share);
        AddInventory(db, share.Id, ("Projects", Sensitivity.Public), ("Finance", Sensitivity.Confidential), ("HR", Sensitivity.Restricted),
            ("Board", Sensitivity.Confidential), ("Legal", Sensitivity.Confidential));
        await db.SaveChangesAsync();
        return new Seeded(server.Id, share.Id);
    }

    private static void AddInventory(DataScopeDbContext db, Guid share, params (string Folder, Sensitivity Level)[] folders)
    {
        var job = Guid.NewGuid();
        foreach (var (path, level) in folders)
        {
            var folder = new Folder { Id = Guid.NewGuid(), ShareId = share, Path = path, PathHash = InventoryStore.PathHash(path), Depth = 1, FirstSeenJobId = job, LastSeenJobId = job };
            db.Add(folder);
            db.Files.Add(new InventoryFile
            {
                Id = Guid.NewGuid(),
                ShareId = share,
                FolderId = folder.Id,
                Name = "a.docx",
                NameKey = "A.DOCX",
                Extension = ".docx",
                Sensitivity = level,
                FirstSeenJobId = job,
                LastSeenJobId = job,
            });
        }
    }

    private FileAuditEvent Event(Guid server, Guid share, string sid, string name, string path, DateTimeOffset at,
        ActivityOperation operation = ActivityOperation.Read, ActivityOutcome outcome = ActivityOutcome.Success) => new()
        {
            ServerId = server,
            Channel = ActivityStore.SecurityChannel,
            RecordId = Interlocked.Increment(ref _record),
            EventId = 4663,
            EventTime = at,
            IngestedAt = at,
            PrincipalSid = sid,
            PrincipalName = name,
            ShareId = share,
            RelativePath = path,
            ObjectPath = $"D:\\Company\\{path}",
            ObjectKind = ActivityObjectKind.File,
            PathSource = ActivityPathSource.Event,
            Operation = operation,
            Certainty = ActivityCertainty.Performed,
            Outcome = outcome,
            RawXml = "<Event/>",
        };

    private async Task Add(IEnumerable<FileAuditEvent> events)
    {
        await using var db = database.CreateContext();
        db.FileAuditEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    /// <summary>Twenty ordinary items a weekday, 09:00 to 17:00, over the four weeks before today; Legal now and then.</summary>
    private IEnumerable<FileAuditEvent> UsualWeeks(Seeded s, string sid, string name, int weekdays = 20)
    {
        var days = Enumerable.Range(1, 28).Select(i => Today.AddDays(-i)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(weekdays);
        foreach (var day in days)
        {
            var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
            for (var i = 0; i < 20; i++)
            {
                yield return Event(s.Server, s.Share, sid, name, $"Projects\\plan {i}.docx", start.AddMinutes(i * 24));
            }
            yield return Event(s.Server, s.Share, sid, name, "Legal\\contract.docx", start.AddHours(2));
        }
    }

    /// <summary>A night's work: <paramref name="count"/> files across Finance, HR, Board and Legal from 02:00.</summary>
    private IEnumerable<FileAuditEvent> Night(Seeded s, string sid, string name, int count, int from = 0)
    {
        string[] folders = ["Finance", "HR", "Board", "Legal"];
        var start = new DateTimeOffset(Today.ToDateTime(new TimeOnly(2, 0)), TimeSpan.Zero);
        return Enumerable.Range(from, count).Select(i => Event(s.Server, s.Share, sid, name, $"{folders[i % 4]}\\file {i}.xlsx", start.AddSeconds(i * 10)));
    }

    private async Task<IReadOnlyList<ActivityAlert>> Check(Guid server, ActivityAlertOptions? options = null, Clock? clock = null)
    {
        await using var db = database.CreateContext();
        var time = clock ?? new Clock(Now);
        return await new UnusualActivityStore(db, new NotificationStore(db, time), time)
            .CheckAsync(server, options ?? Options, TimeZoneInfo.Utc, "https://ds01.northwind.test/", default);
    }

    private async Task<List<ActivityAlert>> Alerts(Guid server)
    {
        await using var db = database.CreateContext();
        return await db.ActivityAlerts.AsNoTracking().Where(a => a.ServerId == server).OrderBy(a => a.Rule).ToListAsync();
    }

    [Fact]
    public async Task A_night_in_sensitive_folders_after_weeks_of_office_hours_raises_three_alerts_that_say_what_was_usual()
    {
        var s = await FileServer("fs-unusual-1");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice").Concat(UsualWeeks(s, Carol, "NORTHWIND\\carol")));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 240));
        // Carol's day is ordinary.
        await Add(Enumerable.Range(0, 15).Select(i => Event(s.Server, s.Share, Carol, "NORTHWIND\\carol", $"Projects\\plan {i}.docx", Now.AddHours(-4).AddMinutes(i))));

        var raised = await Check(s.Server);
        Assert.Equal([ActivityAlertRule.UnusualVolume, ActivityAlertRule.UnusualHours, ActivityAlertRule.UnusualPlaces], raised.Select(a => a.Rule).Order());
        Assert.All(raised, a => Assert.Equal((Alice, "NORTHWIND\\alice", ActivityAlertSeverity.Medium, ActivityAlertState.Open), (a.PrincipalSid, a.PrincipalName, a.Severity, a.State)));

        var alerts = await Alerts(s.Server);
        var volume = alerts.Single(a => a.Rule == ActivityAlertRule.UnusualVolume);
        Assert.Equal((240, 21), (volume.EventCount, volume.Usual));
        var hours = alerts.Single(a => a.Rule == ActivityAlertRule.UnusualHours);
        // 240 files ten seconds apart run from 02:00 to 02:40.
        Assert.Equal((240, "02:00 to 03:00"), (hours.EventCount, hours.Detail));
        Assert.Equal(Now.Date.AddHours(2), hours.FirstEventAt.UtcDateTime);
        var places = alerts.Single(a => a.Rule == ActivityAlertRule.UnusualPlaces);
        // Legal holds sensitive data but Alice used it before.
        Assert.Equal((3, "Company\\Board, Company\\Finance and Company\\HR"), (places.EventCount, places.Detail));
        Assert.Equal("NORTHWIND\\alice used 3 folders holding sensitive data on fs-unusual-1 that they hadn't used before: Company\\Board, Company\\Finance and Company\\HR.",
            ActivityAlertText.Describe(places.Rule, places.PrincipalName, places.EventCount, "fs-unusual-1", places.FirstEventAt, places.LastEventAt, places.Usual, places.Detail));

        await using var db = database.CreateContext();
        var learned = await db.ActivityBaselines.AsNoTracking().Where(b => b.ServerId == s.Server).OrderBy(b => b.PersonKey).ToListAsync();
        Assert.Equal(["", Alice, Carol], learned.Select(b => b.PersonKey));
        Assert.All(learned, b => Assert.Equal(Today, b.LearnedOn));
        var alice = learned[1];
        Assert.Equal((20, 21, "NORTHWIND\\alice"), (alice.ActiveDays, alice.BusyDay, alice.PersonName));
        // 09:00 to 16:36, each hour a bit.
        Assert.Equal(0b1_1111_1110_0000_0000, alice.Hours);
        Assert.Equal([$"{s.Share}|legal", $"{s.Share}|projects"], alice.Places.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Checking_again_updates_the_days_alerts_without_raising_or_reopening_them()
    {
        var s = await FileServer("fs-unusual-2");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 240));
        Assert.Equal(3, (await Check(s.Server)).Count);

        var volume = (await Alerts(s.Server)).Single(a => a.Rule == ActivityAlertRule.UnusualVolume);
        await using (var db = database.CreateContext())
        {
            await new ActivityAlertStore(db, new NotificationStore(db, TimeProvider.System), TimeProvider.System)
                .SetStateAsync(volume.Id, ActivityAlertState.Acknowledged, "Year-end close", "S-1-5-21-1-2-3-500", "NORTHWIND\\admin", default);
        }

        await Add(Night(s, Alice, "NORTHWIND\\alice", 30, from: 240));
        Assert.Empty(await Check(s.Server, clock: new Clock(Now.AddMinutes(10))));
        var alerts = await Alerts(s.Server);
        Assert.Equal(3, alerts.Count);
        var after = alerts.Single(a => a.Rule == ActivityAlertRule.UnusualVolume);
        Assert.Equal((270, ActivityAlertState.Acknowledged), (after.EventCount, after.State));
        Assert.True(after.LastEventId > volume.LastEventId);
    }

    [Fact]
    public async Task People_still_being_learned_and_Windows_itself_raise_nothing()
    {
        var s = await FileServer("fs-unusual-3");
        // Bob has only four days so far.
        await Add(UsualWeeks(s, Bob, "NORTHWIND\\bob", weekdays: 4));
        await Add(Night(s, Bob, "NORTHWIND\\bob", 400));
        // The system account and a computer account are never people, whatever they do.
        await Add(UsualWeeks(s, "S-1-5-18", "NT AUTHORITY\\SYSTEM").Concat(Night(s, "S-1-5-18", "NT AUTHORITY\\SYSTEM", 400)));
        await Add(UsualWeeks(s, "S-1-5-21-1-2-3-2001", "NORTHWIND\\FS01$").Concat(Night(s, "S-1-5-21-1-2-3-2001", "NORTHWIND\\FS01$", 400)));

        Assert.Empty(await Check(s.Server));
        await using var db = database.CreateContext();
        var learned = await db.ActivityBaselines.AsNoTracking().Where(b => b.ServerId == s.Server).ToListAsync();
        Assert.Equal(["", Bob], learned.Select(b => b.PersonKey).Order());
        Assert.Equal(4, learned.Single(b => b.PersonKey == Bob).ActiveDays);
    }

    [Fact]
    public async Task Refused_access_and_unreadable_events_are_not_activity()
    {
        var s = await FileServer("fs-unusual-4");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 300).Select(e => { e.Outcome = ActivityOutcome.Failure; return e; }));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 300, from: 300).Select(e => { e.EventId = 0; return e; }));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 300, from: 600).Select(e => { e.Operation = ActivityOperation.MetadataRead; return e; }));
        Assert.Empty(await Check(s.Server));
    }

    [Fact]
    public async Task Baselines_are_learned_once_a_day_and_again_the_next_day()
    {
        var s = await FileServer("fs-unusual-5");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Check(s.Server);
        // Carol's past arrives late: today's baselines stand until tomorrow.
        await Add(UsualWeeks(s, Carol, "NORTHWIND\\carol"));
        await Check(s.Server, clock: new Clock(Now.AddHours(1)));
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.ActivityBaselines.AnyAsync(b => b.ServerId == s.Server && b.PersonKey == Carol));
        }

        await Check(s.Server, clock: new Clock(Now.AddDays(1)));
        await using (var db = database.CreateContext())
        {
            var learned = await db.ActivityBaselines.AsNoTracking().Where(b => b.ServerId == s.Server).ToListAsync();
            Assert.Equal(["", Alice, Carol], learned.Select(b => b.PersonKey).Order());
            Assert.All(learned, b => Assert.Equal(Today.AddDays(1), b.LearnedOn));
        }
    }

    [Fact]
    public async Task Alerts_are_emailed_only_when_medium_alerts_are_and_say_they_are_a_lead()
    {
        var s = await FileServer("fs-unusual-6");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 240));
        var raised = await Check(s.Server, options: new ActivityAlertOptions { EmailTo = ["soc@northwind.test"], EmailMedium = true });
        Assert.Equal(3, raised.Count);

        await using var db = database.CreateContext();
        var ids = raised.Select(a => $"activity-alert:{a.Id}:SOC@NORTHWIND.TEST").ToList();
        var mail = await db.Outbox.AsNoTracking().Where(m => ids.Contains(m.IdempotencyKey)).ToListAsync();
        Assert.Equal(3, mail.Count);
        var hours = mail.Single(m => m.Subject.Contains("unusual hours", StringComparison.Ordinal));
        Assert.Equal("Data Scope alert: Active at unusual hours on fs-unusual-6", hours.Subject);
        Assert.Contains("from 02:00 to 03:00, hours they hadn't worked in before", hours.Body, StringComparison.Ordinal);
        Assert.Contains("not proof of wrongdoing", hours.Body, StringComparison.Ordinal);

        var quiet = await FileServer("fs-unusual-7");
        await Add(UsualWeeks(quiet, Alice, "NORTHWIND\\alice"));
        await Add(Night(quiet, Alice, "NORTHWIND\\alice", 240));
        var unsent = await Check(quiet.Server);
        Assert.Equal(3, unsent.Count);
        var keys = unsent.Select(a => $"activity-alert:{a.Id}:SOC@NORTHWIND.TEST").ToList();
        Assert.False(await db.Outbox.AnyAsync(m => keys.Contains(m.IdempotencyKey)));
    }

    [Fact]
    public async Task Turned_off_learns_nothing_raises_nothing_and_forgets_what_was_learned()
    {
        var s = await FileServer("fs-unusual-8");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 240));
        Assert.Empty(await Check(s.Server, options: new ActivityAlertOptions { UnusualEnabled = false }));
        Assert.Empty(await Check(s.Server, options: new ActivityAlertOptions { Enabled = false }));
        await using var db = database.CreateContext();
        Assert.False(await db.ActivityBaselines.AnyAsync(b => b.ServerId == s.Server));

        // Turning it off later forgets what was learned.
        Assert.Equal(3, (await Check(s.Server)).Count);
        Assert.True(await db.ActivityBaselines.AnyAsync(b => b.ServerId == s.Server));
        Assert.Empty(await Check(s.Server, options: new ActivityAlertOptions { UnusualEnabled = false }));
        Assert.False(await db.ActivityBaselines.AnyAsync(b => b.ServerId == s.Server));
    }

    private sealed record SourceView(Guid ServerId, string Hostname, DateOnly? LearnedOn, int People, int Judged);

    private sealed record UnusualView(bool On, int LearnDays, int MinActiveDays, List<SourceView> Sources);

    [Fact]
    public async Task The_page_shows_how_many_people_are_learned_and_judged_to_those_who_may_read_activity()
    {
        var s = await FileServer("fs-unusual-9");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice").Concat(UsualWeeks(s, Bob, "NORTHWIND\\bob", weekdays: 3)));
        await Check(s.Server);

        using var factory = new ApiFactory(database);
        var view = await factory.ClientAs(Roles.Auditor).GetFromJsonAsync<UnusualView>("/api/v1/activity/unusual");
        Assert.NotNull(view);
        Assert.Equal((true, 28, 5), (view.On, view.LearnDays, view.MinActiveDays));
        var source = Assert.Single(view.Sources, x => x.ServerId == s.Server);
        Assert.Equal(("fs-unusual-9", (DateOnly?)Today, 2, 1), (source.Hostname, source.LearnedOn, source.People, source.Judged));

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/activity/unusual")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientAs(null).GetAsync("/api/v1/activity/unusual")).StatusCode);
    }

    [Fact]
    public async Task The_alert_list_carries_what_was_usual_and_why()
    {
        var s = await FileServer("fs-unusual-10");
        await Add(UsualWeeks(s, Alice, "NORTHWIND\\alice"));
        await Add(Night(s, Alice, "NORTHWIND\\alice", 240));
        await Check(s.Server);

        using var factory = new ApiFactory(database);
        var list = await factory.ClientAs(Roles.Auditor).GetFromJsonAsync<AlertList>("/api/v1/activity/alerts?limit=500");
        var mine = list!.Items.Where(a => a.ServerId == s.Server).ToList();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, a => Assert.True(a.Unusual));
        var volume = mine.Single(a => a.Rule == nameof(ActivityAlertRule.UnusualVolume));
        Assert.Equal(("Unusually busy day", 21), (volume.Title, volume.Usual));
        Assert.Equal("NORTHWIND\\alice touched 240 files and folders on fs-unusual-10 in one day, far more than on their usual busy day (about 21).", volume.Description);
    }

    private sealed record AlertItem(string Rule, string Title, Guid ServerId, string Description, bool Unusual, int? Usual, string? Detail);

    private sealed record AlertList(List<AlertItem> Items, int Open);
}
