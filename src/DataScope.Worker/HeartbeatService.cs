using DataScope.Persistence;

namespace DataScope.Worker;

/// <summary>Checks in every minute, so System health can tell whether the scanner is running.</summary>
public sealed class HeartbeatService(ServiceHeartbeatWriter writer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => writer.RunAsync(stoppingToken);
}
