namespace UsbWebLauncher.Core.Devices;

public interface IUsbDeviceMonitor : IAsyncDisposable
{
    event UsbDevicesChangedEventHandler? DevicesChanged;

    Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);
}

public delegate Task UsbDevicesChangedEventHandler(
    object? sender,
    UsbDevicesChangedEventArgs eventArgs);

public sealed class UsbDevicesChangedEventArgs(
    IReadOnlyList<UsbDeviceDescriptor> devices,
    Exception? error = null) : EventArgs
{
    public IReadOnlyList<UsbDeviceDescriptor> Devices { get; } = devices;

    public Exception? Error { get; } = error;
}
