using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.App.Composition;

// A business session belongs to the first physically present matching Key set.
// A replacement Key is a new explicit session, never a new insertion cycle in
// this monitor. Missing metadata for an existing physical node is not removal.
internal sealed class SessionUsbDeviceMonitor(
    IUsbDeviceMonitor inner,
    Func<UsbDeviceDescriptor, bool> matches,
    Action onDeviceRemoved,
    Action<string, Exception>? reportFault = null) : IUsbDeviceMonitor
{
    private readonly object sync = new();
    private readonly HashSet<string> originalIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<UsbDeviceDescriptor> latestDevices = [];
    private long snapshotVersion;
    private bool subscribed;
    private bool ended;
    private bool disposed;
    private Task? disposal;
    private UsbDevicesChangedEventHandler? devicesChanged;
    private readonly Action<string, Exception> reportFault =
        reportFault ?? ReportDefaultFault;

    public event UsbDevicesChangedEventHandler? DevicesChanged
    {
        add
        {
            lock (sync)
            {
                ThrowIfEnded();
                devicesChanged += value;
            }
        }
        remove
        {
            lock (sync)
            {
                devicesChanged -= value;
            }
        }
    }

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
        UsbDevicesChangedEventHandler[] subscribers;
        lock (sync)
        {
            if (ended || disposed || !ReferenceEquals(sender, inner)) return;
            // A failed enumeration supplies no physical absence evidence.
            removed = args.Error is null && ObserveSnapshot(args.Devices);
            subscribers = devicesChanged?.GetInvocationList()
                .Cast<UsbDevicesChangedEventHandler>()
                .ToArray() ?? [];
        }
        if (removed)
        {
            // This must synchronously revoke session authorization. Its UI cleanup
            // is posted, not awaited: disposing the source awaits this callback.
            onDeviceRemoved();
            return;
        }

        for (var index = 0; index < subscribers.Length; index++)
        {
            lock (sync)
            {
                if (ended || disposed) return;
            }

            try
            {
                await subscribers[index](this, args).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportFault($"session-usb-subscriber-{index}", exception);
            }
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
            devicesChanged = null;
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

    private void ReportFault(string operation, Exception exception)
    {
        try
        {
            reportFault(operation, exception);
        }
        catch
        {
            // Diagnostics cannot alter session revocation or subscriber delivery.
        }
    }

    private static void ReportDefaultFault(string operation, Exception exception) =>
        CertificateLoginDiagnostics.Write(
            "background-task-error",
            new { operation, exceptionType = exception.GetType().Name });
}
