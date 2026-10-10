using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ApplicationLaunchOptionsTests
{
    [Fact]
    public void Parse_WithSmokeArgument_UsesEmptyDeviceMonitor()
    {
        var options = ApplicationLaunchOptions.Parse(
            ["--smoke-test-no-devices"]);

        Assert.True(options.UseEmptyDeviceMonitor);
    }

    [Fact]
    public void Parse_WithoutSmokeArgument_UsesProductionDeviceMonitor()
    {
        var options = ApplicationLaunchOptions.Parse(["--unrelated"]);

        Assert.False(options.UseEmptyDeviceMonitor);
    }

    [Fact]
    public async Task EmptyDeviceMonitor_NeverReportsADevice()
    {
        await using var monitor = new EmptyUsbDeviceMonitor();

        await monitor.StartAsync();
        var devices = await monitor.GetConnectedDevicesAsync();

        Assert.Empty(devices);
    }
}
