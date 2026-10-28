using System.Globalization;
using DataScope.Core.Settings;
using DataScope.Persistence.Entities;
using DataScope.Persistence.Settings;
using DataScope.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>
/// How saved settings reach the services (D123): only listed keys with valid values become configuration, a saved
/// value wins over datascope.json, and the scanner service picks changes up without a restart.
/// </summary>
public sealed class SystemSettingsWorkerTests
{
    private static SystemSetting Row(string key, string json) =>
        new() { Key = key, Value = json, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBySid = "S-1-5-21-1", UpdatedByName = @"LAB\admin" };

    private static (IConfigurationRoot Config, SystemSettingsConfigurationProvider Saved) Build(Dictionary<string, string?> file)
    {
        var saved = new SystemSettingsConfigurationProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(file).Add(new TestSource(saved)).Build();
        return (config, saved);
    }

    private sealed class TestSource(IConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class Production : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DataScope.Worker";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Only_listed_keys_with_valid_values_become_configuration()
    {
        var skipped = new List<string>();
        var data = SystemSettingsConfigurationProvider.ToConfiguration(
        [
            Row("ConnectionStrings:DataScope", "\"Host=elsewhere\""),
            Row("Notifications:Smtp:Password", "\"secret\""),
            Row("Cleanup:StaleAfterYears", "\"500\""),
            Row("Cleanup:MinDuplicateBytes", "not json"),
            Row("Activity:PollSeconds", "120"),
            Row("Retention:ActivityDays", "\"0\""),
            Row("Notifications:Smtp:Host", "null"),
        ], skipped.Add);
        Assert.Equal(["ConnectionStrings:DataScope", "Notifications:Smtp:Password", "Cleanup:StaleAfterYears", "Cleanup:MinDuplicateBytes", "Activity:PollSeconds"], skipped);
        Assert.Equal("0", data["Retention:ActivityDays"]);
        Assert.Equal("", data["Notifications:Smtp:Host"]);
        Assert.Equal(2, data.Count);
    }

    [Fact]
    public void A_saved_list_hides_longer_lists_in_the_file()
    {
        var data = SystemSettingsConfigurationProvider.ToConfiguration([Row("Activity:Alerts:EmailTo", "[\"it@yourdomain.local\"]")]);
        Assert.Equal(SystemSettings.MaxEmailRecipients, data.Count);
        Assert.Equal("it@yourdomain.local", data["Activity:Alerts:EmailTo:0"]);
        Assert.Equal("", data["Activity:Alerts:EmailTo:1"]);

        var (config, saved) = Build(new()
        {
            ["Activity:Alerts:EmailTo:0"] = "old1@yourdomain.local",
            ["Activity:Alerts:EmailTo:1"] = "old2@yourdomain.local",
        });
        Assert.Equal(["old1@yourdomain.local", "old2@yourdomain.local"], CurrentSettings.BindAlerts(config).EmailTo);
        saved.Apply(data);
        Assert.Equal(["it@yourdomain.local"], CurrentSettings.BindAlerts(config).EmailTo);
    }

    [Fact]
    public void Encoded_values_read_back_the_same()
    {
        var years = SystemSettings.Find("Cleanup:StaleAfterYears")!;
        Assert.True(SystemSettingsConfigurationProvider.TryDecode(years, SystemSettingsConfigurationProvider.Encode(years, "12", null), out var text, out _));
        Assert.Equal("12", text);
        var host = SystemSettings.Find("Notifications:Smtp:Host")!;
        Assert.Equal("null", SystemSettingsConfigurationProvider.Encode(host, "", null));
        var list = SystemSettings.Find("Activity:Alerts:EmailTo")!;
        Assert.True(SystemSettingsConfigurationProvider.TryDecode(list, SystemSettingsConfigurationProvider.Encode(list, null, ["a@yourdomain.local"]), out _, out var items));
        Assert.Equal(["a@yourdomain.local"], items!);
        Assert.False(SystemSettingsConfigurationProvider.TryDecode(list, "\"a@yourdomain.local\"", out _, out _));
        Assert.False(SystemSettingsConfigurationProvider.TryDecode(years, "[1]", out _, out _));
    }

    [Fact]
    public void Applying_the_same_values_again_does_not_reload()
    {
        var (config, saved) = Build([]);
        var reloads = 0;
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(config.GetReloadToken, () => reloads++);
        Assert.True(saved.Apply(new Dictionary<string, string?> { ["Directory:SyncHours"] = "12" }));
        Assert.False(saved.Apply(new Dictionary<string, string?> { ["Directory:SyncHours"] = "12" }));
        Assert.True(saved.Apply(new Dictionary<string, string?>()));
        Assert.Equal(2, reloads);
        Assert.False(saved.Has("Directory:SyncHours"));
    }

    [Fact]
    public void The_scanner_service_reads_saved_values_without_a_restart()
    {
        var (config, saved) = Build(new()
        {
            ["Scanner:MaxFoldersPerSecond"] = "10",
            ["Activity:Alerts:MassDeleteCount"] = "75",
            ["Retention:ActivityDays"] = "400",
            ["Notifications:Smtp:Host"] = "old-mail.yourdomain.local",
            ["Notifications:Smtp:From"] = "datascope@yourdomain.local",
            ["Notifications:PublicUrl"] = "https://ds01.yourdomain.local/",
        });
        var current = new CurrentSettings(config, new Production(), NullLogger<CurrentSettings>.Instance);
        Assert.Equal(10, current.Scan().MaxFoldersPerSecond);
        Assert.Equal(75, current.Alerts().MassDeleteCount);
        Assert.Equal(400, current.Retention().ActivityDays);
        Assert.Equal("old-mail.yourdomain.local", current.Notifications().Smtp.Host);

        saved.Apply(new Dictionary<string, string?>
        {
            ["Scanner:MaxFoldersPerSecond"] = "0",
            ["Activity:Alerts:MassDeleteCount"] = "200",
            ["Retention:ActivityDays"] = "0",
            ["Notifications:Smtp:Host"] = "",
            ["Notifications:PublicUrl"] = "",
        });
        Assert.Equal(0, current.Scan().MaxFoldersPerSecond);
        Assert.Equal(200, current.Alerts().MassDeleteCount);
        Assert.Equal(0, current.Retention().ActivityDays);
        var notifications = current.Notifications();
        Assert.False(notifications.EmailConfigured);
        // An emptied address falls back to this server's own name.
        Assert.StartsWith("https://", notifications.PublicUrl, StringComparison.Ordinal);
        Assert.NotEqual("https://ds01.yourdomain.local/", notifications.PublicUrl);
    }

    [Fact]
    public void A_bad_value_in_the_file_keeps_the_last_good_settings()
    {
        var file = new Dictionary<string, string?> { ["Retention:ActivityDays"] = "400", ["Activity:Alerts:MassDeleteCount"] = "75" };
        var (config, _) = Build(file);
        var current = new CurrentSettings(config, new Production(), NullLogger<CurrentSettings>.Instance);
        Assert.Equal(400, current.Retention().ActivityDays);
        Assert.Equal(75, current.Alerts().MassDeleteCount);

        config["Retention:ActivityDays"] = "5";
        config["Activity:Alerts:MassDeleteCount"] = "1";
        Assert.Equal(400, current.Retention().ActivityDays);
        Assert.Equal(75, current.Alerts().MassDeleteCount);
    }

    [Fact]
    public void Turning_off_STARTTLS_is_still_refused_outside_development()
    {
        var (config, _) = Build(new() { ["Notifications:Smtp:UseStartTls"] = "false" });
        Assert.Throws<InvalidOperationException>(() => CurrentSettings.BindNotifications(config, new Production()));
    }

    [Fact]
    public void The_built_in_values_match_what_the_services_use_when_nothing_is_set()
    {
        string? Default(string key) => SystemSettings.Find(key)!.Default;
        var retention = new DataScope.Persistence.RetentionOptions();
        Assert.Equal(retention.RemovedItemsDays.ToString(CultureInfo.InvariantCulture), Default("Retention:RemovedItemsDays"));
        Assert.Equal(retention.PermissionChangesDays.ToString(CultureInfo.InvariantCulture), Default("Retention:PermissionChangesDays"));
        Assert.Equal(retention.OldScanGapsDays.ToString(CultureInfo.InvariantCulture), Default("Retention:OldScanGapsDays"));
        Assert.Equal(retention.NotificationsDays.ToString(CultureInfo.InvariantCulture), Default("Retention:NotificationsDays"));
        Assert.Equal(retention.ScanHistoryDays.ToString(CultureInfo.InvariantCulture), Default("Retention:ScanHistoryDays"));
        Assert.Equal(retention.ActivityDays.ToString(CultureInfo.InvariantCulture), Default("Retention:ActivityDays"));
        Assert.Equal(retention.IntervalHours.ToString(CultureInfo.InvariantCulture), Default("Retention:IntervalHours"));
        Assert.Equal(DataScope.Persistence.RetentionOptions.MinimumDays, SystemSettings.Find("Retention:ActivityDays")!.Min);

        var alerts = new DataScope.Core.Activity.ActivityAlertOptions();
        Assert.Equal(alerts.Enabled ? "true" : "false", Default("Activity:Alerts:Enabled"));
        Assert.Equal(alerts.EmailMedium ? "true" : "false", Default("Activity:Alerts:EmailMedium"));
        Assert.Equal(alerts.MassDeleteCount.ToString(CultureInfo.InvariantCulture), Default("Activity:Alerts:MassDeleteCount"));
        Assert.Equal(alerts.MassDeleteMinutes.ToString(CultureInfo.InvariantCulture), Default("Activity:Alerts:MassDeleteMinutes"));
        Assert.Equal(alerts.DeniedCount.ToString(CultureInfo.InvariantCulture), Default("Activity:Alerts:DeniedCount"));
        Assert.Equal(alerts.DeniedMinutes.ToString(CultureInfo.InvariantCulture), Default("Activity:Alerts:DeniedMinutes"));
        Assert.Equal(alerts.PermissionChangeMinutes.ToString(CultureInfo.InvariantCulture), Default("Activity:Alerts:PermissionChangeMinutes"));

        var cleanup = new DataScope.Api.Cleanup.CleanupOptions();
        Assert.Equal(cleanup.StaleAfterYears.ToString(CultureInfo.InvariantCulture), Default("Cleanup:StaleAfterYears"));
        Assert.Equal(cleanup.MinDuplicateBytes.ToString(CultureInfo.InvariantCulture), Default("Cleanup:MinDuplicateBytes"));
        Assert.Equal((DataScope.Api.Cleanup.CleanupOptions.MinYears, DataScope.Api.Cleanup.CleanupOptions.MaxYears),
            ((int)SystemSettings.Find("Cleanup:StaleAfterYears")!.Min, (int)SystemSettings.Find("Cleanup:StaleAfterYears")!.Max));

        var notifications = new NotificationOptions();
        Assert.Equal(notifications.ReminderMinutes.ToString(CultureInfo.InvariantCulture), Default("Notifications:ReminderMinutes"));
        Assert.Equal(notifications.Smtp.Port.ToString(CultureInfo.InvariantCulture), Default("Notifications:Smtp:Port"));

        var scan = ScanLimits.From(new ConfigurationBuilder().Build());
        Assert.Equal(scan.MaxFoldersPerSecond.ToString(CultureInfo.InvariantCulture), Default("Scanner:MaxFoldersPerSecond"));
        Assert.Equal(scan.MaxFilesReadPerSecond.ToString(CultureInfo.InvariantCulture), Default("Scanner:MaxFilesReadPerSecond"));
    }
}
