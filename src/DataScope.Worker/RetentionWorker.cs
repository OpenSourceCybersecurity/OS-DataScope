using DataScope.Persistence;
using Npgsql;

namespace DataScope.Worker;

/// <summary>Purges old history once a day (see <see cref="RetentionOptions"/>). Starts a few minutes after the service.</summary>
public sealed partial class RetentionWorker(
    IServiceScopeFactory scopes, RetentionOptions options, ILogger<RetentionWorker> log, TimeProvider clock, Func<RetentionOptions>? current = null) : BackgroundService
{
    /// <summary>Read before each run, so changes on System settings apply at the next clear-up (D123).</summary>
    private RetentionOptions Options => current?.Invoke() ?? options;

    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = StartDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
                await RunOnceAsync(stoppingToken);
                delay = TimeSpan.FromHours(Options.IntervalHours);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // Try again in an hour; nothing is lost by purging late.
                LogFailed(log, ex.GetType().Name, ex.Message);
                delay = TimeSpan.FromHours(1);
            }
        }
    }

    public async Task<RetentionResult> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<RetentionStore>().PurgeAsync(Options, ct);
        LogPurged(log, result.Files, result.Folders, result.PermissionChanges, result.ScanGaps, result.Notifications, result.ScanHistory);
        return result;
    }

    [LoggerMessage(LogLevel.Information, "Retention: removed {Files} files, {Folders} folders, {Changes} permission changes, {Gaps} old scan gaps, {Notifications} emails and {History} scan totals")]
    private static partial void LogPurged(ILogger logger, long files, long folders, long changes, long gaps, long notifications, long history);

    [LoggerMessage(LogLevel.Warning, "Retention did not run ({ExceptionType}: {Message}); trying again in an hour")]
    private static partial void LogFailed(ILogger logger, string exceptionType, string message);
}
