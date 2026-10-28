using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class InstallOutputTests
{
    [Theory]
    [InlineData("==> Copying programs to C:\\Program Files\\Data Scope", OutputKind.Step, "Copying programs to C:\\Program Files\\Data Scope")]
    [InlineData("    [OK  ] Memory: 8 GB", OutputKind.CheckPassed, "Memory: 8 GB")]
    [InlineData("    [WARN] Domain: not joined", OutputKind.CheckWarning, "Domain: not joined")]
    [InlineData("    [FAIL] HTTPS port: Port 443 is used by 'w3wp'.", OutputKind.CheckFailed, "HTTPS port: Port 443 is used by 'w3wp'.")]
    [InlineData("    Scanner runs as LAB\\svc-datascope$", OutputKind.Plain, "    Scanner runs as LAB\\svc-datascope$")]
    public void Classifies_what_the_installer_prints(string line, OutputKind kind, string text)
    {
        Assert.Equal((kind, text), InstallOutput.Classify(line));
    }

    [Fact]
    public void The_reason_drops_the_script_path_and_the_position_lines()
    {
        string[] errors =
        [
            "",
            "C:\\Users\\admin\\AppData\\Local\\Temp\\DataScope-Setup-1a2b\\DataScope-1.0.0\\Install-DataScope.ps1 : 1 check(s) failed; nothing was changed.",
            "At line:1 char:1",
            "+ CategoryInfo          : NotSpecified",
        ];
        Assert.Equal("1 check(s) failed; nothing was changed.", InstallOutput.Reason(errors));
    }

    [Fact]
    public void No_error_output_has_no_reason()
    {
        Assert.Null(InstallOutput.Reason([]));
    }
}
