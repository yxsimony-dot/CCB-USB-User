using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsCredentialTargetTests
{
    [Fact]
    public void Create_UsesTheFixedApplicationNamespaceAndStableAuthenticationId()
    {
        var target = WindowsCredentialTarget.Create("ccb-client-certificate");

        Assert.Equal("UsbWebLauncher/ClientCertificate/ccb-client-certificate", target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ccb-client-certificate")]
    [InlineData("ccb-client-certificate ")]
    [InlineData("CCB-client-certificate")]
    [InlineData("ccb/client-certificate")]
    [InlineData("ccb\\client-certificate")]
    [InlineData("ccb client-certificate")]
    [InlineData("ccb\u0000client-certificate")]
    [InlineData("ccb\u001fclient-certificate")]
    [InlineData("café")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Create_RejectsNonCanonicalStableAuthenticationId(string? authenticationId) =>
        Assert.ThrowsAny<ArgumentException>(() => WindowsCredentialTarget.Create(authenticationId!));

    [Fact]
    public void Create_RejectsCaseVariantInsteadOfProducingWinCredCollision()
    {
        Assert.Throws<ArgumentException>(() => WindowsCredentialTarget.Create("ccb-client-certificate".ToUpperInvariant()));
        Assert.Equal(
            "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
            WindowsCredentialTarget.Create("ccb-client-certificate"));
    }

    [Fact]
    public void Create_AcceptsTheMaximumCanonicalAuthenticationIdLength() =>
        Assert.Equal(
            $"UsbWebLauncher/ClientCertificate/{new string('a', 64)}",
            WindowsCredentialTarget.Create(new string('a', 64)));

    [Fact]
    public void WinCredNativeBypassTypes_AreNotPublic()
    {
        Assert.False(typeof(WindowsCredentialManager).IsPublic);
        Assert.False(typeof(IWindowsCredentialNativeApi).IsPublic);
        Assert.False(typeof(WindowsCredentialNativeApi).IsPublic);
        Assert.False(typeof(WindowsCredentialNativeRecord).IsPublic);
        Assert.False(typeof(WindowsCredentialTarget).IsPublic);
        Assert.False(typeof(WindowsCredentialPinStore).IsPublic);
        Assert.True(typeof(WindowsClientCertificateAuthenticationStores).IsPublic);
    }
}
