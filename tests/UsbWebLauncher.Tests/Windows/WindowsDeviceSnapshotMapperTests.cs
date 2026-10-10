using UsbWebLauncher.Windows.Devices;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsDeviceSnapshotMapperTests
{
    [Fact]
    public void Map_PreservesBusNameWhenFriendlyNameDoesNotContainBankIdentity()
    {
        WindowsDeviceRecord[] records =
        [
            new("USB\\VID_1234&PID_5678\\SANITIZED", [], "Token", null, "USB"),
            new("USBSTOR\\CDROM\\SANITIZED", [], "CCB virtual CD", "Generic drive", "CDROM",
                "USB\\VID_1234&PID_5678\\SANITIZED")
        ];

        var device = Assert.Single(WindowsDeviceSnapshotMapper.Map(records, []));

        Assert.Contains("CCB virtual CD", device.CdRomNames);
        Assert.Contains("Generic drive", device.CdRomNames);
    }

    [Fact]
    public void Map_PreservesStableUsbPropertiesAndAuxiliaryDriveData()
    {
        WindowsDeviceRecord[] records =
        [
            new(
                "USB\\VID_1677&PID_0054\\6&PORT&0&1",
                ["USB\\VID_1677&PID_0054&REV_0103", "USB\\VID_1677&PID_0054"],
                "uKeyCI800-K156",
                "USB 大容量存储设备",
                "USB"),
            new(
                "USBSTOR\\CDROM&VEN_HDZB-CCB&PROD_USB_AUTORUNDISK&REV_1.00\\7&CHILD&1",
                ["USBSTOR\\CdRomHDZB-CCBUSB_AutoRunDisk_1.00"],
                "HDZB-CCB USB AutoRunDisk USB Device",
                "HDZB-CCB USB AutoRunDisk USB Device",
                "CDROM",
                "USB\\VID_1677&PID_0054\\6&PORT&0&1")
        ];
        WindowsVolumeRecord[] volumes = [new(
            "HDZB_USBKEY_KEY",
            "CDFS",
            "USBSTOR\\CDROM&VEN_HDZB-CCB&PROD_USB_AUTORUNDISK&REV_1.00\\7&CHILD&1")];

        var result = WindowsDeviceSnapshotMapper.Map(records, volumes);

        var device = Assert.Single(result);
        Assert.Equal("USB\\VID_1677&PID_0054\\6&PORT&0&1", device.InstanceId);
        Assert.Equal(
            ["USB\\VID_1677&PID_0054&REV_0103", "USB\\VID_1677&PID_0054"],
            device.HardwareIds);
        Assert.Equal("uKeyCI800-K156", device.BusReportedDescription);
        Assert.Contains("HDZB-CCB USB AutoRunDisk USB Device", device.CdRomNames);
        Assert.Contains("HDZB_USBKEY_KEY", device.VolumeLabels);
        Assert.Contains("CDFS", device.FileSystems);
    }

    [Fact]
    public void Map_PreservesUsbNodeMembershipWithoutVendorAndProductHardwareIds()
    {
        WindowsDeviceRecord[] records =
        [
            new(
                "USB\\ROOT_HUB30\\5&ROOT&0&0",
                ["USB\\ROOT_HUB30"],
                "USB Root Hub",
                "USB 根集线器",
                "USB")
        ];

        var result = WindowsDeviceSnapshotMapper.Map(records, []);

        Assert.Equal("USB\\ROOT_HUB30\\5&ROOT&0&0", Assert.Single(result).InstanceId);
    }

    [Fact]
    public void Map_UsesInstanceIdAsVidPidSourceWhenHardwareIdsAreUnavailable()
    {
        WindowsDeviceRecord[] records =
        [
            new(
                "USB\\VID_1677&PID_0054\\6&PORT&0&1",
                [],
                "uKeyCI800-K156",
                "USB 大容量存储设备",
                "USB")
        ];

        var result = WindowsDeviceSnapshotMapper.Map(records, []);

        var device = Assert.Single(result);
        Assert.Equal(["USB\\VID_1677&PID_0054"], device.HardwareIds);
    }

    [Fact]
    public void Map_DoesNotCopyAnotherDevicesCdRomAndVolumeProperties()
    {
        WindowsDeviceRecord[] records =
        [
            new(
                "USB\\VID_1677&PID_0054\\TARGET",
                ["USB\\VID_1677&PID_0054"],
                "uKeyCI800-K156",
                "Target",
                "USB"),
            new(
                "USB\\VID_9999&PID_9999\\OTHER",
                ["USB\\VID_9999&PID_9999"],
                "Other Token",
                "Other",
                "USB"),
            new(
                "USBSTOR\\CDROM\\OTHER-CHILD",
                ["USBSTOR\\CDROM"],
                "OTHER U-DISK",
                "OTHER U-DISK",
                "CDROM",
                "USB\\VID_9999&PID_9999\\OTHER")
        ];

        var result = WindowsDeviceSnapshotMapper.Map(
            records,
            [new("UNRELATED", "CDFS", "USBSTOR\\CDROM\\OTHER-CHILD")]);

        var target = Assert.Single(result, device =>
            device.InstanceId.EndsWith("TARGET", StringComparison.Ordinal));
        var other = Assert.Single(result, device =>
            device.InstanceId.EndsWith("OTHER", StringComparison.Ordinal));
        Assert.Empty(target.CdRomNames);
        Assert.Empty(target.VolumeLabels);
        Assert.Contains("OTHER U-DISK", other.CdRomNames);
        Assert.Contains("UNRELATED", other.VolumeLabels);
    }
}
