using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class UsbDetectionStrategyTests
{
    [Fact]
    public void Parse_CcbModeDoesNotRequireLegacyFields()
    {
        var profile = LauncherProfileJson.Parse("""
            {"targetUrl":"https://example.com/","usbDetectionStrategy":"ccbUsbStor"}
            """);

        Assert.True(LauncherProfileValidator.Validate(profile).IsValid);
        Assert.Empty(profile.GetUsbDeviceAllowlist());
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Parse_RejectsUnsupportedStrategyEvenWithValidLegacyFields(string strategy)
    {
        var json = $$$"""
            {"targetUrl":"https://example.com/","usbDetectionStrategy":{{{strategy}}},
             "usbDevice":{"vendorId":"1677","productId":"0054","busReportedDescription":"uKeyCI800-K156"}}
            """;

        Assert.Throws<LauncherProfileException>(() => LauncherProfileJson.Parse(json));
    }
}
