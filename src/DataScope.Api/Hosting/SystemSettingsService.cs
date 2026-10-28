using DataScope.Persistence.Settings;

namespace DataScope.Api.Hosting;

/// <summary>Reads the settings saved on System settings before the other services start, then every 30 seconds (D123).</summary>
internal sealed class SystemSettingsService(SystemSettingsRefresher refresher) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await refresher.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => refresher.RunAsync(stoppingToken);
}
