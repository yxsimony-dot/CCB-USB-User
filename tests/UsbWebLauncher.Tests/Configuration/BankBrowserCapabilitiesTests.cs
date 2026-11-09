using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class BankBrowserCapabilitiesTests
{
    [Fact]
    public void ConstructionBankResolvesOnlyConstructionBankBrowserCapabilities()
    {
        var capabilities =
            BankFlowCatalog.ChinaConstructionBank.BrowserCapabilities;

        Assert.Equal(
            BankWebView2EnvironmentKind.ExtensionEnabled,
            capabilities.EnvironmentKind);
        Assert.True(capabilities.InstallCcbUKeyExtension);
        Assert.True(capabilities.EnableCcbPageCoordination);
        Assert.False(capabilities.EnablePsbcClientCertificateHandling);
        Assert.Equal("ccb", capabilities.SessionDirectoryName);
    }

    [Fact]
    public void PostalSavingsBankResolvesOnlyPostalSavingsBrowserCapabilities()
    {
        var capabilities =
            BankFlowCatalog.PostalSavingsBank.BrowserCapabilities;

        Assert.Equal(
            BankWebView2EnvironmentKind.Standard,
            capabilities.EnvironmentKind);
        Assert.False(capabilities.InstallCcbUKeyExtension);
        Assert.False(capabilities.EnableCcbPageCoordination);
        Assert.True(capabilities.EnablePsbcClientCertificateHandling);
        Assert.Equal("psbc", capabilities.SessionDirectoryName);
    }
}
