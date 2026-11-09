namespace UsbWebLauncher.Core.Devices;

public interface IUsbMetadataRetryPolicy
{
    bool IsEnabled { get; }

    bool IsMetadataReady(UsbDeviceDescriptor device);

    bool IsRetryCandidate(UsbDeviceDescriptor device);
}

public sealed class DisabledUsbMetadataRetryPolicy : IUsbMetadataRetryPolicy
{
    public static DisabledUsbMetadataRetryPolicy Instance { get; } = new();

    private DisabledUsbMetadataRetryPolicy()
    {
    }

    public bool IsEnabled => false;

    public bool IsMetadataReady(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return false;
    }

    public bool IsRetryCandidate(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return false;
    }
}

public sealed class CcbUsbMetadataRetryPolicy : IUsbMetadataRetryPolicy
{
    public static CcbUsbMetadataRetryPolicy Instance { get; } = new();

    private CcbUsbMetadataRetryPolicy()
    {
    }

    public bool IsEnabled => true;

    public bool IsMetadataReady(UsbDeviceDescriptor device) =>
        UsbDeviceMatcher.IsCcbUsbStorMatch(device);

    public bool IsRetryCandidate(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(device.Service, "USBSTOR", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PsbcUsbMetadataRetryPolicy : IUsbMetadataRetryPolicy
{
    public static PsbcUsbMetadataRetryPolicy Instance { get; } = new();

    private PsbcUsbMetadataRetryPolicy()
    {
    }

    public bool IsEnabled => true;

    public bool IsMetadataReady(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(device);

    public bool IsRetryCandidate(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
               PsbcUsbKeyMatcher.IsCandidate(device);
    }
}
