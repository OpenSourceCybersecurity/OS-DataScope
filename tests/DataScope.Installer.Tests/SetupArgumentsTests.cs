using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class SetupArgumentsTests
{
    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void No_arguments_open_the_wizard()
    {
        Assert.Equal(SetupMode.Wizard, SetupArguments.Parse([], NoEnvironment).Mode);
    }

    [Fact]
    public void Quiet_mode_takes_the_answers_and_reads_passwords_from_the_environment()
    {
        var environment = new Dictionary<string, string>
        {
            [InstallCommand.ScanPasswordVariable] = "scan secret",
            [InstallCommand.DatabaseAdminPasswordVariable] = "db secret",
        };
        var parsed = SetupArguments.Parse(
            ["/quiet", "--scan-account", "LAB\\svc", "--admin-group", "LAB\\Admins", "--database", "existing", "--db-host", "sql01",
             "--db-port", "5433", "--db-admin-user", "pgadmin", "--host-name", "ds01.lab.local", "--port", "8443", "--log", "C:\\setup.log"],
            name => environment.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(SetupMode.Quiet, parsed.Mode);
        Assert.Equal("LAB\\svc", parsed.Options.ScanAccount);
        Assert.Equal("scan secret", parsed.Options.ScanPassword);
        Assert.Equal(DatabaseChoice.Existing, parsed.Options.Database);
        Assert.Equal("sql01", parsed.Options.DatabaseHost);
        Assert.Equal(5433, parsed.Options.DatabasePort);
        Assert.Equal("pgadmin", parsed.Options.DatabaseAdminUser);
        Assert.Equal("db secret", parsed.Options.DatabaseAdminPassword);
        Assert.Equal(8443, parsed.Options.HttpsPort);
        Assert.Equal("C:\\setup.log", parsed.LogPath);
    }

    [Fact]
    public void The_bundled_database_is_the_default()
    {
        Assert.Equal(DatabaseChoice.Bundled, SetupArguments.Parse(["/quiet"], NoEnvironment).Options.Database);
    }

    [Fact]
    public void Extract_needs_a_folder()
    {
        Assert.Equal("C:\\out", SetupArguments.Parse(["/extract", "C:\\out"], NoEnvironment).ExtractTo);
        Assert.Throws<SetupArgumentException>(() => SetupArguments.Parse(["/extract"], NoEnvironment));
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--db-port", "abc")]
    [InlineData("--database", "sqlite")]
    [InlineData("--password", "x")]
    public void Wrong_options_are_refused_with_a_reason(string name, string value)
    {
        var e = Assert.Throws<SetupArgumentException>(() => SetupArguments.Parse(["/quiet", name, value], NoEnvironment));
        Assert.NotEmpty(e.Message);
    }

    [Fact]
    public void An_option_missing_its_value_is_refused()
    {
        Assert.Throws<SetupArgumentException>(() => SetupArguments.Parse(["/quiet", "--scan-account", "--admin-group", "x"], NoEnvironment));
    }
}
