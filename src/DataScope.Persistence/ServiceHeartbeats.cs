using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataScope.Persistence;

/// <summary>
/// Writes this service's row in <c>service_heartbeats</c> every minute, so System health can say whether the scanner
/// and the web service are running and since when. A failed write is logged and tried again; it never stops the service.
/// Each service runs <see cref="RunAsync"/> from a hosted service of its own.
/// </summary>
public sealed partial class ServiceHeartbeatWriter(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ServiceHeartbeatWriter> log, string service, string version)
{
    public const string Web = "Web";
    public const string Scanner = "Scanner";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly DateTimeOffset _started = clock.GetUtcNow();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BeatAsync(stoppingToken);
            }
            catch (Exception ex) when (DatabaseOutage.Is(ex))
            {
                LogFailed(ex.Message);
            }
            await Task.Delay(Interval, clock, stoppingToken);
        }
    }

    public async Task BeatAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataScopeDbContext>();
        var machine = Environment.MachineName;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO service_heartbeats (service, machine, version, started_at, seen_at) VALUES ({service}, {machine}, {version}, {_started}, {now})
            ON CONFLICT (service) DO UPDATE SET machine = EXCLUDED.machine, version = EXCLUDED.version, started_at = EXCLUDED.started_at, seen_at = EXCLUDED.seen_at
            """, ct);
    }

    [LoggerMessage(LogLevel.Warning, "Could not record that this service is running ({Message}); trying again in a minute")]
    private partial void LogFailed(string message);
}

public static class ServiceHeartbeatSetup
{
    /// <summary>Registers this service's writer; the service's own hosted service runs it.</summary>
    public static IServiceCollection AddServiceHeartbeat(this IServiceCollection services, string service, string version) =>
        services.AddSingleton(sp => new ServiceHeartbeatWriter(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ServiceHeartbeatWriter>>(), service, version));
}
