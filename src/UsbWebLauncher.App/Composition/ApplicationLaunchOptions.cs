using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

internal sealed record ApplicationLaunchOptions(bool UseEmptyDeviceMonitor)
{
    internal const string SmokeTestNoDevicesArgument = "--smoke-test-no-devices";
    internal const string SmokeReadyWindowTitle = "USB Web Launcher [Smoke Ready]";

    public static ApplicationLaunchOptions Parse(IEnumerable<string>? arguments) =>
        new(arguments?.Any(argument => string.Equals(
            argument,
            SmokeTestNoDevicesArgument,
            StringComparison.OrdinalIgnoreCase)) == true);
}

internal sealed class EmptyUsbDeviceMonitor : IUsbDeviceMonitor
{
    public event UsbDevicesChangedEventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<UsbDeviceDescriptor>>([]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
