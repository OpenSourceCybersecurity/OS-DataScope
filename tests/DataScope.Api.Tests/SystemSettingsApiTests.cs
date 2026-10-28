using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// System settings through the API (D123): who may read and change them, the checks on every value, the audit trail,
/// and that a saved value is used straight away. Each test gets its own database, since every test changes settings.
/// </summary>
[Trait("Category", "Database")]
public sealed class SystemSettingsApiTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] DuplicateAddresses = ["it@yourdomain.local", "IT@yourdomain.local"];
    private static readonly string[] TwoAddresses = ["it@yourdomain.local", " security@yourdomain.local "];

    private readonly TestDatabase _db = new();
    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _factory = new ApiFactory(_db);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _factory?.Dispose();

    private const string Url = "/api/v1/settings";

    private HttpClient Admin => _factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-70-80-90-1200", @"LAB\settings-admin");

    private sealed record SettingDto(string Key, string Group, string Kind, string? Value, List<string>? Values, string? Default, string Source,
        bool OverridesFile, DateTimeOffset? UpdatedAt, string? UpdatedByName, long Min, long Max, string? ZeroLabel, bool Optional);

    private sealed record SettingsDto(List<GroupDto> Groups, List<SettingDto> Settings, string TimeZone, bool EmailSignIn);

    private sealed record GroupDto(string Id, string Title);

    private sealed record TestEmailDto(long Id, string State, string? Error);

    private sealed record ServerDto(Guid Id, ScheduleDto Schedule);

    private sealed record ScheduleDto(string Frequency, int StartHour, string? Day, int? StopHour, int StartMinute, int? EveryHours);

    private sealed record CleanupDto(int Years, int DefaultYears);

    private sealed record ProblemDto(Dictionary<string, string[]> Errors);

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static SettingDto Find(SettingsDto settings, string key) => settings.Settings.Single(s => s.Key == key);

    private static Task<HttpResponseMessage> Save(HttpClient client, object values) => client.PutAsJsonAsync(Url, new { values });

    private static async Task<Dictionary<string, string[]>> Refused(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Errors;
    }

    [Fact]
    public async Task Admins_and_auditors_see_every_setting_with_its_built_in_value()
    {
        foreach (var role in new[] { Roles.PlatformAdmin, Roles.Auditor })
        {
            var settings = await Ok<SettingsDto>(await _factory.ClientAs(role).GetAsync(Url));
            Assert.Equal(Core.Settings.SystemSettings.All.Count, settings.Settings.Count);
            Assert.Equal(Core.Settings.SystemSettings.Groups.Count, settings.Groups.Count);
            var stale = Find(settings, "Cleanup:StaleAfterYears");
            Assert.Equal(("3", "3", "Default", 1L, 30L), (stale.Value, stale.Default, stale.Source, stale.Min, stale.Max));
            Assert.Equal("No limit", Find(settings, "Scanner:MaxFoldersPerSecond").ZeroLabel);
            Assert.Empty(Find(settings, "Activity:Alerts:EmailTo").Values!);
            Assert.DoesNotContain(settings.Settings, s => s.Key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || s.Key.StartsWith("ConnectionStrings", StringComparison.Ordinal) || s.Key.StartsWith("Auth", StringComparison.Ordinal));
            Assert.False(settings.EmailSignIn);
        }
    }

    [Theory]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Viewer)]
    [InlineData("")]
    public async Task Other_roles_cannot_read_settings(string role)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(role).GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Signed_out_users_cannot_read_settings()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync(Url)).StatusCode);
    }

    [Theory]
    [InlineData(Roles.Auditor)]
    [InlineData(Roles.SecurityAnalyst)]
    [InlineData(Roles.Viewer)]
    public async Task Only_platform_admins_can_change_settings_or_send_a_test_email(string role)
    {
        var client = _factory.ClientAs(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(client, new Dictionary<string, object> { ["Cleanup:StaleAfterYears"] = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Url}/test-email", new { to = "it@yourdomain.local" })).StatusCode);
        await using var db = _db.CreateContext();
        Assert.Empty(await db.SystemSettings.ToListAsync());
        Assert.Empty(await db.Outbox.ToListAsync());
    }

    [Fact]
    public async Task Changes_need_the_anti_forgery_header()
    {
        var client = _factory.ClientAs(Roles.PlatformAdmin, antiForgeryHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(client, new Dictionary<string, object> { ["Cleanup:StaleAfterYears"] = 7 })).StatusCode);
        await using var db = _db.CreateContext();
        Assert.Empty(await db.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task A_saved_value_is_shown_audited_and_used_straight_away()
    {
        var before = await Ok<CleanupDto>(await Admin.GetAsync("/api/v1/cleanup"));
        Assert.Equal(3, before.DefaultYears);

        var saved = await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Cleanup:StaleAfterYears"] = 12, ["Scanner:MaxFoldersPerSecond"] = "250" }));
        var stale = Find(saved, "Cleanup:StaleAfterYears");
        Assert.Equal(("12", "Here", @"LAB\settings-admin"), (stale.Value, stale.Source, stale.UpdatedByName));
        Assert.NotNull(stale.UpdatedAt);
        Assert.Equal("250", Find(saved, "Scanner:MaxFoldersPerSecond").Value);

        // The stale screen uses the new default at once, without a restart.
        var after = await Ok<CleanupDto>(await Admin.GetAsync("/api/v1/cleanup"));
        Assert.Equal((12, 12), (after.Years, after.DefaultYears));

        await using var db = _db.CreateContext();
        var audit = await db.AppAuditLog.SingleAsync(a => a.Action == "settings.change");
        Assert.Equal(("system settings", @"LAB\settings-admin"), (audit.Target, audit.ActorName));
        Assert.Contains("Cleanup:StaleAfterYears", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"Before\":\"3\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"After\":\"12\"", audit.Details, StringComparison.Ordinal);

        // Saving the same value again changes nothing and is not audited again.
        await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Cleanup:StaleAfterYears"] = 12 }));
        Assert.Equal(1, await db.AppAuditLog.CountAsync(a => a.Action == "settings.change"));
    }

    [Fact]
    public async Task Reset_goes_back_to_the_built_in_value_and_is_audited()
    {
        await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Retention:ActivityDays"] = 0 }));
        var reset = await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object?> { ["Retention:ActivityDays"] = null }));
        var days = Find(reset, "Retention:ActivityDays");
        Assert.Equal(("365", "Default"), (days.Value, days.Source));
        Assert.Null(days.UpdatedByName);

        await using var db = _db.CreateContext();
        Assert.Empty(await db.SystemSettings.ToListAsync());
        var audit = await db.AppAuditLog.Where(a => a.Action == "settings.change").OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.Contains("\"Reset\":true", audit[1].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_in_datascope_json_shows_as_from_the_file_and_a_saved_value_wins()
    {
        using var withFile = _factory.WithWebHostBuilder(b => b.UseSetting("Activity:Alerts:MassDeleteCount", "75"));
        var client = ApiFactory.Configure(withFile.CreateClient(), Roles.PlatformAdmin);

        var fromFile = Find(await Ok<SettingsDto>(await client.GetAsync(Url)), "Activity:Alerts:MassDeleteCount");
        Assert.Equal(("75", "File", false), (fromFile.Value, fromFile.Source, fromFile.OverridesFile));

        var saved = Find(await Ok<SettingsDto>(await Save(client, new Dictionary<string, object> { ["Activity:Alerts:MassDeleteCount"] = 120 })), "Activity:Alerts:MassDeleteCount");
        Assert.Equal(("120", "Here", true), (saved.Value, saved.Source, saved.OverridesFile));

        var reset = Find(await Ok<SettingsDto>(await Save(client, new Dictionary<string, object?> { ["Activity:Alerts:MassDeleteCount"] = null })), "Activity:Alerts:MassDeleteCount");
        Assert.Equal(("75", "File"), (reset.Value, reset.Source));
    }

    [Fact]
    public async Task Bad_values_are_refused_and_nothing_is_saved()
    {
        var errors = await Refused(await Save(Admin, new Dictionary<string, object>
        {
            ["Cleanup:StaleAfterYears"] = 31,
            ["Retention:ActivityDays"] = 10,
            ["Notifications:Smtp:From"] = "not an address",
            ["Notifications:PublicUrl"] = "http://ds01.yourdomain.local/",
            ["Activity:Alerts:Enabled"] = "maybe",
            ["Activity:Alerts:EmailTo"] = DuplicateAddresses,
            ["Activity:PollSeconds"] = 1.5,
            ["Scanner:MaxFoldersPerSecond"] = 50,
        }));
        Assert.Equal(7, errors.Count);
        Assert.DoesNotContain("Scanner:MaxFoldersPerSecond", errors.Keys);
        await using var db = _db.CreateContext();
        Assert.Empty(await db.SystemSettings.ToListAsync());
        Assert.Equal(0, await db.AppAuditLog.CountAsync(a => a.Action == "settings.change"));
    }

    [Theory]
    [InlineData("ConnectionStrings:DataScope")]
    [InlineData("Auth:Roles:PlatformAdmin:0")]
    [InlineData("Notifications:Smtp:Password")]
    [InlineData("Notifications:Smtp:UseStartTls")]
    [InlineData("Scanner:Content:MaxFileBytes")]
    public async Task Settings_not_on_the_list_cannot_be_saved(string key)
    {
        var errors = await Refused(await Save(Admin, new Dictionary<string, object> { [key] = "x" }));
        Assert.Contains(key, errors.Keys);
        await using var db = _db.CreateContext();
        Assert.Empty(await db.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task An_empty_or_huge_change_is_refused()
    {
        await Refused(await Save(Admin, new Dictionary<string, object>()));
        await Refused(await Admin.PutAsJsonAsync(Url, new { }));
        var many = Enumerable.Range(0, 101).ToDictionary(i => $"Unknown:{i}", object (_) => 1);
        await Refused(await Save(Admin, many));
    }

    [Fact]
    public async Task Settings_that_depend_on_each_other_are_checked_together()
    {
        // A mail server without a From address can't work.
        var errors = await Refused(await Save(Admin, new Dictionary<string, object> { ["Notifications:Smtp:Host"] = "mail.yourdomain.local" }));
        Assert.Contains("Notifications:Smtp:From", errors.Keys);
        // Saved together, they are fine.
        await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object>
        {
            ["Notifications:Smtp:Host"] = "mail.yourdomain.local",
            ["Notifications:Smtp:From"] = "datascope@yourdomain.local",
        }));
        // And the From address can't then be emptied on its own.
        errors = await Refused(await Save(Admin, new Dictionary<string, object> { ["Notifications:Smtp:From"] = "" }));
        Assert.Contains("Notifications:Smtp:From", errors.Keys);

        // A weekly schedule needs a day.
        errors = await Refused(await Save(Admin, new Dictionary<string, object> { ["Schedule:NewServer:Frequency"] = "Weekly" }));
        Assert.Contains("Schedule:NewServer", errors.Keys);
    }

    [Fact]
    public async Task New_servers_get_the_saved_default_schedule()
    {
        await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object>
        {
            ["Schedule:NewServer:Frequency"] = "Weekly",
            ["Schedule:NewServer:Day"] = "Saturday",
            ["Schedule:NewServer:StartHour"] = 22,
            ["Schedule:NewServer:StartMinute"] = 30,
            ["Schedule:NewServer:StopHour"] = "",
        }));
        var server = await Ok<ServerDto>(await Admin.PostAsJsonAsync("/api/v1/servers", new { hostname = "fs-settings-new.lab.test" }));
        Assert.Equal(new ScheduleDto("Weekly", 22, "Saturday", null, 30, null), server.Schedule);
    }

    [Fact]
    public async Task Alert_addresses_are_saved_as_a_list_and_can_be_emptied()
    {
        var saved = await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Activity:Alerts:EmailTo"] = TwoAddresses }));
        Assert.Equal(["it@yourdomain.local", "security@yourdomain.local"], Find(saved, "Activity:Alerts:EmailTo").Values!);
        var emptied = await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Activity:Alerts:EmailTo"] = Array.Empty<string>() }));
        var list = Find(emptied, "Activity:Alerts:EmailTo");
        Assert.Empty(list.Values!);
        Assert.Equal("Here", list.Source);
    }

    [Fact]
    public async Task A_test_email_is_queued_audited_and_its_progress_can_be_read()
    {
        var bad = await Refused(await Admin.PostAsJsonAsync($"{Url}/test-email", new { to = "it@yourdomain.local\r\nBcc: x@y.z" }));
        Assert.Contains("to", bad.Keys);
        await Refused(await Admin.PostAsJsonAsync($"{Url}/test-email", new { to = "" }));

        var response = await Admin.PostAsJsonAsync($"{Url}/test-email", new { to = "it@yourdomain.local" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<TestEmailDto>())!;
        Assert.Equal("Pending", queued.State);
        Assert.Equal($"/api/v1/settings/test-email/{queued.Id}", response.Headers.Location!.OriginalString);

        var status = await Ok<TestEmailDto>(await Admin.GetAsync($"{Url}/test-email/{queued.Id}"));
        Assert.Equal(queued.Id, status.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"{Url}/test-email/{queued.Id + 1000}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.Auditor).GetAsync($"{Url}/test-email/{queued.Id}")).StatusCode);

        await using var db = _db.CreateContext();
        var message = await db.Outbox.SingleAsync();
        Assert.Equal((NotificationKind.TestEmail, "it@yourdomain.local"), (message.Kind, message.RecipientEmail));
        var audit = await db.AppAuditLog.SingleAsync(a => a.Action == "settings.test-email");
        Assert.Equal("it@yourdomain.local", audit.Target);
    }

    [Fact]
    public async Task Rows_written_around_the_api_are_ignored_unless_they_are_on_the_list_and_valid()
    {
        await using (var owner = _db.CreateOwnerContext())
        {
            var now = DateTimeOffset.UtcNow;
            owner.SystemSettings.AddRange(
                new SystemSetting { Key = "ConnectionStrings:DataScope", Value = "\"Host=evil\"", UpdatedAt = now, UpdatedBySid = "S-1", UpdatedByName = "x" },
                new SystemSetting { Key = "Cleanup:StaleAfterYears", Value = "\"500\"", UpdatedAt = now, UpdatedBySid = "S-1", UpdatedByName = "x" },
                new SystemSetting { Key = "Retention:ActivityDays", Value = "\"0\"", UpdatedAt = now, UpdatedBySid = "S-1", UpdatedByName = "x" });
            await owner.SaveChangesAsync();
        }
        // A save makes the API read the table again.
        var settings = await Ok<SettingsDto>(await Save(Admin, new Dictionary<string, object> { ["Directory:SyncHours"] = 12 }));
        Assert.Equal(("3", "Default"), (Find(settings, "Cleanup:StaleAfterYears").Value, Find(settings, "Cleanup:StaleAfterYears").Source));
        Assert.Equal(("0", "Here"), (Find(settings, "Retention:ActivityDays").Value, Find(settings, "Retention:ActivityDays").Source));
        Assert.Equal("12", Find(settings, "Directory:SyncHours").Value);
    }
}
