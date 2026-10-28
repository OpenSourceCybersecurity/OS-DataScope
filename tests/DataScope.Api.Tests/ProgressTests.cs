using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataScope.Api.Progress;
using DataScope.Api.Reports;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataScope.Api.Tests;

/// <summary>
/// Progress over time (D165) on made-up files: the day's row and how it matches the Risks page, measuring again only
/// after something changed, the range, retention, sharing outside the organisation and the report. Every name here is
/// made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class ProgressTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly Guid _job = Guid.NewGuid();
    private ApiFactory Factory { get; set; } = null!;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        Factory = new ApiFactory(_database);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    /// <summary>
    /// Finance on a file server, with no owner: two Restricted files, a Confidential file, and an Internal file that
    /// couldn't be read.
    /// </summary>
    private async Task<Folder> SeedAsync()
    {
        await using var db = _database.CreateContext();
        var serverId = Guid.NewGuid();
        db.ManagedServers.Add(new ManagedServer { Id = serverId, Hostname = "fs01.contoso.example", Kind = SourceKind.FileServer, CreatedAt = _now, UpdatedAt = _now, CreatedBySid = "S-1" });
        var shareId = Guid.NewGuid();
        db.Shares.Add(new Share
        {
            Id = shareId,
            ServerId = serverId,
            Name = "Finance",
            NameKey = "FINANCE",
            UncPath = @"\\fs01\Finance",
            Included = true,
            State = ShareState.Scanned,
            LastScannedAt = _now.AddHours(-1),
        });
        var root = new Folder { Id = Guid.NewGuid(), ShareId = shareId, Path = "", PathHash = InventoryStore.PathHash(""), Depth = 0, FirstSeenJobId = _job, LastSeenJobId = _job };
        db.Folders.Add(root);
        db.Files.AddRange(
            File(root, "pay.xlsx", Sensitivity.Restricted),
            File(root, "audit.docx", Sensitivity.Confidential),
            File(root, "old.csv", Sensitivity.Restricted),
            File(root, "menu.docx", Sensitivity.Internal, ContentState.AccessDenied));
        await db.SaveChangesAsync();
        return root;
    }

    [Fact]
    public async Task The_first_visit_of_a_day_measures_it_the_way_the_other_pages_do()
    {
        await SeedAsync();
        var viewer = Factory.ClientAs(Roles.Viewer);
        var result = await Ok(await viewer.GetAsync("/api/v1/progress"));
        Assert.Equal(90, result.GetProperty("days").GetInt32());
        Assert.Equal(Today.AddDays(-89), DateOnly.Parse(result.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(result.GetProperty("thinned").GetBoolean());
        var day = Assert.Single(result.GetProperty("points").EnumerateArray());
        Assert.Equal(Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), day.GetProperty("day").GetString());

        Assert.Equal((4L, 1L, 2L, 1L), (day.GetProperty("files").GetInt64(), day.GetProperty("filesNotRead").GetInt64(),
            day.GetProperty("restricted").GetInt64(), day.GetProperty("confidential").GetInt64()));
        Assert.Equal(1, day.GetProperty("sharesUnowned").GetInt32());
        Assert.False(day.TryGetProperty("remediationOpen", out _));

        var risks = await Ok(await Factory.ClientAs(Roles.SecurityAnalyst).GetAsync("/api/v1/risks"));
        foreach (var (row, page) in new[] { ("risksHigh", "high"), ("risksMedium", "medium"), ("risksLow", "low"), ("risksAccepted", "accepted"), ("risksSnoozed", "snoozed") })
        {
            Assert.Equal(risks.GetProperty(page).GetInt32(), day.GetProperty(row).GetInt32());
        }

        // A second visit the same day reads the row rather than measuring again.
        await using var db = _database.CreateContext();
        var taken = (await db.PostureSnapshots.SingleAsync()).TakenAt;
        var again = await Ok(await viewer.GetAsync("/api/v1/progress"));
        Assert.Equal(taken, Assert.Single(again.GetProperty("points").EnumerateArray()).GetProperty("takenAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_day_is_measured_again_only_after_something_changed()
    {
        await SeedAsync();
        using var scope = Factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<ProgressRecorder>();
        var first = await recorder.RecordIfChangedAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await recorder.RecordIfChangedAsync(CancellationToken.None));

        // Accepting a risk is audited, so the day is measured again with one fewer open risk.
        var analyst = Factory.ClientAs(Roles.SecurityAnalyst);
        var risk = (await Ok(await analyst.GetAsync("/api/v1/risks"))).GetProperty("items").EnumerateArray().First();
        var severity = risk.GetProperty("severity").GetString();
        await Ok(await analyst.PostAsJsonAsync("/api/v1/risks/decisions",
            new { riskId = risk.GetProperty("id").GetString(), kind = "accepted", note = "Made-up test decision.", until = Today.AddDays(30) }));

        var second = await recorder.RecordIfChangedAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(first!.RisksAccepted + 1, second!.RisksAccepted);
        Assert.Equal(first.RisksHigh + first.RisksMedium + first.RisksLow - 1, second.RisksHigh + second.RisksMedium + second.RisksLow);
        Assert.Equal(severity == "High" ? first.RisksHigh - 1 : first.RisksHigh, second.RisksHigh);

        await using var db = _database.CreateContext();
        var row = await db.PostureSnapshots.SingleAsync();
        Assert.Equal(second.RisksAccepted, row.RisksAccepted);
        Assert.Null(await recorder.RecordIfChangedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_range_shows_the_days_kept_and_old_days_are_deleted()
    {
        await SeedAsync();
        await using (var db = _database.CreateContext())
        {
            db.PostureSnapshots.AddRange(
                Past(Today.AddDays(-ProgressRecorder.KeepDays - 1), high: 9),
                Past(Today.AddDays(-200), high: 7),
                Past(Today.AddDays(-20), high: 5),
                Past(Today.AddDays(-3), high: 4));
            await db.SaveChangesAsync();
        }
        var viewer = Factory.ClientAs(Roles.Viewer);
        // Measuring today deletes the row older than Data Scope keeps.
        var recent = await Ok(await viewer.GetAsync("/api/v1/progress?days=30"));
        Assert.Equal([Today.AddDays(-20), Today.AddDays(-3), Today], Days(recent));
        Assert.Equal(Today.AddDays(-200), DateOnly.Parse(recent.GetProperty("keptSince").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        var year = await Ok(await viewer.GetAsync("/api/v1/progress?days=365"));
        Assert.Equal([Today.AddDays(-200), Today.AddDays(-20), Today.AddDays(-3), Today], Days(year));

        await using var check = _database.CreateContext();
        Assert.Equal(4, await check.PostureSnapshots.CountAsync());
    }

    [Theory]
    [InlineData("6")]
    [InlineData("1831")]
    [InlineData("-1")]
    public async Task A_range_outside_7_to_1830_days_is_refused(string days)
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Factory.ClientAs(Roles.Viewer).GetAsync($"/api/v1/progress?days={days}")).StatusCode);
    }

    [Fact]
    public async Task Progress_needs_a_signed_in_user()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.ClientAs(null).GetAsync("/api/v1/progress")).StatusCode);
        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.PostureSnapshots.CountAsync());
    }

    [Fact]
    public async Task The_report_lists_each_day_and_what_changed_over_the_period()
    {
        await SeedAsync();
        await using (var db = _database.CreateContext())
        {
            db.PostureSnapshots.Add(Past(Today.AddDays(-10), high: 5));
            await db.SaveChangesAsync();
        }
        var response = await Factory.ClientAs(Roles.SecurityAnalyst).GetAsync("/api/v1/reports/progress?format=csv&days=30");
        var csv = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, csv);
        Assert.Contains("Open high risks 5 to ", csv, StringComparison.Ordinal);
        Assert.Contains(Today.AddDays(-10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), csv, StringComparison.Ordinal);
        Assert.DoesNotContain("pay.xlsx", csv, StringComparison.Ordinal);
    }

    private static List<DateOnly> Days(JsonElement result) =>
        [.. result.GetProperty("points").EnumerateArray().Select(p => DateOnly.Parse(p.GetProperty("day").GetString()!, System.Globalization.CultureInfo.InvariantCulture))];

    private static PostureSnapshot Past(DateOnly day, int high) => new()
    {
        Day = day,
        TakenAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero),
        RisksHigh = high,
        Files = 100,
        Restricted = 10,
        Confidential = 20,
    };

    private InventoryFile File(Folder folder, string name, Sensitivity level, ContentState content = ContentState.Scanned) => new()
    {
        Id = Guid.NewGuid(),
        ShareId = folder.ShareId,
        FolderId = folder.Id,
        Name = name,
        NameKey = name.ToUpperInvariant(),
        Extension = Path.GetExtension(name),
        Sensitivity = level,
        ContentState = content,
        ClassifiedPolicy = ClassificationPolicy.CurrentVersion,
        FirstSeenJobId = _job,
        LastSeenJobId = _job,
    };

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

