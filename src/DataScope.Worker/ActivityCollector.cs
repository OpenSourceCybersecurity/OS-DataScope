using DataScope.Core.Activity;
using DataScope.Persistence;

namespace DataScope.Worker;

/// <summary>
/// Collects file activity from the Security log of each server where an admin turned it on. Each pass reads at most
/// <c>Activity:MaxEventsPerPass</c> events in batches of <c>Activity:BatchSize</c>, stores each batch together with the
/// new position, then waits <c>Activity:PollSeconds</c>. A failed read keeps the position, so nothing is skipped, and
/// the same events read twice are stored once (D96, D98).
/// </summary>
public sealed partial class ActivityCollector(
    IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<ActivityCollector> log, ISecurityEventSource? source = null,
    ActivityAlertOptions? alerts = null, Func<ActivityAlertOptions>? currentAlerts = null) : BackgroundService
{
    public const string NotAvailable = "Reading Security logs needs Data Scope's scanner service to run on Windows.";

    private readonly string _workerId = $"{Environment.MachineName}/{Environment.ProcessId}/activity/{Guid.NewGuid():N}";
    private readonly TimeSpan _lease = TimeSpan.FromSeconds(config.GetValue("Scanner:LeaseSeconds", 60));
    private readonly TimeSpan _idle = TimeSpan.FromSeconds(config.GetValue("Scanner:PollSeconds", 5));
    private readonly TimeSpan _interval = Positive(TimeSpan.FromSeconds(config.GetValue("Activity:PollSeconds", 60)), "Activity:PollSeconds");
    private readonly int _batch = Positive(config.GetValue("Activity:BatchSize", 500), "Activity:BatchSize");
    private readonly int _maxPerPass = Positive(config.GetValue("Activity:MaxEventsPerPass", 20_000), "Activity:MaxEventsPerPass");
    private readonly int _maxPerSecond = config.GetValue("Activity:MaxEventsPerSecond", 0);
    private readonly ActivityAlertOptions _alerts = alerts ?? new ActivityAlertOptions();

    /// <summary>Read on every pass, so changes on System settings apply within a minute (D123).</summary>
    private TimeSpan Interval => config.GetValue("Activity:PollSeconds", 60) is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : _interval;

    private ActivityAlertOptions Alerts => currentAlerts?.Invoke() ?? _alerts;

    private static T Positive<T>(T value, string name) where T : IComparable<T> =>
        value.CompareTo(default!) > 0 ? value : throw new InvalidOperationException($"{name} must be greater than zero.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stoppingToken))
                {
                    await Task.Delay(_idle, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (Persistence.DatabaseOutage.Is(ex))
            {
                LogDatabaseUnavailable(ex);
                await Task.Delay(_idle * 2, clock, stoppingToken);
            }
        }
    }

    /// <summary>Reads one due source. Returns false when nothing was due.</summary>
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ActivityStore>();
        var claimed = await store.ClaimNextAsync(_workerId, _lease, ct);
        if (claimed is null)
        {
            return false;
        }
        if (source is null)
        {
            await store.ReleaseAsync(claimed.ServerId, claimed.Channel, _workerId, clock.GetUtcNow() + Interval, NotAvailable, ct);
            return true;
        }

        string? error = null;
        var read = 0;
        var stored = 0;
        try
        {
            await store.CatchUpAsync(claimed.ServerId, claimed.Channel, ct);
            // The log's range first: records that disappeared since the last pass are a gap, never silently skipped.
            var log = await source.GetLogInfoAsync(claimed.Hostname, ct);
            var check = await store.CheckLogAsync(claimed.ServerId, claimed.Channel, log, ct);
            if (check.Missed is { } missed)
            {
                LogGap(claimed.Hostname, missed.Reason, missed.FirstRecordId, missed.LastRecordId);
            }
            var after = check.ReadAfter;
            var pacer = new Pacer(_maxPerSecond, clock);
            var caughtUp = false;
            while (read < _maxPerPass)
            {
                var size = Math.Min(_batch, _maxPerPass - read);
                var batch = await source.ReadAsync(claimed.Hostname, after, size, ct);
                if (batch.Count == 0)
                {
                    caughtUp = true;
                    break;
                }
                await using var batchScope = scopes.CreateAsyncScope();
                var result = await batchScope.ServiceProvider.GetRequiredService<ActivityStore>().IngestAsync(claimed.ServerId, claimed.Channel, batch, ct);
                read += batch.Count;
                stored += result.Stored;
                after = result.LastRecordId;
                await store.RenewLeaseAsync(claimed.ServerId, claimed.Channel, _workerId, _lease, ct);
                if (batch.Count < size)
                {
                    caughtUp = true;
                    break;
                }
                await PaceAsync(pacer, batch.Count, ct);
            }
            if (caughtUp && log.NewestRecordId is { } newest)
            {
                await store.MarkCaughtUpAsync(claimed.ServerId, claimed.Channel, check.LogGeneration, newest, ct);
            }
            await using var alertScope = scopes.CreateAsyncScope();
            var raised = await alertScope.ServiceProvider.GetRequiredService<ActivityAlertStore>()
                .DetectAsync(claimed.ServerId, claimed.Channel, Alerts, config["Notifications:PublicUrl"], ct);
            // Each person against their own past (D166), once the day's events are in.
            var unusual = await alertScope.ServiceProvider.GetRequiredService<UnusualActivityStore>()
                .CheckAsync(claimed.ServerId, Alerts, TimeZoneInfo.Local, config["Notifications:PublicUrl"], ct);
            foreach (var alert in raised.Concat(unusual))
            {
                LogAlert(claimed.Hostname, alert.Rule, alert.EventCount);
            }
        }
        catch (SecurityEventReadException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Npgsql.NpgsqlException and not Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            error = $"Reading the Security log failed unexpectedly ({ex.GetType().Name}).";
        }
        // A full pass means more is waiting: come back straight away instead of after the interval.
        var next = error is null && read >= _maxPerPass ? clock.GetUtcNow() : clock.GetUtcNow() + Interval;
        await store.ReleaseAsync(claimed.ServerId, claimed.Channel, _workerId, next, error, ct);
        if (error is null)
        {
            LogRead(claimed.Hostname, read, stored);
        }
        else
        {
            LogFailed(claimed.Hostname, error);
        }
        return true;
    }

    /// <summary>Holds reading to <c>Activity:MaxEventsPerSecond</c> on average (0: no limit), to spare busy servers.</summary>
    private async Task PaceAsync(Pacer pacer, int events, CancellationToken ct)
    {
        if (!pacer.Enabled)
        {
            return;
        }
        var now = clock.GetElapsedTime(0);
        var wait = TimeSpan.Zero;
        for (var i = 0; i < events; i++)
        {
            wait = pacer.Next(now);
        }
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, clock, ct);
        }
    }

    [LoggerMessage(LogLevel.Warning, "File activity from {Host}: log records were gone before they were read ({Reason}, from record {First} to {Last})")]
    private partial void LogGap(string host, ActivityGapReason reason, long first, long? last);

    [LoggerMessage(LogLevel.Warning, "File activity alert on {Host}: {Rule} ({Count} events)")]
    private partial void LogAlert(string host, ActivityAlertRule rule, int count);

    [LoggerMessage(LogLevel.Information, "File activity from {Host}: {Read} events read, {Stored} stored")]
    private partial void LogRead(string host, int read, int stored);

    [LoggerMessage(LogLevel.Warning, "File activity from {Host} failed: {Reason}")]
    private partial void LogFailed(string host, string reason);

    [LoggerMessage(LogLevel.Warning, "Database unavailable; retrying")]
    private partial void LogDatabaseUnavailable(Exception ex);
}
