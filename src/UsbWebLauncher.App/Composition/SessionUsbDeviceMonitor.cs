using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

// A business session belongs to the first physically present matching Key set.
// A replacement Key is a new explicit session, never a new insertion cycle in
// this monitor. Missing metadata for an existing physical node is not removal.
internal sealed class SessionUsbDeviceMonitor(
    IUsbDeviceMonitor inner,
    Func<UsbDeviceDescriptor, bool> matches,
    Action onDeviceRemoved) : IUsbDeviceMonitor
{
    private readonly object sync = new();
    private readonly HashSet<string> originalIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<UsbDeviceDescriptor> latestDevices = [];
    private long snapshotVersion;
    private bool subscribed;
    private bool ended;
    private bool disposed;
    private Task? disposal;

    public event UsbDevicesChangedEventHandler? DevicesChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ThrowIfEnded();
            cancellationToken.ThrowIfCancellationRequested();
            if (!subscribed)
            {
                inner.DevicesChanged += OnDevicesChangedAsync;
                subscribed = true;
            }
        }
        return inner.StartAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
    {
        long version;
        lock (sync)
        {
            ThrowIfEnded();
            version = snapshotVersion;
        }
        var devices = await inner.GetConnectedDevicesAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool removed;
        lock (sync)
        {
            ThrowIfEnded();
            // An event can finish a newer read while the startup read is pending.
            // Do not let its old result replace current presence or identity.
            if (version != snapshotVersion) return latestDevices;
            removed = ObserveSnapshot(devices);
        }
        if (removed)
        {
            onDeviceRemoved();
            throw EndedException();
        }
        return devices;
    }

    private async Task OnDevicesChangedAsync(object? sender, UsbDevicesChangedEventArgs args)
    {
        bool removed;
        lock (sync)
        {
            if (ended || disposed || !ReferenceEquals(sender, inner)) return;
            // A failed enumeration supplies no physical absence evidence.
            removed = args.Error is null && ObserveSnapshot(args.Devices);
        }
        if (removed)
        {
            // This must synchronously revoke session authorization. Its UI cleanup
            // is posted, not awaited: disposing the source awaits this callback.
            onDeviceRemoved();
            return;
        }

        foreach (var handler in DevicesChanged?.GetInvocationList().Cast<UsbDevicesChangedEventHandler>() ?? [])
        {
            Task delivery;
            lock (sync)
            {
                if (ended || disposed) return;
                delivery = handler(this, args);
            }
            await delivery.ConfigureAwait(false);
        }
    }

    // Caller holds sync. The initial ID set is deliberately not expanded when
    // another matching Key is inserted alongside the original one.
    private bool ObserveSnapshot(IReadOnlyList<UsbDeviceDescriptor> devices)
    {
        latestDevices = devices;
        snapshotVersion++;
        if (originalIds.Count == 0)
        {
            foreach (var device in devices.Where(matches)) originalIds.Add(device.InstanceId);
            return false;
        }
        if (devices.Any(device => originalIds.Contains(device.InstanceId))) return false;
        ended = true;
        return true;
    }

    private void ThrowIfEnded()
    {
        if (ended || disposed) throw EndedException();
    }

    private static OperationCanceledException EndedException() =>
        new("The business session's USB Key was removed or the session was stopped.", new CancellationToken(true));

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (disposal is not null) return new ValueTask(disposal);
            disposed = true;
            ended = true;
            if (subscribed) inner.DevicesChanged -= OnDevicesChangedAsync;
            DevicesChanged = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
