using System.Globalization;

namespace UsbWebLauncher.Core.Devices;

// VID/PID pairs recognized by E路护航's present-device Dev2KeyType classifier.
// These identify compatible device families, not a credential or a unique Key.
internal static class CcbDetectorDeviceCatalog
{
    private static readonly HashSet<uint> Pairs =
    [
        0x163C0406, 0x163C0407, 0x163C0402, 0x163C5471, 0x163C548C,
        0x8829CCB0, 0x8829CCB1, 0x8829CCB2, 0x8829CCB3,
        0x1780FE33, 0x17800410, 0x17800401,
        0x16770028, 0x16770050, 0x16770054, 0x16770057,
        0x096E0707, 0x1EA8C03E, 0x14D63766, 0x14D63769
    ];

    public static bool IsMatch(UsbDeviceDescriptor device)
    {
        if (!device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (device.HardwareIds.Count != 0) return device.HardwareIds.Any(MatchesHardwareSegment);
        return MatchesHardwareSegment(device.InstanceId);
    }

    private static bool MatchesHardwareSegment(string identifier)
    {
        if (!identifier.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return false;
        // Only USB's hardware segment is evidence. The following backslash starts
        // a serial/instance identifier and must never supply VID/PID tokens.
        var hardware = identifier.AsSpan(4);
        var serialSeparator = hardware.IndexOf('\\');
        if (serialSeparator >= 0) hardware = hardware[..serialSeparator];
        ushort? vendor = null, product = null;
        foreach (var token in hardware.ToString().Split('&'))
        {
            if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
            {
                if (vendor is not null || !TryParseToken(token, out var value)) return false;
                vendor = value;
            }
            else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
            {
                if (product is not null || !TryParseToken(token, out var value)) return false;
                product = value;
            }
        }
        return vendor is not null && product is not null &&
            Pairs.Contains(((uint)vendor.Value << 16) | product.Value);
    }

    private static bool TryParseToken(string token, out ushort value)
    {
        value = 0;
        return token.Length == 8 && ushort.TryParse(token.AsSpan(4),
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
