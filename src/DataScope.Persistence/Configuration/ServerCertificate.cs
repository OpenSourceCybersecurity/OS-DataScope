using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DataScope.Persistence.Configuration;

/// <summary>Finds the HTTPS certificate named by thumbprint in the server's certificate store.</summary>
public static class ServerCertificate
{
    /// <summary>Loads from LocalMachine\My. Throws with a plain explanation when the certificate can't be used.</summary>
    [SupportedOSPlatform("windows")]
    public static X509Certificate2 LoadFromMachineStore(string thumbprint, DateTimeOffset now)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return Select(store.Certificates, thumbprint, now);
    }

    /// <summary>Picks the certificate with the given thumbprint and checks it can serve HTTPS.</summary>
    public static X509Certificate2 Select(IEnumerable<X509Certificate2> candidates, string thumbprint, DateTimeOffset now)
    {
        var wanted = Normalize(thumbprint);
        if (wanted.Length != 40 || !wanted.All(Uri.IsHexDigit))
        {
            throw new CertificateUnusableException($"'{thumbprint}' is not a certificate thumbprint (40 hexadecimal characters).");
        }
        var cert = candidates.FirstOrDefault(c => string.Equals(c.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new CertificateUnusableException($"No certificate with thumbprint {wanted} is in the LocalMachine\\My store.");
        if (!cert.HasPrivateKey)
        {
            throw new CertificateUnusableException($"Certificate {wanted} has no private key, so it can't serve HTTPS.");
        }
        if (now < cert.NotBefore || now > cert.NotAfter)
        {
            throw new CertificateUnusableException(
                $"Certificate {wanted} is only valid from {cert.NotBefore:yyyy-MM-dd} to {cert.NotAfter:yyyy-MM-dd}.");
        }
        return cert;
    }

    /// <summary>Removes spaces and the invisible marker Windows' certificate dialog adds when copying a thumbprint.</summary>
    public static string Normalize(string thumbprint) =>
        new([.. thumbprint.Where(c => !char.IsWhiteSpace(c) && c != '‎' && c != '‏').Select(char.ToUpperInvariant)]);
}

public sealed class CertificateUnusableException(string message) : Exception(message);
