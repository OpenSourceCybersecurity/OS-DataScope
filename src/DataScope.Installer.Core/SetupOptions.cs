using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DataScope.Installer;

public enum DatabaseChoice
{
    /// <summary>The PostgreSQL that comes in the package, installed as the "Data Scope Database" service.</summary>
    Bundled,

    /// <summary>A PostgreSQL server the customer already runs.</summary>
    Existing,
}

/// <summary>The answers the wizard (or the command line) collects for a first install.</summary>
public sealed class SetupOptions
{
    public string ScanAccount { get; set; } = "";
    public string? ScanPassword { get; set; }
    public string AdminGroup { get; set; } = "";
    public DatabaseChoice Database { get; set; } = DatabaseChoice.Bundled;
    public string DatabaseHost { get; set; } = "localhost";
    public int DatabasePort { get; set; } = 5432;
    public string DatabaseAdminUser { get; set; } = "postgres";
    public string? DatabaseAdminPassword { get; set; }

    /// <summary>The name people type in the browser; the installer uses this server's DNS name when empty.</summary>
    public string? HostName { get; set; }

    public int HttpsPort { get; set; } = 443;

    /// <summary>A certificate in LocalMachine\My; the installer creates a self-signed one when empty.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>A group managed service account: its name ends with $ and it needs no password.</summary>
    public bool ScanAccountIsGmsa => ScanAccount.Trim().EndsWith("$", StringComparison.Ordinal);

    /// <summary>What is missing or wrong, in words for the person at the wizard. Empty when the answers can be used.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ScanAccount))
        {
            problems.Add("Type the scan account, such as YOURDOMAIN\\svc-datascope$.");
        }
        else if (!ScanAccountIsGmsa && string.IsNullOrEmpty(ScanPassword))
        {
            problems.Add("Type the scan account's password, or use a gMSA (a name that ends with $, which needs no password).");
        }
        if (string.IsNullOrWhiteSpace(AdminGroup))
        {
            problems.Add("Type the admin group, such as YOURDOMAIN\\DataScope-Admins.");
        }
        if (Database == DatabaseChoice.Existing)
        {
            if (string.IsNullOrWhiteSpace(DatabaseHost))
            {
                problems.Add("Type the PostgreSQL server's name, or localhost.");
            }
            if (!IsPort(DatabasePort))
            {
                problems.Add("The PostgreSQL port must be a number from 1 to 65535.");
            }
            if (string.IsNullOrWhiteSpace(DatabaseAdminUser))
            {
                problems.Add("Type the PostgreSQL admin login, usually postgres.");
            }
            if (string.IsNullOrEmpty(DatabaseAdminPassword))
            {
                problems.Add("Type the PostgreSQL admin password. It is used once to create the Data Scope database and is not saved.");
            }
        }
        if (!IsPort(HttpsPort))
        {
            problems.Add("The web port must be a number from 1 to 65535.");
        }
        if (!string.IsNullOrEmpty(CertificateThumbprint) && !IsThumbprint(CertificateThumbprint!))
        {
            problems.Add("The certificate thumbprint must be 40 letters and numbers (0-9, A-F).");
        }
        if (HostName is { Length: > 0 } name && name.Any(c => char.IsWhiteSpace(c) || c is '/' or ':'))
        {
            problems.Add("The web address name must be a server name such as datascope.yourdomain.local, without https:// or a port.");
        }
        return problems;
    }

    internal static bool IsPort(int port) => port is >= 1 and <= 65535;

    internal static bool IsThumbprint(string value) =>
        value.Length == 40 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    internal static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}
