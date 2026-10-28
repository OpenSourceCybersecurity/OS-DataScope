using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace DataScope.Api.Hosting;

internal static class WindowsServiceExtensions
{
    /// <summary>Application event log source; the installer registers it.</summary>
    public const string EventSource = "Data Scope API";

    /// <summary>Runs as a Windows Service when started by the Service Control Manager; a no-op elsewhere.</summary>
    public static IHostBuilder UseWindowsServiceIfAvailable(this IHostBuilder host) =>
        OperatingSystem.IsWindows() ? UseWindowsServiceWithEventLog(host) : host;

    [SupportedOSPlatform("windows")]
    private static IHostBuilder UseWindowsServiceWithEventLog(IHostBuilder host) =>
        host.UseWindowsService(o => o.ServiceName = EventSource)
            .ConfigureServices(s => s.Configure<EventLogSettings>(SetSource));

    [SupportedOSPlatform("windows")]
    private static void SetSource(EventLogSettings settings) => settings.SourceName = EventSource;
}
