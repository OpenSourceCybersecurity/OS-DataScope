using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DataScope.Persistence.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataScope.Setup.Tests;

/// <summary>Reversible stand-in for DPAPI so the format and error paths are tested on every platform.</summary>
internal sealed class FakeCipher(byte key = 0x5A) : ISecretCipher
{
    public byte[] Encrypt(byte[] plain) => [0xD5, .. plain.Select(b => (byte)(b ^ key))];

    public byte[] Decrypt(byte[] encrypted) =>
        encrypted.Length > 0 && encrypted[0] == 0xD5 && key == 0x5A
            ? [.. encrypted.Skip(1).Select(b => (byte)(b ^ key))]
            : throw new CryptographicException("Key not valid for use in specified state.");
}

public sealed class SecretSettingsTests : IDisposable
{
    private const string Cs = "Host=db;Database=datascope;Username=datascope_app;Password=synthetic-test-only";
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-secrets-").FullName;
    private string ConfigPath => Path.Combine(_dir, "datascope.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Secrets_are_saved_encrypted_and_read_back()
    {
        var config = ConfigFile.Load(ConfigPath, new FakeCipher());
        config.SetSecret(Cs, "ConnectionStrings", "DataScope");
        config.Set("relay.northwind.test", "Notifications", "Smtp", "Host");
        config.Save();

        var text = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("synthetic-test-only", text, StringComparison.Ordinal);
        Assert.Contains("\"dpapi:", text, StringComparison.Ordinal);
        Assert.Contains("relay.northwind.test", text, StringComparison.Ordinal);
        Assert.Equal(Cs, ConfigFile.Load(ConfigPath, new FakeCipher()).GetSecret("ConnectionStrings", "DataScope"));
        Assert.Empty(ConfigFile.Load(ConfigPath, new FakeCipher()).PlainSecrets());
    }

    [Fact]
    public void Plain_secrets_from_an_older_version_are_found_and_protected()
    {
        File.WriteAllText(ConfigPath, $$"""
            { "ConnectionStrings": { "DataScope": "{{Cs}}" },
              "Notifications": { "Smtp": { "Host": "relay", "Password": "smtp-synthetic" } },
              "Directory": { "Password": "" } }
            """);
        var config = ConfigFile.Load(ConfigPath, new FakeCipher());
        Assert.Equal(["ConnectionStrings:DataScope", "Notifications:Smtp:Password"], config.PlainSecrets());
        // Plain values still read as they are, so an upgraded server starts before setup protects them.
        Assert.Equal(Cs, config.GetSecret("ConnectionStrings", "DataScope"));

        Assert.Equal(2, config.ProtectSecrets());
        config.Save();
        Assert.Equal(0, config.ProtectSecrets());
        var again = ConfigFile.Load(ConfigPath, new FakeCipher());
        Assert.Empty(again.PlainSecrets());
        Assert.Equal("smtp-synthetic", again.GetSecret("Notifications", "Smtp", "Password"));
        Assert.Equal("relay", again.GetString("Notifications", "Smtp", "Host"));
    }

    [Fact]
    public void Without_DPAPI_secrets_stay_plain_and_nothing_is_protected()
    {
        var config = ConfigFile.Load(ConfigPath, cipher: null);
        config.SetSecret(Cs, "ConnectionStrings", "DataScope");
        Assert.False(config.EncryptsSecrets);
        Assert.Equal(0, config.ProtectSecrets());
        Assert.Equal(Cs, config.GetString("ConnectionStrings", "DataScope"));
    }

    [Fact]
    public void A_secret_from_another_computer_gives_a_plain_error()
    {
        var config = ConfigFile.Load(ConfigPath, new FakeCipher());
        config.SetSecret(Cs, "ConnectionStrings", "DataScope");
        config.Save();

        var elsewhere = ConfigFile.Load(ConfigPath, new FakeCipher(key: 0x11));
        var ex = Assert.Throws<SetupException>(() => elsewhere.GetSecret("ConnectionStrings", "DataScope"));
        Assert.Contains("could not be decrypted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:DataScope", ex.Message, StringComparison.Ordinal);

        var noDpapi = ConfigFile.Load(ConfigPath, cipher: null);
        Assert.Contains("only be read on the Windows server", Assert.Throws<SetupException>(() => noDpapi.GetSecret("ConnectionStrings", "DataScope")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_services_settings_loader_decrypts_values()
    {
        var config = ConfigFile.Load(ConfigPath, new FakeCipher());
        config.SetSecret(Cs, "ConnectionStrings", "DataScope");
        config.SetSecret("smtp-synthetic", "Notifications", "Smtp", "Password");
        config.Set(587, "Notifications", "Smtp", "Port");
        config.Save();

        var loaded = new ConfigurationBuilder().AddProtectedJsonFile(ConfigPath, new FakeCipher()).Build();
        Assert.Equal(Cs, loaded.GetConnectionString("DataScope"));
        Assert.Equal("smtp-synthetic", loaded["Notifications:Smtp:Password"]);
        Assert.Equal("587", loaded["Notifications:Smtp:Port"]);

        // A service that can't decrypt refuses to start rather than running with a wrong setting.
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().AddProtectedJsonFile(ConfigPath, new FakeCipher(0x11)).Build());
        var reason = Assert.IsType<SecretUnreadableException>(ex.InnerException);
        Assert.Contains("could not be decrypted", reason.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_settings_file_is_optional()
    {
        var loaded = new ConfigurationBuilder().AddProtectedJsonFile(Path.Combine(_dir, "absent.json"), new FakeCipher()).Build();
        Assert.Null(loaded.GetConnectionString("DataScope"));
    }

    [Fact]
    public void Dpapi_round_trip_on_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            DpapiRoundTrip();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DpapiRoundTrip()
    {
        var cipher = new DpapiMachineCipher();
        var protectedValue = SettingSecrets.Protect(Cs, cipher);
        Assert.StartsWith(SettingSecrets.Prefix, protectedValue, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", protectedValue, StringComparison.Ordinal);
        Assert.Equal(Cs, SettingSecrets.Reveal(protectedValue, cipher, "ConnectionStrings:DataScope"));
        // Same machine key, different entropy: plain machine DPAPI from another program can't read it.
        var raw = Convert.FromBase64String(protectedValue[SettingSecrets.Prefix.Length..]);
        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.LocalMachine));
    }

    [Fact]
    public void The_owner_folder_is_closed_to_everyone_else()
    {
        var folder = Path.Combine(_dir, OwnerSettings.FolderName);
        AdminOnlyFolder.Ensure(folder);
        if (OperatingSystem.IsWindows())
        {
            AssertAdminOnly(folder);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }
        Assert.Equal(Path.Combine(_dir, OwnerSettings.FolderName, OwnerSettings.FileName), OwnerSettings.PathFor(ConfigPath));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertAdminOnly(string folder)
    {
        var rules = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.False(r.IsInherited));
        var allowed = AdminOnlyFolder.AllowedSids.Select(s => s.Value).Order().ToList();
        Assert.Equal(allowed, rules.Select(r => r.IdentityReference.Value).Distinct().Order().ToList());
    }
}
