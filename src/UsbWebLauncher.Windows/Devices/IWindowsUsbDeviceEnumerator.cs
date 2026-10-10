using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Windows.Devices;

public interface IWindowsUsbDeviceEnumerator
{
    Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(
        CancellationToken cancellationToken = default);
}

public interface IDeviceChangeSource : IDisposable
{
    event EventHandler? Changed;

    event EventHandler<DeviceChangeSourceFaultedEventArgs>? Faulted;

    void Start();

    void Stop();
}

public sealed class DeviceChangeSourceFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
