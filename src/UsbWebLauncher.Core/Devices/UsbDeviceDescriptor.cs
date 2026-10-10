namespace UsbWebLauncher.Core.Devices;

public sealed record UsbDeviceDescriptor(
    string InstanceId,
    IReadOnlyList<string> HardwareIds,
    string? BusReportedDescription,
    IReadOnlyList<string> CdRomNames,
    IReadOnlyList<string> VolumeLabels,
    IReadOnlyList<string> FileSystems,
    string? Service = null);
