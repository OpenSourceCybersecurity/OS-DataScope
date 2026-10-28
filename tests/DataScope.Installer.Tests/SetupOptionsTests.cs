using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class SetupOptionsTests
{
    [Fact]
    public void A_gmsa_and_an_admin_group_are_enough_with_the_bundled_database()
    {
        var options = new SetupOptions { ScanAccount = "LAB\\svc-datascope$", AdminGroup = "LAB\\DataScope-Admins" };
        Assert.Empty(options.Problems());
        Assert.True(options.ScanAccountIsGmsa);
    }

    [Fact]
    public void Nothing_filled_in_names_each_missing_answer()
    {
        var problems = new SetupOptions().Problems();
        Assert.Contains(problems, p => p.Contains("scan account", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("admin group", StringComparison.Ordinal));
    }

    [Fact]
    public void A_normal_scan_account_needs_its_password()
    {
        var options = new SetupOptions { ScanAccount = "LAB\\svc-datascope", AdminGroup = "LAB\\Admins" };
        Assert.Contains(options.Problems(), p => p.Contains("password", StringComparison.Ordinal));
        options.ScanPassword = "secret";
        Assert.Empty(options.Problems());
    }

    [Fact]
    public void An_existing_database_needs_its_admin_password_and_a_valid_port()
    {
        var options = new SetupOptions
        {
            ScanAccount = "LAB\\svc-datascope$",
            AdminGroup = "LAB\\Admins",
            Database = DatabaseChoice.Existing,
            DatabasePort = 0,
        };
        var problems = options.Problems();
        Assert.Contains(problems, p => p.Contains("PostgreSQL admin password", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("PostgreSQL port", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://ds01")]
    [InlineData("ds01:8443")]
    [InlineData("data scope")]
    public void A_web_address_name_is_only_a_server_name(string name)
    {
        var options = new SetupOptions { ScanAccount = "LAB\\s$", AdminGroup = "LAB\\A", HostName = name };
        Assert.Contains(options.Problems(), p => p.Contains("web address", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF01234567", true)]
    [InlineData("0123", false)]
    [InlineData("0123456789abcdef0123456789ABCDEF0123456G", false)]
    public void A_certificate_thumbprint_is_40_hexadecimal_characters(string thumbprint, bool ok)
    {
        var options = new SetupOptions { ScanAccount = "LAB\\s$", AdminGroup = "LAB\\A", CertificateThumbprint = thumbprint };
        Assert.Equal(ok, options.Problems().Count == 0);
    }
}