/// <summary>Thinning a long range to weekly points (D165).</summary>
public sealed class ProgressThinningTests
{
    [Fact]
    public void A_long_range_keeps_each_weeks_last_day_and_the_first_day()
    {
        var start = new DateOnly(2026, 1, 1);
        var rows = Enumerable.Range(0, 400).Select(i => new PostureSnapshot { Day = start.AddDays(i), RisksHigh = i }).ToList();
        var thinned = ProgressEndpoints.Thin(rows);
        Assert.True(thinned.Count <= ProgressEndpoints.MaxPoints);
        Assert.Equal(rows[0].Day, thinned[0].Day);
        Assert.Equal(rows[^1].Day, thinned[^1].Day);
        Assert.Equal(thinned.Count, thinned.Select(r => r.Day).Distinct().Count());
        // Between the first and last, one day a week.
        var weekly = thinned.Skip(1).SkipLast(1).ToList();
        Assert.All(weekly.Zip(weekly.Skip(1)), pair => Assert.Equal(7, pair.Second.Day.DayNumber - pair.First.Day.DayNumber));
    }

    [Fact]
    public void The_change_names_only_what_moved()
    {
        var first = new PostureSnapshot { RisksHigh = 12, SensitiveOpen = 40, SharesUnowned = 3 };
        var last = new PostureSnapshot { RisksHigh = 4, SensitiveOpen = 40, SharesUnowned = 1 };
        Assert.Equal(["open high risks 12 to 4", "shares with no owner 3 to 1"], ProgressReport.Changes(first, last));
        Assert.Equal(["No change"], ProgressReport.Changes(first, first));
    }
}
