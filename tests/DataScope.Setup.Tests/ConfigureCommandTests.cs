namespace DataScope.Setup.Tests;

public sealed class ConfigureCommandTests : IDisposable
{
    private const string AdminSid = "S-1-5-21-111-222-333-1105";
    private const string ViewerSid = "S-1-5-21-111-222-333-1106";
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-configure-").FullName;
    private string ConfigPath => Path.Combine(_dir, "datascope.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SetupProgram.RunAsync([.. args, "--config", ConfigPath], output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Maps_roles_by_sid_and_sets_https()
    {
        var (code, _, err) = await Run("configure", "--role", $"platformadmin={AdminSid}", "--role", $"Viewer={ViewerSid}",
            "--https-port", "8443", "--certificate-thumbprint", "‎ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01");
        Assert.True(code == 0, err);

        var config = ConfigFile.Load(ConfigPath);
        Assert.Equal([AdminSid], config.GetStrings("Auth", "RoleGroupSids", "PlatformAdmin"));
        Assert.Equal([ViewerSid], config.GetStrings("Auth", "RoleGroupSids", "Viewer"));
        Assert.Equal(8443, config.GetInt("Https", "Port"));
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", config.GetString("Https", "CertificateThumbprint"));
    }

    [Fact]
    public async Task Adding_a_role_again_keeps_existing_groups_once()
    {
        await Run("configure", "--role", $"PlatformAdmin={AdminSid}");
        await Run("configure", "--role", $"PlatformAdmin={AdminSid.ToLowerInvariant()}", "--role", $"PlatformAdmin={ViewerSid}");
        Assert.Equal([AdminSid, ViewerSid], ConfigFile.Load(ConfigPath).GetStrings("Auth", "RoleGroupSids", "PlatformAdmin"));

        await Run("configure", "--clear-roles", "--role", $"Viewer={ViewerSid}");
        var config = ConfigFile.Load(ConfigPath);
        Assert.Empty(config.GetStrings("Auth", "RoleGroupSids", "PlatformAdmin"));
        Assert.Equal([ViewerSid], config.GetStrings("Auth", "RoleGroupSids", "Viewer"));
    }

    [Theory]
    [InlineData("--role", "PlatformAdmin=NORTHWIND\\DataScope-Admins")]
    [InlineData("--role", "Owner=S-1-5-21-1-2-3-4")]
    [InlineData("--role", "S-1-5-21-1-2-3-4")]
    [InlineData("--certificate-thumbprint", "not-a-thumbprint")]
    [InlineData("--https-port", "99999")]
    [InlineData("--surprise", "1")]
    public async Task Invalid_values_change_nothing(string option, string value)
    {
        var (code, _, err) = await Run("configure", option, value);
        Assert.Equal(2, code);
        Assert.StartsWith("Error:", err, StringComparison.Ordinal);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Unknown_commands_and_help()
    {
        Assert.Equal(2, (await Run("explode")).Code);
        using var output = new StringWriter();
        Assert.Equal(0, await SetupProgram.RunAsync(["help"], output, TextWriter.Null, CancellationToken.None));
        Assert.Contains("DataScope.Setup database", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_without_a_database_connection_explains_what_to_do()
    {
        var (code, _, err) = await Run("migrate");
        Assert.Equal(1, code);
        Assert.Contains("DataScope.Setup database", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_fails_when_the_settings_file_is_missing()
    {
        var (code, output, _) = await Run("check");
        Assert.Equal(1, code);
        Assert.Contains("[FAIL] Settings file", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_needs_a_known_name()
    {
        Assert.Equal(2, (await Run("secret")).Code);
        var (code, _, err) = await Run("secret", "--name", "database-password");
        Assert.Equal(2, code);
        Assert.Contains("smtp-password", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_clear_removes_the_setting()
    {
        File.WriteAllText(ConfigPath, """{ "Notifications": { "Smtp": { "Host": "relay", "Password": "old-synthetic" } } }""");
        var (code, _, err) = await Run("secret", "--name", "smtp-password", "--clear");
        Assert.True(code == 0, err);
        var config = ConfigFile.Load(ConfigPath);
        Assert.Null(config.GetString("Notifications", "Smtp", "Password"));
        Assert.Equal("relay", config.GetString("Notifications", "Smtp", "Host"));
    }

    [Fact]
    public async Task Protect_explains_itself_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var (code, _, err) = await Run("protect");
        Assert.Equal(1, code);
        Assert.Contains("only be encrypted on Windows", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protect_encrypts_plain_passwords_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        File.WriteAllText(ConfigPath, """{ "ConnectionStrings": { "DataScope": "Host=db;Username=u;Password=plain-synthetic" } }""");
        var (code, output, err) = await Run("protect");
        Assert.True(code == 0, err);
        Assert.Contains("Encrypted 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-synthetic", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Equal("Host=db;Username=u;Password=plain-synthetic", ConfigFile.Load(ConfigPath).GetSecret("ConnectionStrings", "DataScope"));
    }
}
