using DataScope.Core.Activity;
using DataScope.Persistence;
using DataScope.Persistence.Settings;

namespace DataScope.Worker;

/// <summary>
/// The settings administrators can change on System settings while the service runs (D123), read from configuration
/// each time they are needed. A value that fails its checks is logged and the last good one kept, so a bad edit to
/// datascope.json can't stop scans; values saved in the UI are checked before they are saved.
/// </summary>
public sealed partial class CurrentSettings(IConfiguration config, IHostEnvironment environment, ILogger<CurrentSettings> log)
{
    private ActivityAlertOptions? _alerts;
    private RetentionOptions? _retention;
    private NotificationOptions? _notifications;
    private ScanLimits? _scan;

    public ScanLimits Scan() => Read(ref _scan, "Scanner", () => ScanLimits.From(config));

    public ActivityAlertOptions Alerts() => Read(ref _alerts, ActivityAlertOptions.Section, () => BindAlerts(config));

    public RetentionOptions Retention() => Read(ref _retention, RetentionOptions.Section, () =>
    {
        var options = new RetentionOptions();
        config.GetSection(RetentionOptions.Section).Bind(options);
        options.Validate();
        return options;
    });

    public NotificationOptions Notifications() => Read(ref _notifications, NotificationOptions.Section, () => BindNotifications(config, environment));

    /// <summary>The alert settings, without the blank entries that stand in for a shorter list saved in the UI.</summary>
    public static ActivityAlertOptions BindAlerts(IConfiguration config)
    {
        var options = new ActivityAlertOptions();
        config.GetSection(ActivityAlertOptions.Section).Bind(options);
        options.EmailTo = [.. options.EmailTo.Where(a => !string.IsNullOrWhiteSpace(a))];
        options.Validate();
        return options;
    }

    public static NotificationOptions BindNotifications(IConfiguration config, IHostEnvironment environment)
    {
        var options = new NotificationOptions();
        config.GetSection(NotificationOptions.Section).Bind(options);
        if (!options.Smtp.UseStartTls && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Notifications:Smtp:UseStartTls can only be turned off when the environment is Development.");
        }
        if (string.IsNullOrWhiteSpace(options.PublicUrl))
        {
            options.PublicUrl = $"https://{System.Net.Dns.GetHostName().ToLowerInvariant()}:{config.GetValue("Https:Port", 443)}/";
        }
        return options;
    }

    private T Read<T>(ref T? last, string section, Func<T> load) where T : class
    {
        try
        {
            return last = load();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException && last is not null)
        {
            LogInvalid(section, ex.Message);
            return last!;
        }
    }

    [LoggerMessage(LogLevel.Warning, "The {Section} settings are not valid ({Message}); keeping the previous ones")]
    private partial void LogInvalid(string section, string message);
}

/// <summary>Reads the settings saved on System settings before the other services start, then every 30 seconds.</summary>
public sealed class SystemSettingsService(SystemSettingsRefresher refresher) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await refresher.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => refresher.RunAsync(stoppingToken);
}
