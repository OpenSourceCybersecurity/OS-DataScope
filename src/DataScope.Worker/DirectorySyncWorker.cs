using DataScope.ActiveDirectory;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Worker;

/// <summary>
/// Runs directory syncs: one a day automatically (Directory:SyncHours), plus any an admin asks for. Each sync reads
/// the whole domain read-only and stores it as a new snapshot; a failed sync never replaces the last good one.
/// </summary>
public sealed partial class DirectorySyncWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<DirectorySyncWorker> log, IDirectoryReader? reader = null) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}/{Environment.ProcessId}/dir/{Guid.NewGuid():N}";
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(config.GetValue("Scanner:LeaseSeconds", 60));
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(config.GetValue("Scanner:PollSeconds", 5));
    /// <summary>Read on every pass, so a change on System settings applies to the next sync (D123).</summary>
    private TimeSpan Interval => TimeSpan.FromHours(config.GetValue("Directory:SyncHours", 24.0));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                LogDatabaseUnavailable(ex);
                await Task.Delay(_poll * 2, stoppingToken);
            }
        }
    }

    /// <summary>Queues the daily sync when due, then runs one queued sync. Returns false when there was nothing to do.</summary>
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DirectoryStore>();
        if (reader is not null && Interval is var interval && interval > TimeSpan.Zero)
        {
            await store.EnsureScheduledAsync(interval, ct);
        }
        var snapshot = await store.ClaimNextAsync(_workerId, _lease, ct);
        if (snapshot is null)
        {
            return false;
        }
        if (reader is null)
        {
            await store.FailAsync(snapshot.Id, _workerId, "Directory sync is not set up on this server. On Windows it uses the service account's domain; see the install guide.", retry: false, ct);
            return true;
        }

        LogStarted(snapshot.Id);
        using var renew = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewAsync(snapshot.Id, renew.Token);
        try
        {
            var result = await reader.ReadAsync(ct);
            await renew.CancelAsync();
            await renewal;
            await using var saveScope = scopes.CreateAsyncScope();
            var saved = await saveScope.ServiceProvider.GetRequiredService<DirectoryStore>().CompleteAsync(snapshot.Id, _workerId, result, ct);
            LogFinished(snapshot.Id, saved ? "completed" : "lease lost", result.Objects.Count);
        }
        catch (DirectoryReadException ex)
        {
            await renew.CancelAsync();
            await renewal;
            LogFailed(snapshot.Id, ex.Message);
            await store.FailAsync(snapshot.Id, _workerId, ex.Message, retry: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await renew.CancelAsync();
            await renewal;
            LogFailed(snapshot.Id, ex.GetType().Name);
            await store.FailAsync(snapshot.Id, _workerId, $"The directory sync failed unexpectedly ({ex.GetType().Name}).", retry: true, ct);
        }
        return true;
    }

    private async Task RenewAsync(Guid id, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_lease / 3, ct);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DirectoryStore>().RenewLeaseAsync(id, _workerId, _lease, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Sync finished.
        }
    }

    [LoggerMessage(LogLevel.Information, "Directory sync {SnapshotId} started")]
    private partial void LogStarted(Guid snapshotId);

    [LoggerMessage(LogLevel.Information, "Directory sync {SnapshotId} {Outcome}: {Objects} objects")]
    private partial void LogFinished(Guid snapshotId, string outcome, int objects);

    [LoggerMessage(LogLevel.Warning, "Directory sync {SnapshotId} failed: {Reason}")]
    private partial void LogFailed(Guid snapshotId, string reason);

    [LoggerMessage(LogLevel.Warning, "Database unavailable; retrying")]
    private partial void LogDatabaseUnavailable(Exception ex);
}
