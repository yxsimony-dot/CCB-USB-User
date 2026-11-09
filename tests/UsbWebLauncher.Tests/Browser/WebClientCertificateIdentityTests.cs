// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

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

    [Fact]
    public void FromCertificate_CollectsPublicFieldsAndInjectedProviderMetadata()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "C=CN, O=PSBC, OU=CORPORATE, CN=SYNTHETIC-PSBC-CERT",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.2", "Client Authentication")
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(usages, critical: false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2));
        var provider = new ClientCertificateProviderMetadata(
            ClientCertificateProviderKind.Ksp,
            "Synthetic PSBC Key Storage Provider",
            0,
            "SYNTHETIC_PSBC_KEY",
            "CERT_NCRYPT_KEY_SPEC",
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
            ClientCertificateProviderMetadataFailure.None);

        var identity = WebClientCertificateIdentity.FromCertificate(
            certificate,
            provider);
        var candidate = identity.ToCandidate();

        Assert.Equal(certificate.Thumbprint, candidate.Sha1Fingerprint);
        Assert.Equal("SYNTHETIC-PSBC-CERT", candidate.Subject);
        Assert.Equal("SYNTHETIC-PSBC-CERT", candidate.Issuer);
        Assert.Contains("O=PSBC", candidate.SubjectDistinguishedName);
        Assert.Equal(certificate.SerialNumber, candidate.SerialNumber);
        Assert.Equal(
            certificate.NotBefore.ToUniversalTime(),
            candidate.ValidFromUtc!.Value.UtcDateTime);
        Assert.Equal(
            certificate.NotAfter.ToUniversalTime(),
            candidate.ValidToUtc!.Value.UtcDateTime);
        var usage = Assert.Single(candidate.EnhancedKeyUsages);
        Assert.Equal("1.3.6.1.5.5.7.3.2", usage.Oid);
        Assert.Equal(provider, candidate.ProviderMetadata);
    }

    [Fact]
    public void ProviderMetadataReader_PublicOnlyCertificate_FailsClosedWithoutPrivateKeyAccess()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=SYNTHETIC-PUBLIC-ONLY-PSBC-CERT",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificateWithPrivateKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2));
        using var publicOnlyCertificate = new X509Certificate2(
            certificateWithPrivateKey.Export(X509ContentType.Cert));

        var provider = WindowsCertificateProviderMetadataReader.Instance.Read(
            publicOnlyCertificate);

        Assert.Equal(ClientCertificateProviderKind.Unknown, provider.ProviderKind);
        Assert.Equal(string.Empty, provider.ProviderName);
        Assert.Null(provider.ProviderType);
        Assert.Equal(string.Empty, provider.ContainerOrKeyName);
        Assert.Equal(string.Empty, provider.KeySpec);
        Assert.Equal(ClientCertificateProviderMetadataSource.None, provider.Source);
        Assert.Equal(
            ClientCertificateProviderMetadataFailure.PropertyNotFound,
            provider.Failure);
    }

    [Fact]
    public void Candidate_NormalizesCertificateAndProviderMetadata()
    {
        var candidate = new ClientCertificateCandidate(
            "aa bb cc",
            "  Synthetic Subject  ",
            " Synthetic Issuer ",
            " CN=Synthetic Subject ",
            " CN=Synthetic Issuer ",
            DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
            DateTimeOffset.Parse("2027-01-02T03:04:05+08:00"),
            "01 02 ab",
            [new(" 1.3.6.1.5.5.7.3.2 ", " Client Authentication ")],
            new(
                ClientCertificateProviderKind.Csp,
                " Tendyron OnKey193 CSP PSBC v1.0 ",
                1,
                " SYNTHETIC_CONTAINER ",
                " at_signature ",
                ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
                ClientCertificateProviderMetadataFailure.None));

        Assert.Equal("AABBCC", candidate.Sha1Fingerprint);
        Assert.Equal("Synthetic Subject", candidate.Subject);
        Assert.Equal("Synthetic Issuer", candidate.Issuer);
        Assert.Equal("CN=Synthetic Subject", candidate.SubjectDistinguishedName);
        Assert.Equal("CN=Synthetic Issuer", candidate.IssuerDistinguishedName);
        Assert.Equal("0102AB", candidate.SerialNumber);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-01T19:04:05Z"),
            candidate.ValidFromUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2027-01-01T19:04:05Z"),
            candidate.ValidToUtc);
        var usage = Assert.Single(candidate.EnhancedKeyUsages);
        Assert.Equal("1.3.6.1.5.5.7.3.2", usage.Oid);
        Assert.Equal("Client Authentication", usage.FriendlyName);
        Assert.Equal(
            "Tendyron OnKey193 CSP PSBC v1.0",
            candidate.ProviderMetadata.ProviderName);
        Assert.Equal(
            "SYNTHETIC_CONTAINER",
            candidate.ProviderMetadata.ContainerOrKeyName);
        Assert.Equal("AT_SIGNATURE", candidate.ProviderMetadata.KeySpec);
    }
}
