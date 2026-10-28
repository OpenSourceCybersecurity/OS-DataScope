using DataScope.Core.ScanJobs;
using DataScope.Core.Settings;

namespace DataScope.Core.Tests;

/// <summary>The list of settings administrators manage on System settings (D123), and the checks on their values.</summary>
public class SystemSettingsTests
{
    private static SettingDefinition Def(string key) => SystemSettings.Find(key) ?? throw new InvalidOperationException(key);

    [Fact]
    public void Every_setting_has_a_known_group_a_unique_key_and_a_valid_default()
    {
        var groups = SystemSettings.Groups.Select(g => g.Id).ToHashSet();
        Assert.Equal(SystemSettings.All.Count, SystemSettings.All.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var d in SystemSettings.All)
        {
            Assert.Contains(d.Group, groups);
            Assert.False(string.IsNullOrWhiteSpace(d.Label), d.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.Help), d.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.Applies), d.Key);
            if (d.Kind != SettingKind.EmailList)
            {
                Assert.Null(SystemSettings.Problem(d, d.Default));
            }
            Assert.True(d.Default is not null || d.Optional || d.Kind == SettingKind.EmailList, $"{d.Key} needs a default or must be optional");
        }
        Assert.All(SystemSettings.Groups, g => Assert.Contains(SystemSettings.All, d => d.Group == g.Id));
    }

    [Theory]
    [InlineData("ConnectionStrings:DataScope")]
    [InlineData("Auth:Mode")]
    [InlineData("Auth:Roles:PlatformAdmin:0")]
    [InlineData("Https:CertificateThumbprint")]
    [InlineData("Notifications:Smtp:Password")]
    [InlineData("Notifications:Smtp:UserName")]
    [InlineData("Notifications:Smtp:UseStartTls")]
    [InlineData("Scanner:Content:MaxFileBytes")]
    [InlineData("Scanner:MaxDepth")]
    [InlineData("Directory:BindPassword")]
    [InlineData("Activity:Enabled")]
    [InlineData("RateLimits:RequestsPerMinute")]
    public void Security_sensitive_and_internal_settings_are_not_managed_here(string key)
    {
        Assert.Null(SystemSettings.Find(key));
        Assert.DoesNotContain(SystemSettings.All, d => d.Key.StartsWith("Auth", StringComparison.OrdinalIgnoreCase)
            || d.Key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
            || d.Key.StartsWith("Https", StringComparison.OrdinalIgnoreCase)
            || d.Key.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keys_are_found_whatever_their_case()
    {
        Assert.Same(Def("Cleanup:StaleAfterYears"), SystemSettings.Find("cleanup:staleafteryears"));
    }

    [Theory]
    [InlineData("Cleanup:StaleAfterYears", "30", true)]
    [InlineData("Cleanup:StaleAfterYears", "31", false)]
    [InlineData("Cleanup:StaleAfterYears", "0", false)]
    [InlineData("Cleanup:StaleAfterYears", "", false)]
    [InlineData("Cleanup:StaleAfterYears", "2.5", false)]
    [InlineData("Cleanup:StaleAfterYears", "-3", false)]
    [InlineData("Cleanup:StaleAfterYears", " 3", false)]
    [InlineData("Cleanup:StaleAfterYears", "+3", false)]
    [InlineData("Scanner:MaxFoldersPerSecond", "0", true)]
    [InlineData("Scanner:MaxFoldersPerSecond", "100000", true)]
    [InlineData("Scanner:MaxFoldersPerSecond", "100001", false)]
    [InlineData("Retention:ActivityDays", "0", true)]
    [InlineData("Retention:ActivityDays", "29", false)]
    [InlineData("Retention:ActivityDays", "30", true)]
    [InlineData("Activity:PollSeconds", "0", false)]
    [InlineData("Activity:PollSeconds", "14", false)]
    [InlineData("Activity:Alerts:Enabled", "false", true)]
    [InlineData("Activity:Alerts:Enabled", "yes", false)]
    [InlineData("Activity:Alerts:Enabled", "True", false)]
    [InlineData("Cleanup:MinDuplicateBytes", "1048576", true)]
    [InlineData("Cleanup:MinDuplicateBytes", "5", false)]
    [InlineData("Schedule:NewServer:Frequency", "Weekly", true)]
    [InlineData("Schedule:NewServer:Frequency", "Monthly", false)]
    [InlineData("Schedule:NewServer:StopHour", "", true)]
    [InlineData("Schedule:NewServer:Day", "Saturday", true)]
    [InlineData("Schedule:NewServer:Day", "6", false)]
    [InlineData("Notifications:Smtp:Host", "mail.yourdomain.local", true)]
    [InlineData("Notifications:Smtp:Host", "", true)]
    [InlineData("Notifications:Smtp:Host", "mail.yourdomain.local:25", false)]
    [InlineData("Notifications:Smtp:Host", "smtp://mail", false)]
    [InlineData("Notifications:Smtp:Host", "mail\nRCPT TO", false)]
    [InlineData("Notifications:Smtp:From", "datascope@yourdomain.local", true)]
    [InlineData("Notifications:Smtp:From", "Data Scope <datascope@yourdomain.local>", false)]
    [InlineData("Notifications:Smtp:From", "datascope@local", false)]
    [InlineData("Notifications:Smtp:From", "a@b.c\r\nBcc: x@y.z", false)]
    [InlineData("Notifications:PublicUrl", "https://ds01.yourdomain.local/", true)]
    [InlineData("Notifications:PublicUrl", "http://ds01.yourdomain.local/", false)]
    [InlineData("Notifications:PublicUrl", "https://user:pw@ds01.yourdomain.local/", false)]
    [InlineData("Notifications:PublicUrl", "javascript:alert(1)", false)]
    [InlineData("Notifications:PublicUrl", "https://ds01/?next=evil", false)]
    public void Values_are_checked_before_they_are_saved(string key, string value, bool ok)
    {
        var problem = SystemSettings.Problem(Def(key), value);
        Assert.True(ok == (problem is null), problem ?? "accepted");
    }

    [Fact]
    public void A_required_setting_cannot_be_emptied()
    {
        Assert.NotNull(SystemSettings.Problem(Def("Activity:PollSeconds"), null));
        Assert.NotNull(SystemSettings.Problem(Def("Notifications:Smtp:Port"), ""));
    }

    [Fact]
    public void Alert_email_lists_hold_distinct_addresses_up_to_the_limit()
    {
        var d = Def("Activity:Alerts:EmailTo");
        Assert.Null(SystemSettings.Problem(d, null, []));
        Assert.Null(SystemSettings.Problem(d, null, ["it@yourdomain.local", "security@yourdomain.local"]));
        Assert.NotNull(SystemSettings.Problem(d, null, ["it@yourdomain.local", "IT@yourdomain.local"]));
        Assert.NotNull(SystemSettings.Problem(d, null, ["it@yourdomain.local", "not an address"]));
        Assert.NotNull(SystemSettings.Problem(d, null, ["it@yourdomain.local", ""]));
        Assert.NotNull(SystemSettings.Problem(d, null, [.. Enumerable.Range(0, SystemSettings.MaxEmailRecipients + 1).Select(i => $"u{i}@yourdomain.local")]));
    }

    [Fact]
    public void Nothing_set_gives_new_servers_the_nightly_schedule()
    {
        Assert.Equal(ScanScheduleSettings.NewServer, SystemSettings.NewServerScheduleFrom(_ => null));
    }

    [Fact]
    public void The_new_server_schedule_comes_from_its_settings()
    {
        var values = new Dictionary<string, string?>
        {
            ["Schedule:NewServer:Frequency"] = "Weekly",
            ["Schedule:NewServer:Day"] = "Saturday",
            ["Schedule:NewServer:StartHour"] = "22",
            ["Schedule:NewServer:StartMinute"] = "30",
            ["Schedule:NewServer:StopHour"] = "",
            ["Schedule:NewServer:EveryHours"] = "4",
        };
        var schedule = SystemSettings.NewServerScheduleFrom(k => values.GetValueOrDefault(k));
        Assert.Equal(new ScanScheduleSettings(ScanFrequency.Weekly, 22, DayOfWeek.Saturday, null, 30), schedule);
        Assert.Null(ScanSchedule.Problem(schedule));
    }

    [Fact]
    public void New_servers_get_a_full_scan_every_Saturday_unless_changed()
    {
        Assert.Equal(FullScanSettings.NewServer, SystemSettings.NewServerFullScanFrom(_ => null));
        var values = new Dictionary<string, string?> { ["Schedule:NewServer:FullScan"] = "Monthly", ["Schedule:NewServer:FullScanDay"] = "Sunday" };
        Assert.Equal(new FullScanSettings(FullScanFrequency.Monthly, DayOfWeek.Sunday), SystemSettings.NewServerFullScanFrom(k => values.GetValueOrDefault(k)));
        values["Schedule:NewServer:FullScan"] = "Off";
        Assert.Equal(FullScanSettings.Off, SystemSettings.NewServerFullScanFrom(k => values.GetValueOrDefault(k)));
    }

    [Fact]
    public void Settings_that_depend_on_each_other_are_checked_together()
    {
        var values = new Dictionary<string, string?>();
        string? Get(string k) => values.GetValueOrDefault(k);
        Assert.Empty(SystemSettings.CrossProblems(Get));

        // Hourly needs how many hours apart; weekly needs a day.
        values["Schedule:NewServer:Frequency"] = "Hourly";
        Assert.Contains(SystemSettings.NewServerSchedule, SystemSettings.CrossProblems(Get).Keys);
        values["Schedule:NewServer:EveryHours"] = "5";
        Assert.Contains(SystemSettings.NewServerSchedule, SystemSettings.CrossProblems(Get).Keys);
        values["Schedule:NewServer:EveryHours"] = "4";
        Assert.Empty(SystemSettings.CrossProblems(Get));
        values["Schedule:NewServer:Frequency"] = "Weekly";
        Assert.Contains(SystemSettings.NewServerSchedule, SystemSettings.CrossProblems(Get).Keys);
        values["Schedule:NewServer:Day"] = "Sunday";
        Assert.Empty(SystemSettings.CrossProblems(Get));

        // Weekly or monthly full scans need a day; by hand doesn't.
        values["Schedule:NewServer:FullScanDay"] = "";
        Assert.Contains(SystemSettings.NewServerSchedule, SystemSettings.CrossProblems(Get).Keys);
        values["Schedule:NewServer:FullScan"] = "Off";
        Assert.Empty(SystemSettings.CrossProblems(Get));
        values["Schedule:NewServer:FullScan"] = "Monthly";
        values["Schedule:NewServer:FullScanDay"] = "Friday";
        Assert.Empty(SystemSettings.CrossProblems(Get));

        // A mail server needs a From address.
        values["Notifications:Smtp:Host"] = "mail.yourdomain.local";
        Assert.Contains("Notifications:Smtp:From", SystemSettings.CrossProblems(Get).Keys);
        values["Notifications:Smtp:From"] = "datascope@yourdomain.local";
        Assert.Empty(SystemSettings.CrossProblems(Get));
    }
}
