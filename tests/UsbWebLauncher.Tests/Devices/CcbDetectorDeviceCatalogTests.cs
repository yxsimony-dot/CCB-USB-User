using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class CcbDetectorDeviceCatalogTests
{
    public static IEnumerable<object?[]> DetectorPairs()
    {
        // Independent transcription of E路护航 Dev2KeyType, not production data.
        var pairs = new[] { "163C:0406", "163C:0407", "163C:0402", "163C:5471", "163C:548C",
            "8829:CCB0", "8829:CCB1", "8829:CCB2", "8829:CCB3", "1780:FE33", "1780:0410", "1780:0401",
            "1677:0028", "1677:0050", "1677:0054", "1677:0057", "096E:0707", "1EA8:C03E", "14D6:3766", "14D6:3769" };
        foreach (var pair in pairs)
        foreach (var service in new string?[] { null, "USBCCGP", "USBSTOR" })
            yield return [pair[..4], pair[5..], service];
    }

    [Theory]
    [MemberData(nameof(DetectorPairs))]
    public void DetectorRecognizedPhysicalPairNeedsNoStorageServiceOrCcbMetadata(string vendor, string product, string? service)
    {
        var device = Device($"USB\\VID_{vendor}&PID_{product}\\PORT-SERIAL",
            [$"usb\\vid_{vendor.ToLowerInvariant()}&pid_{product.ToLowerInvariant()}&rev_0100"], service);
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
        Assert.True(UsbDeviceMatcher.IsMatch(device, new LauncherProfile("https://example.test", new("0000", "0000", "ignored"), UsbDetectionStrategy: "ccbUsbStor")));
    }

    [Theory]
    [InlineData("USB\\VID_163C&PID_548C1")]
    [InlineData("USB\\VID_163C1&PID_548C")]
    [InlineData("USB\\XVID_163C&PID_548C")]
    [InlineData("USB\\VID_163C&XPID_548C")]
    [InlineData("USB\\VID_9999&PID_548C")]
    [InlineData("USB\\VID_163C&PID_9999")]
    [InlineData("USB\\VID_163C&PID_CCB0")]
    [InlineData("USB\\VID_9999&PID_9999\\VID_163C&PID_548C")]
    [InlineData("OTHER\\VID_163C&PID_548C")]
    public void RejectsUnknownOrSubstringPairsAndSerialSpoofing(string hardwareId) =>
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("USB\\VID_9999&PID_9999\\KEY", [hardwareId])));

    [Theory]
    [InlineData("USBSTOR\\VID_163C&PID_548C\\KEY")]
    [InlineData("SCSI\\VID_163C&PID_548C\\KEY")]
    [InlineData("ROOT\\VID_163C&PID_548C\\KEY")]
    [InlineData("HID\\VID_163C&PID_548C\\KEY")]
    public void KnownPairStillRequiresAPhysicalUsbNode(string instanceId) =>
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device(instanceId, ["USB\\VID_163C&PID_548C"])));

    [Fact]
    public void DoesNotCombineVendorAndProductFromDifferentHardwareIds() =>
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("USB\\VID_163C&PID_548C\\KEY",
            ["USB\\VID_163C&PID_0000", "USB\\VID_0000&PID_548C"])));

    [Fact]
    public void EmptyHardwareIdsMayUseOnlyTheInstanceHardwareSegment()
    {
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("usb\\vid_163c&pid_548c\\KEY", [])));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("USB\\VID_9999&PID_9999\\VID_163C&PID_548C", [])));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("USB\\VID_163C&PID_548C1\\KEY", [])));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(Device("USB\\VID_163C&PID_548C\\KEY", ["USB\\VID_9999&PID_9999"])));
    }

    [Fact]
    public void ExistingCcbStorageMarkerStillAcceptsFutureModels()
    {
        var future = Device("USB\\VID_ABCD&PID_1234\\KEY", ["USB\\VID_ABCD&PID_1234"], "USBSTOR")
            with { VolumeLabels = ["NEW-CCB-KEY"] };
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(future));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(future with { Service = null }));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(future with { VolumeLabels = [] }));
    }

    private static UsbDeviceDescriptor Device(string instance, string[] hardware, string? service = null) =>
        new(instance, hardware, null, [], [], [], service);
}
