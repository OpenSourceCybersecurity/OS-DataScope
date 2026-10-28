using System.Collections.Generic;
using System.IO;

namespace DataScope.Installer;

/// <summary>
/// How the wizard runs the package's Install-DataScope.ps1: Windows PowerShell 5.1, never asking questions
/// (-NonInteractive), with the answers as parameters. Passwords go in the environment of that one process, never
/// on its command line, where other programs on the server could read them; the script removes them once read.
/// </summary>
public sealed class InstallCommand
{
    public const string ScanPasswordVariable = "DATASCOPE_INSTALL_SCAN_PASSWORD";
    public const string DatabaseAdminUserVariable = "DATASCOPE_INSTALL_DB_ADMIN_USER";
    public const string DatabaseAdminPasswordVariable = "DATASCOPE_INSTALL_DB_ADMIN_PASSWORD";

    private InstallCommand(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        FileName = fileName;
        Arguments = arguments;
        Environment = environment;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Arguments { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public string CommandLine => WindowsCommandLine.Join(Arguments);

    /// <param name="powerShell">Full path of powershell.exe (Windows PowerShell 5.1).</param>
    /// <param name="packageFolder">The unzipped package: the folder with Install-DataScope.ps1.</param>
    /// <param name="options">The answers; ignored on an upgrade, which keeps every setting.</param>
    /// <param name="isUpgrade">Data Scope is already installed here.</param>
    /// <param name="requireSignature">Refuse a package whose programs are not code-signed.</param>
    public static InstallCommand Build(string powerShell, string packageFolder, SetupOptions options, bool isUpgrade, bool requireSignature)
    {
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(packageFolder, "Install-DataScope.ps1"),
        };
        var environment = new Dictionary<string, string>();
        if (!isUpgrade)
        {
            arguments.AddRange(["-ScanAccount", options.ScanAccount.Trim(), "-AdminGroup", options.AdminGroup.Trim()]);
            if (!options.ScanAccountIsGmsa && !string.IsNullOrEmpty(options.ScanPassword))
            {
                environment[ScanPasswordVariable] = options.ScanPassword!;
            }
            if (options.Database == DatabaseChoice.Bundled)
            {
                arguments.Add("-InstallDatabase");
            }
            else
            {
                arguments.AddRange(["-DatabaseHost", options.DatabaseHost.Trim(), "-DatabasePort", SetupOptions.Port(options.DatabasePort)]);
                environment[DatabaseAdminUserVariable] = options.DatabaseAdminUser.Trim();
                environment[DatabaseAdminPasswordVariable] = options.DatabaseAdminPassword ?? "";
            }
            arguments.AddRange(["-Port", SetupOptions.Port(options.HttpsPort)]);
            if (!string.IsNullOrWhiteSpace(options.HostName))
            {
                arguments.AddRange(["-HostName", options.HostName!.Trim()]);
            }
            if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
            {
                arguments.AddRange(["-CertificateThumbprint", options.CertificateThumbprint!.Trim()]);
            }
        }
        if (requireSignature)
        {
            arguments.Add("-RequireSignature");
        }
        return new InstallCommand(powerShell, arguments, environment);
    }
}
