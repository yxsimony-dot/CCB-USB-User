using UsbWebLauncher.App.Composition;
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

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task DetectAsync_WhenTendyronPsbcTokenIsConnected_ReturnsReady()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var devices = await new SetupApiUsbDeviceEnumerator().EnumerateAsync();
        var candidate = Assert.Single(devices.Where(device =>
            device.HardwareIds.Any(id =>
                id.Contains("VID_1780", StringComparison.OrdinalIgnoreCase) &&
                id.Contains("PID_0401", StringComparison.OrdinalIgnoreCase))));
        var detector = new PsbcUsbKeyProbeAggregator(
            new WindowsPsbcUsbKeyEnvironmentQuery());

        var results = await detector.DetectAsync(devices);

        var ready = Assert.Single(results.Where(result =>
            result.VendorFamily == UsbKeyVendorFamily.Tendyron));
        Assert.Equal(UsbKeyDetectionStatus.Ready, ready.Status);
        Assert.Equal(candidate.InstanceId, ready.PhysicalInstanceId);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task PresenceObserver_WhenTendyronPsbcTokenIsAlreadyConnected_PublishesReady()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var source = new WindowsUsbDeviceMonitor(
            PsbcUsbMetadataRetryPolicy.Instance);
        var monitor = new PsbcUsbKeyDeviceMonitor(
            source,
            new PsbcUsbKeyProbeAggregator(
                new WindowsPsbcUsbKeyEnvironmentQuery()));
        var publications = new List<UsbKeyDetectionStatus?>();
        await using var observer = new PsbcUsbKeyPresenceObserver(
            monitor,
            publications.Add);

        await observer.StartAsync();

        Assert.Equal(UsbKeyDetectionStatus.Ready, Assert.Single(publications));
    }
}
