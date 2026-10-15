using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Devices;

public static class UsbDeviceMatcher
{
    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] CcbStorageMarkers = ["CCB", "\u5EFA\u884C\u7F51\u94F6\u76FE"];

    public static bool IsCcbUsbStorMatch(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        // Keep the historical entry point/strategy name so the monitor's metadata
        // retries and application presence checks use the same compatibility rule.
        return device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
               (CcbDetectorDeviceCatalog.IsMatch(device) ||
                (TextComparer.Equals(device.Service, "USBSTOR") &&
                 (ContainsCcbStorageMarker(device.CdRomNames) ||
                  ContainsCcbStorageMarker(device.VolumeLabels))));
    }

    public static bool IsMatch(UsbDeviceDescriptor device, LauncherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        return profile.UsbDetectionStrategy switch
        {
            "ccbUsbStor" => IsCcbUsbStorMatch(device),
            "legacyAllowlist" => profile.GetUsbDeviceAllowlist().Any(options => IsMatch(device, options)),
            _ => false
        };
    }

    public static bool IsMatch(UsbDeviceDescriptor device, UsbMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        var vendorToken = $"VID_{options.VendorId}";
        var productToken = $"PID_{options.ProductId}";
        var hardwareMatches = device.HardwareIds.Any(id =>
            id.Contains(vendorToken, StringComparison.OrdinalIgnoreCase) &&
            id.Contains(productToken, StringComparison.OrdinalIgnoreCase));

        return hardwareMatches &&
               TextComparer.Equals(device.BusReportedDescription, options.BusReportedDescription) &&
               ContainsOptionalFragment(device.CdRomNames, options.CdRomName) &&
               ContainsOptionalValue(device.VolumeLabels, options.VolumeLabel) &&
               ContainsOptionalValue(device.FileSystems, options.FileSystem);
    }

    private static bool ContainsOptionalFragment(
        IEnumerable<string> values,
        string? expected)
    {
        return string.IsNullOrWhiteSpace(expected) ||
               values.Any(value => value.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsOptionalValue(
        IEnumerable<string> values,
        string? expected)
    {
        return string.IsNullOrWhiteSpace(expected) || values.Contains(expected, TextComparer);
    }

    private static bool ContainsCcbStorageMarker(IEnumerable<string> values) =>
        values.Any(value => CcbStorageMarkers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase)));
}
