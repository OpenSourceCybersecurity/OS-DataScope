using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace DataScope.Worker;

internal static class WindowsHosting
{
    /// <summary>Application event log source; the installer registers it.</summary>
    public const string EventSource = "Data Scope Scanner";

    [SupportedOSPlatform("windows")]
    public static void AddWindowsServiceWithEventLog(IServiceCollection services)
    {
        services.AddWindowsService(o => o.ServiceName = EventSource);
        services.Configure<EventLogSettings>(SetSource);
    }

    [SupportedOSPlatform("windows")]
    private static void SetSource(EventLogSettings settings) => settings.SourceName = EventSource;
}
