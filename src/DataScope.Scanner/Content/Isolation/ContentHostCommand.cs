using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>How the worker starts its reader processes: itself, with the <see cref="ContentHost.Argument"/> argument.</summary>
public static class ContentHostCommand
{
    /// <param name="workerAssembly">The path of the worker's main assembly, DataScope.Worker.dll.</param>
    public static ProcessStartInfo For(string workerAssembly)
    {
        ArgumentException.ThrowIfNullOrEmpty(workerAssembly);
        var self = Environment.ProcessPath;
        ProcessStartInfo start;
        if (self is not null && string.Equals(Path.GetFileNameWithoutExtension(self), Path.GetFileNameWithoutExtension(workerAssembly), StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo(self); // DataScope.Worker.exe, as installed.
        }
        else
        {
            start = new ProcessStartInfo(DotnetHost()); // Run with "dotnet", as in development and tests.
            start.ArgumentList.Add(workerAssembly);
        }
        start.ArgumentList.Add(ContentHost.Argument);
        return start;
    }

    /// <summary>The dotnet host that runs this process: the runtime lives in &lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;.</summary>
    private static string DotnetHost()
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(host) ? host : "dotnet";
    }
}
