using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Worker;

/// <summary>Claims scan jobs from the database queue and runs them one at a time.</summary>
public sealed partial class ScanWorker(
    IServiceScopeFactory scopes, IScanExecutor executor, IConfiguration config, ILogger<ScanWorker> log, TimeZoneInfo? zone = null) : BackgroundService
{
    /// <summary>Scan schedules use the Data Scope server's own time zone (D116).</summary>
    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;
    private readonly bool _schedules = config.GetValue("Scanner:Schedules", true);

    private readonly string _workerId = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(config.GetValue("Scanner:LeaseSeconds", 60));
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(config.GetValue("Scanner:PollSeconds", 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stoppingToken))
                {
                    await Task.Delay(_poll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (Persistence.DatabaseOutage.Is(ex))
            {
                // Database outage: back off and retry. Jobs keep their checkpoints.
                LogDatabaseUnavailable(ex);
                await Task.Delay(_poll * 2, stoppingToken);
            }
        }
    }

    /// <summary>Runs one job if one is available. Returns false when the queue is empty.</summary>
    public async Task<bool> RunOneAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ScanJobStore>();
        var db = scope.ServiceProvider.GetRequiredService<DataScopeDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        if (_schedules && await store.EnsureScheduledAsync(_zone, stoppingToken) is > 0 and var started)
        {
            LogScheduled(started);
        }
        var job = await store.ClaimNextAsync(_workerId, _lease, stoppingToken);
        if (job is null)
        {
            return false;
        }
        var server = await db.ManagedServers.Where(s => s.Id == job.ServerId)
            .Select(s => new { s.Hostname, s.State, Schedule = new ScanScheduleSettings(s.ScanFrequency, s.ScanStartHour, s.ScanDay, s.ScanStopHour, s.ScanStartMinute, s.ScanEveryHours) })
            .SingleAsync(stoppingToken);
        var hostname = server.Hostname;
        LogClaimed(job.Id, hostname, job.Attempts);
        if (server.State == Persistence.Entities.EnrollmentState.Retired)
        {
            // Removal cancels queued scans; this catches one queued in the same moment.
            await store.FinishAsync(job.Id, _workerId, ScanJobState.Cancelled, "The server was removed from Data Scope.", stoppingToken);
            LogFinished(job.Id, "cancelled: server removed");
            return true;
        }

        ScanControl result;
        var outsideWindow = false;
        try
        {
            result = await executor.RunAsync(new ScanContext(job.Id, hostname, job.Checkpoint, async (done, total, checkpoint, ct) =>
            {
                if (!await store.CheckpointAsync(job.Id, _workerId, done, total, checkpoint, _lease, ct))
                {
                    return ScanControl.LeaseLost;
                }
                var requested = await store.GetRequestedStateAsync(job.Id, ct);
                if (requested is null && job.KeepToWindow && await store.KeepsToWindowAsync(job.Id, ct)
                    && !ScanSchedule.InWindow(server.Schedule, clock.GetUtcNow(), _zone))
                {
                    outsideWindow = true;
                    return ScanControl.Pause;
                }
                return requested switch
                {
                    ScanJobState.Paused => ScanControl.Pause,
                    ScanJobState.Cancelled => ScanControl.Cancel,
                    _ => ScanControl.Continue,
                };
            }, job.Kind), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service is stopping: hand the job back so it resumes from its checkpoint.
            await store.FinishAsync(job.Id, _workerId, ScanJobState.Queued, "Worker stopped; will resume.", CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var retry = job.Attempts < ScanJobStore.MaxAttempts;
            LogJobFailed(ex, job.Id, retry);
            await store.FinishAsync(job.Id, _workerId, retry ? ScanJobState.Queued : ScanJobState.Failed, Redact(ex), stoppingToken);
            return true;
        }

        var final = result switch
        {
            ScanControl.Continue => ScanJobState.Completed,
            ScanControl.Pause => ScanJobState.Paused,
            ScanControl.Cancel => ScanJobState.Cancelled,
            _ => (ScanJobState?)null,
        };
        if (final == ScanJobState.Paused && outsideWindow)
        {
            await store.PauseForWindowAsync(job.Id, _workerId, stoppingToken);
            LogPausedForWindow(job.Id, hostname);
        }
        else if (final is { } state)
        {
            await store.FinishAsync(job.Id, _workerId, state, null, stoppingToken);
        }
        LogFinished(job.Id, final?.ToString() ?? "lease lost");
        return true;
    }

    /// <summary>Error text stored in the database: type and message only, never stack traces with paths or data.</summary>
    private static string Redact(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 500 ? text : text[..500];
    }

    [LoggerMessage(LogLevel.Information, "Scan worker {WorkerId} started")]
    private partial void LogStarted(string workerId);

    [LoggerMessage(LogLevel.Information, "Started or continued {Count} scheduled scans")]
    private partial void LogScheduled(int count);

    [LoggerMessage(LogLevel.Information, "Paused scheduled scan job {JobId} for {Hostname} at the end of its scan window")]
    private partial void LogPausedForWindow(Guid jobId, string hostname);

    [LoggerMessage(LogLevel.Information, "Claimed scan job {JobId} for {Hostname} (attempt {Attempt})")]
    private partial void LogClaimed(Guid jobId, string hostname, int attempt);

    [LoggerMessage(LogLevel.Information, "Scan job {JobId} ended: {State}")]
    private partial void LogFinished(Guid jobId, string state);

    [LoggerMessage(LogLevel.Error, "Scan job {JobId} failed (will retry: {Retry})")]
    private partial void LogJobFailed(Exception ex, Guid jobId, bool retry);

    [LoggerMessage(LogLevel.Warning, "Database unavailable; retrying")]
    private partial void LogDatabaseUnavailable(Exception ex);
}
