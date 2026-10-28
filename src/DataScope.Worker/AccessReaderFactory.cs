using DataScope.ActiveDirectory;
using DataScope.Core.Activity;
using DataScope.Scanner.Activity;
using DataScope.Scanner.Security;

namespace DataScope.Worker;

/// <summary>Picks the permission and directory readers, with the same Development-only lab rules as share listing.</summary>
public static class AccessReaderFactory
{
    /// <summary>
    /// Windows reads real permissions; <c>Scanner:LabRoot</c> uses the lab's permission file. Elsewhere, or with
    /// <c>Scanner:Permissions:Enabled</c> false, scans record no permissions.
    /// </summary>
    public static ISecurityReader? CreateSecurityReader(IConfiguration config, IHostEnvironment env)
    {
        if (!config.GetValue("Scanner:Permissions:Enabled", true))
        {
            return null;
        }
        var labRoot = config["Scanner:LabRoot"];
        if (!string.IsNullOrWhiteSpace(labRoot))
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Scanner:LabRoot is only allowed when the environment is Development.");
            }
            return new LabSecurityReader(Path.GetFullPath(labRoot, env.ContentRootPath));
        }
        return OperatingSystem.IsWindows() ? new WindowsSecurityReader() : null;
    }

    /// <summary>
    /// <c>Directory:LabFile</c> (Development only) reads a synthetic domain. Otherwise LDAP: on Windows by default,
    /// elsewhere only when a server is configured. Null means directory sync is not available on this host.
    /// </summary>
    public static IDirectoryReader? CreateDirectoryReader(IConfiguration config, IHostEnvironment env)
    {
        var section = config.GetSection("Directory");
        if (!section.GetValue("Enabled", true))
        {
            return null;
        }
        var labFile = section["LabFile"];
        if (!string.IsNullOrWhiteSpace(labFile))
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Directory:LabFile is only allowed when the environment is Development.");
            }
            return new LabDirectoryReader(Path.GetFullPath(labFile, env.ContentRootPath));
        }
        var options = new LdapDirectoryOptions();
        section.Bind(options);
        if (options.AllowUnencryptedPassword && !env.IsDevelopment())
        {
            throw new InvalidOperationException("Directory:AllowUnencryptedPassword is only allowed when the environment is Development.");
        }
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(options.Server))
        {
            return null;
        }
        options.Validate();
        return new LdapDirectoryReader(options);
    }

    /// <summary>
    /// <c>Scanner:LabRoot</c> (Development only) reads the lab's synthetic Security logs. On Windows, file servers'
    /// Security logs are read remotely (D102). Elsewhere there is no source and the collector says so on each server.
    /// </summary>
    public static ISecurityEventSource? CreateEventSource(IConfiguration config, IHostEnvironment env)
    {
        if (!config.GetValue("Activity:Enabled", true))
        {
            return null;
        }
        var labRoot = config["Scanner:LabRoot"];
        if (!string.IsNullOrWhiteSpace(labRoot))
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Scanner:LabRoot is only allowed when the environment is Development.");
            }
            return new LabSecurityEventSource(Path.GetFullPath(labRoot, env.ContentRootPath));
        }
        return OperatingSystem.IsWindows()
            ? new WindowsSecurityEventSource(TimeSpan.FromSeconds(config.GetValue("Activity:ReadTimeoutSeconds", 120)))
            : null;
    }
}
