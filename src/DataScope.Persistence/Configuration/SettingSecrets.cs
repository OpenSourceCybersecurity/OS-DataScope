using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DataScope.Persistence.Configuration;

/// <summary>Encrypts and decrypts setting values. Production uses Windows DPAPI; tests can supply their own.</summary>
public interface ISecretCipher
{
    byte[] Encrypt(byte[] plain);

    byte[] Decrypt(byte[] encrypted);
}

/// <summary>
/// Windows DPAPI with the local machine's key. Both Data Scope services (which run as different accounts) and setup can
/// read the values, and nothing can read them on another computer, so a copied settings file or backup of the config
/// folder does not reveal the passwords. Any program already running on this server could decrypt them too, which is
/// why the folder's permissions still matter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMachineCipher : ISecretCipher
{
    // Ties the ciphertext to Data Scope: another program using machine DPAPI without this value can't decrypt it.
    private static readonly byte[] Entropy = "Data Scope settings v1"u8.ToArray();

    public byte[] Encrypt(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

    public byte[] Decrypt(byte[] encrypted) => ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
}

/// <summary>
/// Secrets in datascope.json are stored as "dpapi:" followed by base64 ciphertext. Values without the prefix are read
/// as they are, so a file written by an older version keeps working until setup protects it.
/// </summary>
public static class SettingSecrets
{
    public const string Prefix = "dpapi:";

    /// <summary>Settings that hold passwords. Setup encrypts these, and its check warns when one is plain text.</summary>
    public static readonly IReadOnlyList<string[]> Keys =
    [
        ["ConnectionStrings", "DataScope"],
        ["ConnectionStrings", "DataScopeOwner"],
        ["Notifications", "Smtp", "Password"],
        ["Directory", "Password"],
        ["Backup", "Key"],
    ];

    /// <summary>DPAPI on Windows. Elsewhere (development and CI on Linux) there is no machine key, so secrets stay plain.</summary>
    public static ISecretCipher? Default => OperatingSystem.IsWindows() ? new DpapiMachineCipher() : null;

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain, ISecretCipher cipher) =>
        Prefix + Convert.ToBase64String(cipher.Encrypt(Encoding.UTF8.GetBytes(plain)));

    /// <summary>Returns the plain value. Throws <see cref="SecretUnreadableException"/> with a fix when it can't.</summary>
    public static string Reveal(string value, ISecretCipher? cipher, string key)
    {
        if (!IsProtected(value))
        {
            return value;
        }
        if (cipher is null)
        {
            throw new SecretUnreadableException($"The setting {key} is encrypted with Windows DPAPI and can only be read on the Windows server that saved it.");
        }
        try
        {
            return Encoding.UTF8.GetString(cipher.Decrypt(Convert.FromBase64String(value[Prefix.Length..])));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new SecretUnreadableException(
                $"The setting {key} could not be decrypted. It was probably saved on another computer, for example in settings restored from " +
                "another server. Run DataScope.Setup again on this server to set it (see ADMIN-GUIDE.md, \"Move to a new server\").");
        }
    }
}

public sealed class SecretUnreadableException(string message) : Exception(message);
