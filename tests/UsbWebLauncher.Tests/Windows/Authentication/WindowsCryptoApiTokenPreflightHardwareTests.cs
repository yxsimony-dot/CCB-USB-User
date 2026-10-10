// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

[Collection(UsbHardwareCollection.Name)]
public sealed class WindowsCryptoApiTokenPreflightHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public void Check_WhenExplicitHardwareTestIsEnabled_FindsConfiguredProviderContainerAndKey()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var authentication = new ClientCertificateAuthenticationOptions(
            "ccb-client-certificate",
            "b2bbank.ccb.com",
            443,
            new(
                "3333333333333333333333333333333333333333",
                "GMB00000000001",
                "CCBSM2CACHILD"),
            new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                "{00000000-0000-0001}",
                "AT_SIGNATURE"),
            "UsbWebLauncher/ClientCertificate/ccb-client-certificate");

        var result = new WindowsCryptoApiTokenPreflight().Check(authentication);

        Assert.True(
            result.IsAvailable,
            $"CSP preflight failed: status={result.FailureStatus}, error={result.ProviderErrorCode}");
    }
}
