using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsCertificateProviderMetadataResolverTests
{
    [Fact]
    public void Read_DirectMetadataAvailable_DoesNotQueryStore()
    {
        using var certificate = CreateCertificate();
        var direct = CreateProvider(
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty);
        var storeQueried = false;
        var resolver = new WindowsCertificateProviderMetadataResolver(
            _ => direct,
            _ =>
            {
                storeQueried = true;
                return [];
            });

        var result = resolver.Read(certificate);

        Assert.Equal(direct, result);
        Assert.False(storeQueried);
    }

    [Fact]
    public void Read_DirectPropertyMissingAndUniqueStoreMatch_UsesStoreMetadata()
    {
        using var certificate = CreateCertificate();
        string? requestedFingerprint = null;
        var storeMetadata = CreateProvider(
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty);
        var resolver = new WindowsCertificateProviderMetadataResolver(
            _ => ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.PropertyNotFound),
            fingerprint =>
            {
                requestedFingerprint = fingerprint;
                return [storeMetadata];
            });

        var result = resolver.Read(certificate);

        Assert.Equal(
            NormalizeFingerprint(certificate.Thumbprint),
            requestedFingerprint);
        Assert.Equal(storeMetadata.ProviderKind, result.ProviderKind);
        Assert.Equal(storeMetadata.ProviderName, result.ProviderName);
        Assert.Equal(storeMetadata.ProviderType, result.ProviderType);
        Assert.Equal(storeMetadata.ContainerOrKeyName, result.ContainerOrKeyName);
        Assert.Equal(storeMetadata.KeySpec, result.KeySpec);
        Assert.Equal(
            ClientCertificateProviderMetadataSource.WindowsCurrentUserMyThumbprintMatch,
            result.Source);
        Assert.Equal(ClientCertificateProviderMetadataFailure.None, result.Failure);
    }

    [Fact]
    public void Read_DirectPropertyMissingAndNoStoreMatch_ClassifiesNotFound()
    {
        using var certificate = CreateCertificate();
        var resolver = CreateFallbackResolver([]);

        var result = resolver.Read(certificate);

        Assert.Equal(
            ClientCertificateProviderMetadataFailure.StoreCertificateNotFound,
            result.Failure);
        Assert.Equal(ClientCertificateProviderMetadataSource.None, result.Source);
    }

    [Fact]
    public void Read_DirectPropertyMissingAndMultipleStoreMatches_ClassifiesConflict()
    {
        using var certificate = CreateCertificate();
        var provider = CreateProvider(
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty);
        var resolver = CreateFallbackResolver([provider, provider]);

        var result = resolver.Read(certificate);

        Assert.Equal(
            ClientCertificateProviderMetadataFailure.StoreCertificateConflict,
            result.Failure);
        Assert.Equal(ClientCertificateProviderMetadataSource.None, result.Source);
    }

    [Fact]
    public void Read_StoreLookupThrows_ClassifiesStoreReadFailure()
    {
        using var certificate = CreateCertificate();
        var resolver = new WindowsCertificateProviderMetadataResolver(
            _ => ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.PropertyNotFound),
            _ => throw new InvalidOperationException("Synthetic store failure."));

        var result = resolver.Read(certificate);

        Assert.Equal(
            ClientCertificateProviderMetadataFailure.StoreReadFailed,
            result.Failure);
        Assert.Equal(ClientCertificateProviderMetadataSource.None, result.Source);
    }

    [Fact]
    public void Read_UniqueStoreMatchPreservesProviderReadFailureAndCorrelationSource()
    {
        using var certificate = CreateCertificate();
        var providerFailure = ClientCertificateProviderMetadata.Unavailable(
            ClientCertificateProviderMetadataFailure.AccessDenied);
        var resolver = CreateFallbackResolver([providerFailure]);

        var result = resolver.Read(certificate);

        Assert.Equal(
            ClientCertificateProviderMetadataFailure.AccessDenied,
            result.Failure);
        Assert.Equal(
            ClientCertificateProviderMetadataSource.WindowsCurrentUserMyThumbprintMatch,
            result.Source);
    }

    private static WindowsCertificateProviderMetadataResolver CreateFallbackResolver(
        IReadOnlyList<ClientCertificateProviderMetadata> matches) =>
        new(
            _ => ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.PropertyNotFound),
            _ => matches);

    private static ClientCertificateProviderMetadata CreateProvider(
        ClientCertificateProviderMetadataSource source) =>
        new(
            ClientCertificateProviderKind.Ksp,
            "Synthetic PSBC Key Storage Provider",
            0,
            "SYNTHETIC_PSBC_KEY",
            "CERT_NCRYPT_KEY_SPEC",
            source,
            ClientCertificateProviderMetadataFailure.None);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=SYNTHETIC-PSBC-STORE-CORRELATION",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string NormalizeFingerprint(string? fingerprint) =>
        string.Concat((fingerprint ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character))).ToUpperInvariant();
}
