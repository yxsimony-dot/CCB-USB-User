namespace UsbWebLauncher.Windows.Devices;

public sealed record WindowsDeviceRecord(
    string InstanceId,
    IReadOnlyList<string> HardwareIds,
    string? BusReportedDescription,
    string? FriendlyName,
    string? DeviceClass,
    string? ParentInstanceId = null,
    string? Service = null);

public sealed record WindowsVolumeRecord(
    string? VolumeLabel,
    string? FileSystem,
    string? DeviceInstanceId = null);
