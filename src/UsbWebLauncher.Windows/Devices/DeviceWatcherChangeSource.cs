using Windows.Devices.Enumeration;

namespace UsbWebLauncher.Windows.Devices;

public sealed class DeviceWatcherChangeSource : IDeviceChangeSource
{
    private DeviceWatcher? _watcher;

    public event EventHandler? Changed;

    public event EventHandler<DeviceChangeSourceFaultedEventArgs>? Faulted;

    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = DeviceInformation.CreateWatcher();
        _watcher.Added += OnAdded;
        _watcher.Removed += OnRemoved;
        _watcher.Updated += OnUpdated;
        _watcher.Stopped += OnStopped;
        try
        {
            _watcher.Start();
        }
        catch
        {
            DetachAndClearWatcher();
            throw;
        }
    }

    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        var watcher = _watcher;
        DetachAndClearWatcher();
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }

    public void Dispose() => Stop();

    private void OnAdded(DeviceWatcher sender, DeviceInformation args) => RaiseChanged();

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => RaiseChanged();

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args) => RaiseChanged();

    private void OnStopped(DeviceWatcher sender, object args)
    {
        if (sender.Status == DeviceWatcherStatus.Aborted)
        {
            Faulted?.Invoke(
                this,
                new(new InvalidOperationException("Windows 设备监听意外终止。")));
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void DetachAndClearWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Added -= OnAdded;
        _watcher.Removed -= OnRemoved;
        _watcher.Updated -= OnUpdated;
        _watcher.Stopped -= OnStopped;
        _watcher = null;
    }
}
