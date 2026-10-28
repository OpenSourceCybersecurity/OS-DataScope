using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class InstallCommandTests
{
    private const string PowerShell = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
    private const string Package = "C:\\Temp\\DataScope-1.0.0";

    private static SetupOptions Answers() => new()
    {
        ScanAccount = "LAB\\svc-datascope$",
        AdminGroup = "LAB\\DataScope-Admins",
        HostName = "ds01.lab.local",
    };

    [Fact]
    public void A_first_install_with_the_bundled_database_passes_the_answers_and_never_asks()
    {
        var command = InstallCommand.Build(PowerShell, Package, Answers(), isUpgrade: false, requireSignature: false);

        Assert.Equal(PowerShell, command.FileName);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Package, "Install-DataScope.ps1"),
             "-ScanAccount", "LAB\\svc-datascope$", "-AdminGroup", "LAB\\DataScope-Admins", "-InstallDatabase",
             "-Port", "443", "-HostName", "ds01.lab.local"],
            command.Arguments);
        Assert.Empty(command.Environment);
    }

    [Fact]
    public void Passwords_go_in_the_environment_never_on_the_command_line()
    {
        var answers = Answers();
        answers.ScanAccount = "LAB\\svc-datascope";
        answers.ScanPassword = "Scan p@ss \"1\"";
        answers.Database = DatabaseChoice.Existing;
        answers.DatabaseHost = "sql01.lab.local";
        answers.DatabasePort = 5433;
        answers.DatabaseAdminUser = "pgadmin";
        answers.DatabaseAdminPassword = "Db p@ss";

        var command = InstallCommand.Build(PowerShell, Package, answers, isUpgrade: false, requireSignature: false);

        Assert.DoesNotContain("Scan p@ss", command.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Db p@ss", command.CommandLine, StringComparison.Ordinal);
        Assert.Equal("Scan p@ss \"1\"", command.Environment[InstallCommand.ScanPasswordVariable]);
        Assert.Equal("pgadmin", command.Environment[InstallCommand.DatabaseAdminUserVariable]);
        Assert.Equal("Db p@ss", command.Environment[InstallCommand.DatabaseAdminPasswordVariable]);
        Assert.DoesNotContain("-InstallDatabase", command.Arguments);
        Assert.Contains("sql01.lab.local", command.Arguments);
        Assert.Contains("5433", command.Arguments);
    }

    [Fact]
    public void A_gmsa_never_sends_a_password_even_if_one_was_typed()
    {
        var answers = Answers();
        answers.ScanPassword = "typed by mistake";

        var command = InstallCommand.Build(PowerShell, Package, answers, isUpgrade: false, requireSignature: false);

        Assert.False(command.Environment.ContainsKey(InstallCommand.ScanPasswordVariable));
    }

    [Fact]
    public void An_upgrade_passes_no_answers_so_every_setting_is_kept()
    {
        var command = InstallCommand.Build(PowerShell, Package, Answers(), isUpgrade: true, requireSignature: true);

        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Package, "Install-DataScope.ps1"),
             "-RequireSignature"],
            command.Arguments);
        Assert.Empty(command.Environment);
    }

    [Fact]
    public void A_chosen_certificate_and_port_are_passed()
    {
        var answers = Answers();
        answers.HttpsPort = 8443;
        answers.CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

        var command = InstallCommand.Build(PowerShell, Package, answers, isUpgrade: false, requireSignature: false);

        Assert.Contains("-CertificateThumbprint", command.Arguments);
        Assert.Contains("0123456789ABCDEF0123456789ABCDEF01234567", command.Arguments);
        Assert.Equal("8443", command.Arguments[command.Arguments.ToList().IndexOf("-Port") + 1]);
    }
}
