using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataScope.Persistence.Configuration;

namespace DataScope.Setup.Tests;

public sealed class ServerCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static X509Certificate2 Make(DateTimeOffset from, DateTimeOffset to)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=datascope.northwind.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(from, to);
    }

    [Fact]
    public void Finds_a_valid_certificate_by_thumbprint_in_any_format()
    {
        using var cert = Make(Now.AddDays(-1), Now.AddYears(1));
        using var other = Make(Now.AddDays(-1), Now.AddYears(1));
        var pasted = "‎" + string.Join(' ', cert.Thumbprint.ToLowerInvariant().Chunk(2).Select(c => new string(c)));
        Assert.Same(cert, ServerCertificate.Select([other, cert], pasted, Now));
    }

    [Fact]
    public void Refuses_an_unknown_thumbprint()
    {
        using var cert = Make(Now.AddDays(-1), Now.AddYears(1));
        var ex = Assert.Throws<CertificateUnusableException>(() => ServerCertificate.Select([cert], new string('A', 40), Now));
        Assert.Contains("LocalMachine\\My", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void Refuses_text_that_is_not_a_thumbprint(string thumbprint) =>
        Assert.Throws<CertificateUnusableException>(() => ServerCertificate.Select([], thumbprint, Now));

    [Fact]
    public void Refuses_a_certificate_without_its_private_key()
    {
        using var cert = Make(Now.AddDays(-1), Now.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var ex = Assert.Throws<CertificateUnusableException>(() => ServerCertificate.Select([publicOnly], cert.Thumbprint, Now));
        Assert.Contains("private key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_expired_and_not_yet_valid_certificates()
    {
        using var expired = Make(Now.AddYears(-2), Now.AddDays(-1));
        using var future = Make(Now.AddDays(1), Now.AddYears(1));
        Assert.Throws<CertificateUnusableException>(() => ServerCertificate.Select([expired], expired.Thumbprint, Now));
        Assert.Throws<CertificateUnusableException>(() => ServerCertificate.Select([future], future.Thumbprint, Now));
    }
}
