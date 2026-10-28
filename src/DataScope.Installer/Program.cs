using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace DataScope.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SetupArguments arguments;
        try
        {
            arguments = SetupArguments.Parse(args, Environment.GetEnvironmentVariable);
        }
        catch (SetupArgumentException e)
        {
            MessageBox.Show(e.Message + Environment.NewLine + Environment.NewLine + SetupArguments.Usage, "Data Scope setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        switch (arguments.Mode)
        {
            case SetupMode.Help:
                MessageBox.Show(SetupArguments.Usage, "Data Scope setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            case SetupMode.Extract:
                return Extract(arguments.ExtractTo!);
            case SetupMode.Quiet:
                return Quiet(arguments);
            default:
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var session = new SetupSession())
                {
                    Application.Run(new SetupForm(session));
                }
                return 0;
        }
    }

    private static int Extract(string folder)
    {
        using var session = new SetupSession();
        using var self = File.OpenRead(session.ProgramPath);
        if (Payload.Find(self) is not { } payload)
        {
            return 3;
        }
        using var zip = new SubStream(self, payload.Offset, payload.Length);
        PackageExtractor.Extract(zip, folder);
        return 0;
    }

    /// <summary>Installs or upgrades with no windows, writing what happened to the log. Returns the installer's exit code.</summary>
    private static int Quiet(SetupArguments arguments)
    {
        var logPath = arguments.LogPath ?? Path.Combine(Path.GetTempPath(), $"DataScope-Setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        using var session = new SetupSession();
        try
        {
            session.Prepare();
            var upgrade = SetupSession.IsInstalled();
            log.WriteLine($"Data Scope setup {session.Version}: {(upgrade ? "upgrading the installation on this server" : "first install")}");
            if (!upgrade)
            {
                if (arguments.Options.Database == DatabaseChoice.Bundled && session.BundledDatabaseVersion is null)
                {
                    throw new InvalidOperationException("This setup has no PostgreSQL inside it. Use --database existing.");
                }
                IReadOnlyList<string> problems = arguments.Options.Problems();
                if (problems.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", problems));
                }
            }
            var command = InstallCommand.Build(SetupSession.PowerShellPath, session.PackageFolder!, arguments.Options, upgrade, session.IsSigned());
            var code = SetupSession.Run(command, (line, _) => log.WriteLine(line));
            log.WriteLine(code == 0 ? "Setup finished." : $"Setup stopped (exit code {code}).");
            return code;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.WriteLine("Setup stopped: " + e.Message);
            return 1;
        }
    }
}
