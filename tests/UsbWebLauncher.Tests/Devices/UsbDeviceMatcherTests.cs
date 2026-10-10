using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class UsbDeviceMatcherTests
{
    [Fact]
    public void IsMatch_AcceptsObservedHardwareAndDescription()
    {
        var device = CreateObservedDevice();

        var matched = UsbDeviceMatcher.IsMatch(device, CreateRequiredOptions());

        Assert.True(matched);
    }

    [Fact]
    public void IsMatch_RejectsUnrelatedHardwareId()
    {
        var device = CreateObservedDevice() with
        {
            HardwareIds = ["USB\\VID_9999&PID_0054"]
        };

        var matched = UsbDeviceMatcher.IsMatch(device, CreateRequiredOptions());

        Assert.False(matched);
    }

    [Fact]
    public void IsMatch_IgnoresCaseInStableTextProperties()
    {
        var device = CreateObservedDevice() with
        {
            HardwareIds = ["usb\\vid_1677&pid_0054&rev_0103"],
            BusReportedDescription = "ukeyci800-k156"
        };

        var matched = UsbDeviceMatcher.IsMatch(device, CreateRequiredOptions());

        Assert.True(matched);
    }

    [Fact]
    public void IsMatch_DoesNotDependOnPortSpecificInstanceId()
    {
        var firstPort = CreateObservedDevice() with
        {
            InstanceId = "USB\\VID_1677&PID_0054\\6&FIRST&0&1"
        };
        var secondPort = firstPort with
        {
            InstanceId = "USB\\VID_1677&PID_0054\\9&SECOND&0&4"
        };

        Assert.True(UsbDeviceMatcher.IsMatch(firstPort, CreateRequiredOptions()));
        Assert.True(UsbDeviceMatcher.IsMatch(secondPort, CreateRequiredOptions()));
    }

    [Fact]
    public void IsMatch_AcceptsConfiguredVirtualDriveAttributes()
    {
        var options = CreateRequiredOptions() with
        {
            CdRomName = "HDZB-CCB USB AutoRunDisk",
            VolumeLabel = "HDZB_USBKEY_KEY",
            FileSystem = "CDFS"
        };

        var matched = UsbDeviceMatcher.IsMatch(CreateObservedDevice(), options);

        Assert.True(matched);
    }

    [Fact]
    public void IsMatch_RejectsMissingConfiguredVirtualDriveAttribute()
    {
        var options = CreateRequiredOptions() with { VolumeLabel = "EXPECTED" };

        var matched = UsbDeviceMatcher.IsMatch(CreateObservedDevice(), options);

        Assert.False(matched);
    }

    [Fact]
    public void IsCcbUsbStorMatch_AcceptsKnownUsbKeyWithoutCcbVolumeMarker()
    {
        var device = new UsbDeviceDescriptor(
            "USB\\VID_163C&PID_548C\\KEY",
            ["USB\\VID_163C&PID_548C", "USB\\VID_163C&PID_548C&REV_0100"],
            "USB Key",
            [], [], [], "USBSTOR");

        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
    }

    [Theory]
    [InlineData("USB Key", "1234", "548C")]
    [InlineData("Generic Storage", "163C", "548D")]
    public void IsCcbUsbStorMatch_RejectsLookalikeStorageDevices(
        string description, string vendorId, string productId)
    {
        var device = new UsbDeviceDescriptor(
            $"USB\\VID_{vendorId}&PID_{productId}\\KEY",
            [$"USB\\VID_{vendorId}&PID_{productId}"],
            description,
            [], [], [], "USBSTOR");

        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
    }

    [Fact]
    public void IsCcbUsbStorMatch_KnownDetectorPairDoesNotRequireFriendlyDescription()
    {
        var device = new UsbDeviceDescriptor("USB\\VID_163C&PID_548C\\KEY",
            ["USB\\VID_163C&PID_548C"], "Generic Storage", [], [], [], null);
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
    }

    private static UsbMatchOptions CreateRequiredOptions() =>
        new("1677", "0054", "uKeyCI800-K156");

    private static UsbDeviceDescriptor CreateObservedDevice() => new(
        "USB\\VID_1677&PID_0054\\6&3B164EBC&0&1",
        ["USB\\VID_1677&PID_0054&REV_0103", "USB\\VID_1677&PID_0054"],
        "uKeyCI800-K156",
        ["HDZB-CCB USB AutoRunDisk USB Device"],
        ["HDZB_USBKEY_KEY"],
        ["CDFS"]);
}
