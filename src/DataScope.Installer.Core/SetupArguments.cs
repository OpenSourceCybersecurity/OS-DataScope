using System;
using System.Collections.Generic;
using System.Globalization;

namespace DataScope.Installer;

public enum SetupMode
{
    /// <summary>The wizard (no arguments).</summary>
    Wizard,

    /// <summary>No windows: install or upgrade with the answers on the command line, for IT providers and CI.</summary>
    Quiet,

    /// <summary>Only unzip the package into a folder.</summary>
    Extract,

    Help,
}

public sealed class SetupArgumentException(string message) : Exception(message);

/// <summary>DataScope-Setup.exe's command line. Passwords are never on it: they come from the environment.</summary>
public sealed class SetupArguments
{
    public const string Usage = """
        DataScope-Setup.exe                     Opens the setup wizard.
        DataScope-Setup.exe /quiet [options]    Installs or upgrades with no windows. Exit code 0 when it worked.
          --scan-account DOMAIN\name            First install: the scan account (a gMSA ends with $).
          --admin-group DOMAIN\group            First install: the group that administers Data Scope.
          --database bundled|existing           bundled (the default) installs the PostgreSQL that comes with Data Scope.
          --db-host, --db-port, --db-admin-user With --database existing (the admin login defaults to postgres).
          --host-name, --port, --certificate    The web address, its port (443) and a certificate thumbprint.
          --log path                            Where to write what happened (default: a file in %TEMP%).
          Passwords come from the environment: DATASCOPE_INSTALL_SCAN_PASSWORD for a scan account that is not
          a gMSA, DATASCOPE_INSTALL_DB_ADMIN_PASSWORD with --database existing.
        DataScope-Setup.exe /extract folder     Only unzips the package into the folder.
        """;

    public SetupMode Mode { get; private set; } = SetupMode.Wizard;
    public SetupOptions Options { get; } = new();
    public string? ExtractTo { get; private set; }
    public string? LogPath { get; private set; }

    public static SetupArguments Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var parsed = new SetupArguments();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new SetupArgumentException($"{name} needs a value.");
                }
                return args[++i];
            }
            switch (name.ToLowerInvariant())
            {
                case "/quiet" or "--quiet" or "/q":
                    parsed.Mode = SetupMode.Quiet;
                    break;
                case "/extract" or "--extract":
                    parsed.Mode = SetupMode.Extract;
                    parsed.ExtractTo = Value();
                    break;
                case "/?" or "-h" or "--help" or "/help":
                    parsed.Mode = SetupMode.Help;
                    break;
                case "--scan-account":
                    parsed.Options.ScanAccount = Value();
                    break;
                case "--admin-group":
                    parsed.Options.AdminGroup = Value();
                    break;
                case "--database":
                    parsed.Options.Database = Value().ToLowerInvariant() switch
                    {
                        "bundled" => DatabaseChoice.Bundled,
                        "existing" => DatabaseChoice.Existing,
                        _ => throw new SetupArgumentException("--database must be bundled or existing."),
                    };
                    break;
                case "--db-host":
                    parsed.Options.DatabaseHost = Value();
                    break;
                case "--db-port":
                    parsed.Options.DatabasePort = Number(name, Value());
                    break;
                case "--db-admin-user":
                    parsed.Options.DatabaseAdminUser = Value();
                    break;
                case "--host-name":
                    parsed.Options.HostName = Value();
                    break;
                case "--port":
                    parsed.Options.HttpsPort = Number(name, Value());
                    break;
                case "--certificate":
                    parsed.Options.CertificateThumbprint = Value();
                    break;
                case "--log":
                    parsed.LogPath = Value();
                    break;
                default:
                    throw new SetupArgumentException($"Unknown option {name}. DataScope-Setup.exe /? lists the options.");
            }
        }
        parsed.Options.ScanPassword = environment(InstallCommand.ScanPasswordVariable);
        parsed.Options.DatabaseAdminPassword = environment(InstallCommand.DatabaseAdminPasswordVariable);
        return parsed;
    }

    private static int Number(string name, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && SetupOptions.IsPort(number)
            ? number
            : throw new SetupArgumentException($"{name} must be a port number from 1 to 65535.");
}
