using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Windows.Devices;

public static class WindowsDeviceSnapshotMapper
{
    public static IReadOnlyList<UsbDeviceDescriptor> Map(
        IEnumerable<WindowsDeviceRecord> records,
        IEnumerable<WindowsVolumeRecord> volumes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(volumes);

        var materializedRecords = records.ToArray();
        var materializedVolumes = volumes.ToArray();
        var recordsByInstanceId = materializedRecords
            .GroupBy(record => record.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return materializedRecords
            // Preserve physical USB membership even when service metadata is temporarily unavailable.
            .Where(record => IsUsbVendorProductDevice(record) ||
                record.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            .Select(record => MapUsbDevice(
                record,
                materializedRecords,
                materializedVolumes,
                recordsByInstanceId))
            .ToArray();
    }

    private static UsbDeviceDescriptor MapUsbDevice(
        WindowsDeviceRecord usbDevice,
        IReadOnlyList<WindowsDeviceRecord> records,
        IReadOnlyList<WindowsVolumeRecord> volumes,
        IReadOnlyDictionary<string, WindowsDeviceRecord> recordsByInstanceId)
    {
        var relatedCdRoms = records
            .Where(IsCdRom)
            .Where(record => IsDescendantOf(record, usbDevice.InstanceId, recordsByInstanceId))
            .ToArray();
        var cdRomNames = NonEmptyDistinct(relatedCdRoms.SelectMany(record =>
            new[] { record.FriendlyName, record.BusReportedDescription }));
        var cdRomInstanceIds = relatedCdRoms
            .Select(record => record.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relatedVolumes = volumes
            .Where(volume =>
                !string.IsNullOrWhiteSpace(volume.DeviceInstanceId) &&
                cdRomInstanceIds.Contains(volume.DeviceInstanceId!))
            .ToArray();

        return new(
            usbDevice.InstanceId,
            ResolveHardwareIds(usbDevice),
            usbDevice.BusReportedDescription,
            cdRomNames,
            NonEmptyDistinct(relatedVolumes.Select(volume => volume.VolumeLabel)),
            NonEmptyDistinct(relatedVolumes.Select(volume => volume.FileSystem)),
            usbDevice.Service);
    }

    private static bool IsDescendantOf(
        WindowsDeviceRecord record,
        string ancestorInstanceId,
        IReadOnlyDictionary<string, WindowsDeviceRecord> recordsByInstanceId)
    {
        var parentId = record.ParentInstanceId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
        {
            if (string.Equals(parentId, ancestorInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            parentId = recordsByInstanceId.TryGetValue(parentId, out var parent)
                ? parent.ParentInstanceId
                : null;
        }

        return false;
    }

    private static bool IsUsbVendorProductDevice(WindowsDeviceRecord record) =>
        ResolveHardwareIds(record).Any(id =>
            id.Contains("VID_", StringComparison.OrdinalIgnoreCase) &&
            id.Contains("PID_", StringComparison.OrdinalIgnoreCase));

    private static string[] ResolveHardwareIds(WindowsDeviceRecord record)
    {
        if (record.HardwareIds.Count > 0)
        {
            return record.HardwareIds.ToArray();
        }

        if (!record.InstanceId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var instanceSeparator = record.InstanceId.IndexOf('\\', "USB\\".Length);
        var stablePrefix = instanceSeparator >= 0
            ? record.InstanceId[..instanceSeparator]
            : record.InstanceId;
        return stablePrefix.Contains("PID_", StringComparison.OrdinalIgnoreCase)
            ? [stablePrefix]
            : [];
    }

    private static bool IsCdRom(WindowsDeviceRecord record) =>
        string.Equals(record.DeviceClass, "CDROM", StringComparison.OrdinalIgnoreCase) ||
        record.InstanceId.StartsWith("USBSTOR\\CDROM", StringComparison.OrdinalIgnoreCase);

    private static string[] NonEmptyDistinct(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
