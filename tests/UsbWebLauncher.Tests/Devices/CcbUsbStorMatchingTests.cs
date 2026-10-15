using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Windows.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class CcbUsbStorMatchingTests
{
    [Fact]
    public void Map_UnknownUsbNodeWithMissingServiceRemainsPresentButDoesNotMatch()
    {
        var usb = new WindowsDeviceRecord("USB\\FUTURE\\SYNTHETIC", [], "Future model", null, "USB", Service: "USBSTOR");
        var cd = new WindowsDeviceRecord("USBSTOR\\CDROM\\SYNTHETIC", [], "CCB", null, "CDROM", usb.InstanceId);
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(Assert.Single(WindowsDeviceSnapshotMapper.Map([usb, cd], []))));

        var current = Assert.Single(WindowsDeviceSnapshotMapper.Map([usb with { Service = null }, cd], []));

        Assert.Equal(usb.InstanceId, current.InstanceId);
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(current));
    }

    [Theory]
    [InlineData("HID\\VID_1677&PID_0054\\SYNTHETIC")]
    [InlineData("ROOT\\STORAGE\\SYNTHETIC")]
    [InlineData("USBSTOR\\CDROM\\SYNTHETIC")]
    public void Match_RejectsNonUsbNodeEvenWithUsbStorServiceAndCcbEvidence(string instanceId)
    {
        var device = new UsbDeviceDescriptor(instanceId, ["USB\\VID_1677&PID_0054"], "Model", ["CCB"], ["CCB"], [], "USBSTOR");
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
    }

    // Four captured samples, using synthetic instance/serial identifiers only.
    [Theory]
    [InlineData("SAMPLE-01", "1780", "0401", "0001", "Tendyron Token", "TENDYRON U-DISK USB Device", "CCB")]
    [InlineData("SAMPLE-02", "1780", "0401", "0001", "Tendyron Token", "TENDYRON U-DISK USB Device", "CCB")]
    [InlineData("SAMPLE-03", "1677", "0054", "0100", "uKeyCI800-K54", "HDZB-CCB USB AutoRunDisk USB Device", "HDZB_USBKEY_KEY")]
    [InlineData("SAMPLE-04", "1677", "0054", "0103", "uKeyCI800-K156", "HDZB-CCB USB AutoRunDisk USB Device", "HDZB_USBKEY_KEY")]
    [InlineData("SAMPLE-05", "163C", "0620", "0001", "USB Key", "Watch Autorun CDROM USB Device", "\u5EFA\u884C\u7F51\u94F6\u76FE")]
    [InlineData("NEW-PORT", "ABCD", "9876", "9999", "Future model", "new cCb virtual CD", "OTHER")]
    public void MapAndMatch_AcceptsBankEvidenceAcrossModelsAndRevisions(
        string instance, string vendor, string product, string revision, string model, string cdName, string label)
    {
        var usbId = $"USB\\VID_{vendor}&PID_{product}\\{instance}";
        var cdId = $"USBSTOR\\CDROM\\{instance}";
        WindowsDeviceRecord[] records =
        [
            new(usbId, [$"USB\\VID_{vendor}&PID_{product}&REV_{revision}"], model, "Storage", "USB", Service: "USBSTOR"),
            new(cdId, [], cdName, cdName, "CDROM", usbId)
        ];

        var device = Assert.Single(WindowsDeviceSnapshotMapper.Map(records, [new(label, "UDF", cdId)]));

        Assert.Equal("USBSTOR", device.Service);
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
        Assert.True(UsbDeviceMatcher.IsMatch(device, new LauncherProfile("https://example.com", UsbDetectionStrategy: "ccbUsbStor")));
    }

    [Theory]
    [InlineData("USBSTOR", "x-CCB-y", null, true)]
    [InlineData("usbstor", null, "x-ccb-y", true)]
    [InlineData("USBSTOR", null, "\u5EFA\u884C\u7F51\u94F6\u76FE", true)]
    [InlineData("USBSTOR", null, "2026-\u5EFA\u884C\u7F51\u94F6\u76FE", true)]
    [InlineData("USBSTOR", null, "\u5EFA\u884C", false)]
    [InlineData("USBSTOR", null, "\u7F51\u94F6\u76FE", false)]
    [InlineData("USBSTOR", "Other", "Other", false)]
    [InlineData(null, "CCB", "CCB", false)]
    [InlineData("", "CCB", "CCB", false)]
    [InlineData("USBCCGP", "CCB", "CCB", false)]
    [InlineData("USBSTOR-extra", "CCB", "CCB", false)]
    [InlineData("USBSTOR", null, null, false)]
    public void Match_RequiresExactServiceAndEitherBankNameOrBankLabel(string? service, string? cd, string? label, bool expected)
    {
        var device = new UsbDeviceDescriptor("USB\\NEW_MODEL\\PORT", [], "CCB USBSTOR", cd is null ? [] : [cd],
            label is null ? [] : [label], ["NTFS"], service);

        Assert.Equal(expected, UsbDeviceMatcher.IsCcbUsbStorMatch(device));
    }

    [Fact]
    public void Map_AcceptsUsbStorNodeWithoutVendorProductTokens()
    {
        WindowsDeviceRecord[] records =
        [
            new("usb\\NEW_MODEL\\PORT", [], "New model", null, "USB", Service: "usbstor"),
            new("USBSTOR\\CDROM\\CHILD", [], "CCB", null, "CDROM", "usb\\NEW_MODEL\\PORT")
        ];

        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(Assert.Single(WindowsDeviceSnapshotMapper.Map(records, []))));
    }

    [Fact]
    public void Map_DoesNotAcceptNonUsbNodeMerelyBecauseItsServiceIsUsbStor()
    {
        var records = new[] { new WindowsDeviceRecord("ROOT\\STORAGE\\PORT", [], "CCB", null, "USB", Service: "USBSTOR") };
        Assert.Empty(WindowsDeviceSnapshotMapper.Map(records, []));
    }

    [Fact]
    public void Map_AssociatesOnlyDescendantCdAndItsExactVolume()
    {
        WindowsDeviceRecord[] records =
        [
            new("USB\\VID_1111&PID_1111\\TARGET", [], "Target", null, "USB", Service: "USBSTOR"),
            new("USB\\VID_2222&PID_2222\\OTHER", [], "Other", null, "USB", Service: "USBSTOR"),
            new("STORAGE\\BRIDGE\\TARGET", [], null, null, null, "USB\\VID_1111&PID_1111\\TARGET"),
            new("USBSTOR\\CDROM\\TARGET", [], "Generic", "Generic", "CDROM", "STORAGE\\BRIDGE\\TARGET"),
            new("USBSTOR\\CDROM\\OTHER", [], "CCB", "CCB", "CDROM", "USB\\VID_2222&PID_2222\\OTHER")
        ];
        var volumes = new[] { new WindowsVolumeRecord("CCB", "CDFS", "USBSTOR\\CDROM\\OTHER"), new WindowsVolumeRecord("CCB", "CDFS") };
        var devices = WindowsDeviceSnapshotMapper.Map(records, volumes);
        var target = Assert.Single(devices, device => device.InstanceId.EndsWith("TARGET"));

        Assert.Contains("Generic", target.CdRomNames);
        Assert.Empty(target.VolumeLabels);
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(target));
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(Assert.Single(devices, device => device.InstanceId.EndsWith("OTHER"))));
    }

    [Fact]
    public void ProfileMatch_CcbModeCannotBeBypassedByLegacyAllowlist()
    {
        var device = new UsbDeviceDescriptor("USB\\VID_9999&PID_9999\\SAMPLE", ["USB\\VID_9999&PID_9999"], "Model", [], [], []);
        var profile = new LauncherProfile("https://example.com", new("9999", "9999", "Model"),
            AdditionalUsbDevices: [new("9999", "9999", "Model")]);

        Assert.True(UsbDeviceMatcher.IsMatch(device, profile));
        Assert.False(UsbDeviceMatcher.IsMatch(device, profile with { UsbDetectionStrategy = "ccbUsbStor" }));
        Assert.False(UsbDeviceMatcher.IsMatch(device, profile with { UsbDetectionStrategy = "unknown" }));
    }

    [Fact]
    public void Match_SameInstanceIdDoesNotReusePreviousEvidence()
    {
        var device = new UsbDeviceDescriptor("USB\\MODEL\\SAME", [], null, ["CCB"], [], [], "USBSTOR");
        Assert.True(UsbDeviceMatcher.IsCcbUsbStorMatch(device));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(device with { CdRomNames = [] }));
        Assert.False(UsbDeviceMatcher.IsCcbUsbStorMatch(device with { Service = null }));
    }
}
