using DataScope.Persistence;

namespace DataScope.Api.Progress;

/// <summary>
/// Keeps today's progress row current (D165): every <c>Progress:CheckMinutes</c> (15 by default; 0 turns it off) it
/// measures again if a scan, a directory sync or an audited change is newer than today's row. Measuring runs the same
/// risk check as the Risks page, so it is skipped while nothing changed.
/// </summary>
internal sealed partial class ProgressService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ProgressService> log) : BackgroundService
{
    /// <summary>How long to wait while it is off before looking at the setting again.</summary>
    private static readonly TimeSpan Off = TimeSpan.FromMinutes(1);

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(0, config.GetValue("Progress:CheckMinutes", 15)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Waits first, so starting the service is never slowed by a risk check.
                var interval = Interval;
                await Task.Delay(interval > TimeSpan.Zero ? interval : Off, stoppingToken);
                if (Interval <= TimeSpan.Zero)
                {
                    continue;
                }
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ProgressRecorder>().RecordIfChangedAsync(stoppingToken) is { } row)
                {
                    LogRecorded(row.Day);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (Persistence.DatabaseOutage.Is(ex))
            {
                LogDatabaseUnavailable(ex);
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Recorded the posture for {Day}")]
    private partial void LogRecorded(DateOnly day);

    [LoggerMessage(LogLevel.Warning, "Database unavailable; the posture will be recorded later")]
    private partial void LogDatabaseUnavailable(Exception ex);
}
