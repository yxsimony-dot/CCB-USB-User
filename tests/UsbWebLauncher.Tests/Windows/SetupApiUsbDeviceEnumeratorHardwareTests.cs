using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Windows.Devices;

namespace UsbWebLauncher.Tests.Windows;

[Collection(UsbHardwareCollection.Name)]
public sealed class SetupApiUsbDeviceEnumeratorHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task EnumerateAsync_WhenCcbTokenIsConnected_CollectsServiceAndBankEvidence()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var devices = await new SetupApiUsbDeviceEnumerator().EnumerateAsync();

        Assert.Contains(devices, UsbDeviceMatcher.IsCcbUsbStorMatch);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task EnumerateAsync_WhenObservedTokenIsConnected_FindsConfiguredModel()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var enumerator = new SetupApiUsbDeviceEnumerator();
        var options = new UsbMatchOptions(
            "1780",
            "0401",
            "Tendyron Token",
            "TENDYRON U-DISK USB Device",
            "CCB",
            "CDFS");

        var devices = await enumerator.EnumerateAsync();

        Assert.Contains(devices, device => UsbDeviceMatcher.IsMatch(device, options));
    }
}
