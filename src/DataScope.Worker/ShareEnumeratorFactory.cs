using DataScope.Scanner;

namespace DataScope.Worker;

public static class ShareEnumeratorFactory
{
    /// <summary>
    /// Production uses Windows share enumeration. <c>Scanner:LabRoot</c> swaps in a folder-based fake
    /// file server, and is refused outside Development so it can never point a real install at local folders.
    /// </summary>
    public static IShareEnumerator Create(IConfiguration config, IHostEnvironment env)
    {
        var labRoot = config["Scanner:LabRoot"];
        if (!string.IsNullOrWhiteSpace(labRoot))
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Scanner:LabRoot is only allowed when the environment is Development.");
            }
            return new LabShareEnumerator(Path.GetFullPath(labRoot, env.ContentRootPath));
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsShareEnumerator();
        }
        throw new PlatformNotSupportedException("Listing SMB shares needs Windows. For development on another OS, set Scanner:LabRoot.");
    }
}
