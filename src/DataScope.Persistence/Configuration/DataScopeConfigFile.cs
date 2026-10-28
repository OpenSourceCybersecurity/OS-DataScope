using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace DataScope.Persistence.Configuration;

/// <summary>
/// The installed server's settings file, written by DataScope.Setup. It holds the
/// database connection string, so the installer locks its folder down to
/// administrators and the two service identities, and setup encrypts the passwords
/// in it with DPAPI. Loaded last, so on a server it overrides appsettings.json.
/// </summary>
public static class DataScopeConfigFile
{
    /// <summary>Overrides the file location; used by tests and non-default installs.</summary>
    public const string PathVariable = "DATASCOPE_CONFIG";

    public const string FileName = "datascope.json";

    /// <summary>
    /// %ProgramData%\DataScope\config\datascope.json on Windows. Elsewhere there is
    /// no default and only DATASCOPE_CONFIG applies.
    /// </summary>
    public static string? ResolvePath()
    {
        var overridden = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DataScope", "config", FileName)
            : null;
    }

    public static IConfigurationBuilder AddDataScopeConfigFile(this IConfigurationBuilder builder) =>
        ResolvePath() is { } path ? builder.AddProtectedJsonFile(path, SettingSecrets.Default) : builder;

    /// <summary>A JSON settings file whose "dpapi:" values are decrypted as it loads.</summary>
    public static IConfigurationBuilder AddProtectedJsonFile(this IConfigurationBuilder builder, string path, ISecretCipher? cipher)
    {
        var source = new ProtectedJsonConfigurationSource(cipher) { Path = path, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}

internal sealed class ProtectedJsonConfigurationSource(ISecretCipher? cipher) : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ProtectedJsonConfigurationProvider(this, cipher);
    }
}

internal sealed class ProtectedJsonConfigurationProvider(JsonConfigurationSource source, ISecretCipher? cipher) : JsonConfigurationProvider(source)
{
    public override void Load(Stream stream)
    {
        base.Load(stream);
        foreach (var (key, value) in Data.Where(kv => SettingSecrets.IsProtected(kv.Value)).ToList())
        {
            // Fails startup with a plain message rather than running with a setting it can't read.
            Data[key] = SettingSecrets.Reveal(value!, cipher, key);
        }
    }
}
