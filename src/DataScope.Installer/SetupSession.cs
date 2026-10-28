using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace DataScope.Installer;

/// <summary>A certificate the web UI can use, as listed in the wizard.</summary>
internal sealed class CertificateChoice(string? thumbprint, string label)
{
    public string? Thumbprint { get; } = thumbprint;
    public override string ToString() => label;
}

/// <summary>Everything the wizard and the quiet mode ask of Windows: the package, this server, and the installer run.</summary>
internal sealed class SetupSession : IDisposable
{
    private const string AppsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataScope";
    private string? _tempFolder;

    public SetupSession()
    {
        ProgramPath = Assembly.GetEntryAssembly()!.Location;
    }

    /// <summary>This setup program.</summary>
    public string ProgramPath { get; }

    /// <summary>The unzipped package, once <see cref="Prepare"/> has run.</summary>
    public string? PackageFolder { get; private set; }

    public string Version => ReadFirstLine(Path.Combine(PackageFolder ?? "", "VERSION")) ?? "unknown";

    public string? BundledDatabaseVersion =>
        PackageFolder is null || !File.Exists(Path.Combine(PackageFolder, @"app\postgresql\bin\postgres.exe"))
            ? null
            : ReadFirstLine(Path.Combine(PackageFolder, @"app\postgresql\VERSION")) ?? "";

    public static string InstallLogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"DataScope\logs");

    /// <summary>
    /// Finds the package: the zip appended to this program (unzipped into a temporary folder, removed when setup
    /// closes), or the unzipped package this program sits in.
    /// </summary>
    public void Prepare(Action<double>? progress = null)
    {
        using (var self = File.OpenRead(ProgramPath))
        {
            if (Payload.Find(self) is { } payload)
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), "DataScope-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                using var zip = new SubStream(self, payload.Offset, payload.Length);
                PackageFolder = PackageExtractor.Extract(zip, _tempFolder, progress);
                return;
            }
        }
        PackageFolder = PackageExtractor.FindPackageFolder(Path.GetDirectoryName(ProgramPath)!)
            ?? throw new InvalidOperationException(
                "This setup program has no Data Scope package inside it, and it is not in an unzipped package folder. Download DataScope-Setup again.");
        progress?.Invoke(1);
    }

    /// <summary>Data Scope's web service exists here, so setup upgrades in place.</summary>
    public static bool IsInstalled() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, "DataScopeApi", StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed version, from Settings, Apps; null for an installation from before setup listed it there.</summary>
    public static string? InstalledVersion()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(AppsKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    /// <summary>Whether this program carries a code signature, in which case the package it carries must be signed too.</summary>
    public bool IsSigned()
    {
        try
        {
            return X509Certificate.CreateFromSignedFile(ProgramPath) is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>DOMAIN\Name for an account or group that exists, else null. Finds names given without a domain too.</summary>
    public static string? FindAccount(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        try
        {
            var sid = new NTAccount(name.Trim()).Translate(typeof(SecurityIdentifier));
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>The first suggestion that exists here, as DOMAIN\Name.</summary>
    public static string? Suggest(params string[] names) => names.Select(FindAccount).FirstOrDefault(n => n is not null);

    /// <summary>This server's DNS name, as the installer would choose it.</summary>
    public static string DefaultHostName()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var name = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}";
        return name.ToLowerInvariant();
    }

    /// <summary>Certificates in the computer's Personal store that can serve HTTPS: with a private key, not expired.</summary>
    public static IReadOnlyList<CertificateChoice> Certificates()
    {
        var choices = new List<CertificateChoice> { new(null, "Create a self-signed certificate (fine for a trial)") };
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates.Cast<X509Certificate2>()
                .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now && c.FriendlyName != "Data Scope (self-signed)")
                .OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase))
            {
                var name = cert.GetNameInfo(X509NameType.DnsName, false);
                choices.Add(new(cert.Thumbprint, string.Format(CultureInfo.CurrentCulture, "{0}, valid until {1:yyyy-MM-dd} ({2})",
                    string.IsNullOrEmpty(name) ? cert.Subject : name, cert.NotAfter, cert.Thumbprint)));
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // An unreadable store leaves only the self-signed choice.
        }
        return choices;
    }

    /// <summary>
    /// Runs the installer, passing each line it prints to <paramref name="onLine"/> (true for error output).
    /// Returns its exit code: 0 when Data Scope is installed and running.
    /// </summary>
    public static int Run(InstallCommand command, Action<string, bool> onLine)
    {
        var start = new ProcessStartInfo(command.FileName, command.CommandLine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
            // Windows PowerShell writes to a pipe in the console's OEM code page.
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        };
        foreach (var variable in command.Environment)
        {
            start.EnvironmentVariables[variable.Key] = variable.Value;
        }
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { onLine(e.Data, false); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { onLine(e.Data, true); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>The backup key of the installation, which the person should keep somewhere safe.</summary>
    public static string ReadBackupKey()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Data Scope");
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"DataScope\config\datascope.json");
        var start = new ProcessStartInfo(Path.Combine(programs, @"setup\DataScope.Setup.exe"),
            WindowsCommandLine.Join(["backup-key", "--config", config]))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException(error.Trim().Length > 0 ? error.Trim() : output.Trim());
    }

    public void Dispose()
    {
        if (_tempFolder is null)
        {
            return;
        }
        try
        {
            Directory.Delete(_tempFolder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for Windows' temporary file clean-up.
        }
    }

    private static string? ReadFirstLine(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
}
