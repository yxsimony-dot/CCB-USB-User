// Provider and container names in this file are synthetic public fixtures.
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsCertificateProviderMetadataReaderTests
{
    [Theory]
    [InlineData(1u, 1u, ClientCertificateProviderKind.Csp, "AT_KEYEXCHANGE")]
    [InlineData(1u, 2u, ClientCertificateProviderKind.Csp, "AT_SIGNATURE")]
    [InlineData(0u, uint.MaxValue, ClientCertificateProviderKind.Ksp, "CERT_NCRYPT_KEY_SPEC")]
    [InlineData(0u, 73u, ClientCertificateProviderKind.Ksp, "73")]
    public void CreateProviderMetadata_ClassifiesCspAndKspWithoutOpeningPrivateKey(
        uint providerType,
        uint keySpec,
        ClientCertificateProviderKind expectedKind,
        string expectedKeySpec)
    {
        var result = WindowsCertificateProviderMetadataReader.CreateProviderMetadata(
            "Synthetic PSBC Provider",
            providerType,
            "SYNTHETIC_PSBC_CONTAINER",
            keySpec);

        Assert.Equal(expectedKind, result.ProviderKind);
        Assert.Equal("Synthetic PSBC Provider", result.ProviderName);
        Assert.Equal(providerType, result.ProviderType);
        Assert.Equal("SYNTHETIC_PSBC_CONTAINER", result.ContainerOrKeyName);
        Assert.Equal(expectedKeySpec, result.KeySpec);
        Assert.Equal(
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
            result.Source);
        Assert.Equal(ClientCertificateProviderMetadataFailure.None, result.Failure);
    }
}
