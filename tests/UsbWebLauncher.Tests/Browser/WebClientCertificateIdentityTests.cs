// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class WebClientCertificateIdentityTests
{
    [Fact]
    public void FromCertificate_UsesSimpleSubjectAndIssuerNames()
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest(
            "C=CN, O=CCB, CN=CCBRSACAROOT",
            issuerKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2));

        using var subjectKey = RSA.Create(2048);
        var subjectRequest = new CertificateRequest(
            "C=CN, O=CCB, OU=NETBANK, CN=GSB00000000001",
            subjectKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var serialNumber = new byte[] { 1, 2, 3, 4 };
        using var certificate = subjectRequest.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            serialNumber);

        var identity = WebClientCertificateIdentity.FromCertificate(certificate);

        Assert.Equal(certificate.Thumbprint, identity.Sha1Fingerprint);
        Assert.Equal("GSB00000000001", identity.Subject);
        Assert.Equal("CCBRSACAROOT", identity.Issuer);
    }
}
