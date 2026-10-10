using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Windows.Devices;

public sealed class WindowsUsbDeviceMonitor : IUsbDeviceMonitor
{
    private readonly IWindowsUsbDeviceEnumerator _enumerator;
    private readonly IDeviceChangeSource _changeSource;
    private readonly TimeSpan _debounceDelay;
    private readonly bool _enableCcbMetadataRetry;
    private readonly TimeSpan _metadataRetryDelay;
    private readonly int _maxMetadataRetries;
    private readonly SemaphoreSlim _initialReadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _refreshes = [];
    private CancellationTokenSource? _debounce;
    private long _refreshGeneration;
    private bool _started;
    private bool _disposed;
    private bool _initialReadInProgress;
    private HashSet<string> _lastReportedCcbInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _monitorFault;

    public WindowsUsbDeviceMonitor(bool enableCcbMetadataRetry = false)
        : this(
            new SetupApiUsbDeviceEnumerator(),
            new DeviceWatcherChangeSource(),
            TimeSpan.FromMilliseconds(350), enableCcbMetadataRetry)
    {
    }

    public WindowsUsbDeviceMonitor(
        IWindowsUsbDeviceEnumerator enumerator,
        IDeviceChangeSource changeSource,
        TimeSpan debounceDelay,
        bool enableCcbMetadataRetry = false,
        TimeSpan? metadataRetryDelay = null,
        int maxMetadataRetries = 5)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _changeSource = changeSource ?? throw new ArgumentNullException(nameof(changeSource));
        _debounceDelay = debounceDelay >= TimeSpan.Zero
            ? debounceDelay
            : throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        _enableCcbMetadataRetry = enableCcbMetadataRetry;
        _metadataRetryDelay = metadataRetryDelay ?? TimeSpan.FromSeconds(1);
        if (_metadataRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(metadataRetryDelay));
        }

        _maxMetadataRetries = maxMetadataRetries >= 0
            ? maxMetadataRetries
            : throw new ArgumentOutOfRangeException(nameof(maxMetadataRetries));
    }

    public event UsbDevicesChangedEventHandler? DevicesChanged;

    public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default) =>
        _enableCcbMetadataRetry
            ? GetCurrentCcbSnapshotAsync(cancellationToken)
            : _enumerator.EnumerateAsync(cancellationToken);

    private async Task<IReadOnlyList<UsbDeviceDescriptor>> GetCurrentCcbSnapshotAsync(
        CancellationToken cancellationToken)
    {
        CancellationToken lifetimeToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetimeToken = _lifetime.Token;
        }

        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        await _initialReadGate.WaitAsync(readCancellation.Token).ConfigureAwait(false);
        try
        {
            long firstGeneration;
            lock (_sync)
            {
                readCancellation.Token.ThrowIfCancellationRequested();
                ReplaceRefreshCancellation();
                firstGeneration = _refreshGeneration;
                _initialReadInProgress = true;
            }

            while (true)
            {
                long generation;
                CancellationToken refreshToken;
                lock (_sync)
                {
                    readCancellation.Token.ThrowIfCancellationRequested();
                    if (_monitorFault is not null)
                    {
                        throw _monitorFault;
                    }

                    generation = _refreshGeneration;
                    refreshToken = _debounce!.Token;
                }

                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(readCancellation.Token, refreshToken);
                try
                {
                    // While a caller is reading the initial snapshot, events invalidate
                    // that read instead of racing it with a separate publication.
                    if (generation != firstGeneration)
                    {
                        await Task.Delay(_debounceDelay, attemptCancellation.Token).ConfigureAwait(false);
                    }

                    var devices = await EnumerateSnapshotAsync(attemptCancellation.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (IsCurrent(generation, attemptCancellation.Token))
                        {
                            RememberMatchingInstanceIds(devices);
                            _initialReadInProgress = false;
                            return devices;
                        }
                    }
                }
                catch (Exception) when (
                    refreshToken.IsCancellationRequested && !readCancellation.IsCancellationRequested)
                {
                    // A newer event owns the next scan; never return an obsolete result.
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _initialReadInProgress = false;
            }

            _initialReadGate.Release();
        }
    }

    private async Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateSnapshotAsync(
        CancellationToken cancellationToken,
        Func<IReadOnlyList<UsbDeviceDescriptor>, Task>? onSnapshot = null)
    {
        if (!_enableCcbMetadataRetry)
        {
            return await _enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Native enumeration may finish after cancellation. Stop awaiting it so
            // an obsolete scan cannot delay removal, a newer scan, or shutdown.
            var devices = await _enumerator.EnumerateAsync(cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (onSnapshot is not null)
            {
                await onSnapshot(devices).ConfigureAwait(false);
            }

            if (retry >= _maxMetadataRetries ||
                devices.Any(UsbDeviceMatcher.IsCcbUsbStorMatch) ||
                !devices.Any(device =>
                    device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(device.Service, "USBSTOR", StringComparison.OrdinalIgnoreCase)))
            {
                return devices;
            }

            await Task.Delay(_metadataRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReplaceRefreshCancellation()
    {
        var previous = _debounce;
        _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshGeneration++;
        // Cancellation can resume the initial read synchronously on this thread.
        // It must observe the replacement rather than loop on the canceled token.
        previous?.Cancel();
        previous?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_started)
        {
            return Task.CompletedTask;
        }

        _changeSource.Changed += OnDeviceChanged;
        _changeSource.Faulted += OnChangeSourceFaulted;
        try
        {
            _changeSource.Start();
            _started = true;
            _monitorFault = null;
        }
        catch
        {
            _changeSource.Changed -= OnDeviceChanged;
            _changeSource.Faulted -= OnChangeSourceFaulted;
            try
            {
                _changeSource.Stop();
            }
            catch
            {
            }

            throw;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] refreshes;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            _debounce?.Cancel();
            refreshes = _refreshes.ToArray();
        }

        if (_started)
        {
            _changeSource.Changed -= OnDeviceChanged;
            _changeSource.Faulted -= OnChangeSourceFaulted;
            _changeSource.Stop();
        }

        try
        {
            await Task.WhenAll(refreshes).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _debounce?.Dispose();
        _lifetime.Dispose();
        _changeSource.Dispose();
    }

    private void OnDeviceChanged(object? sender, EventArgs eventArgs)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var initialReadOwnsRefresh = _initialReadInProgress;
            ReplaceRefreshCancellation();
            if (!initialReadOwnsRefresh)
            {
                Track(RefreshAfterDelayAsync(_refreshGeneration, _debounce!.Token));
            }
        }
    }

    private void OnChangeSourceFaulted(
        object? sender,
        DeviceChangeSourceFaultedEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _started = false;
            _monitorFault = eventArgs.Exception;
            _debounce?.Cancel();
            _refreshGeneration++;
        }

        _changeSource.Changed -= OnDeviceChanged;
        _changeSource.Faulted -= OnChangeSourceFaulted;
        try
        {
            _changeSource.Stop();
        }
        catch
        {
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                Track(PublishAsync([], eventArgs.Exception));
            }
        }
    }

    private async Task RefreshAfterDelayAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        IReadOnlyList<UsbDeviceDescriptor> devices;
        var reportedAbsence = false;
        try
        {
            devices = await EnumerateSnapshotAsync(cancellationToken, snapshot =>
            {
                lock (_sync)
                {
                    if (!IsCurrent(generation, cancellationToken) ||
                        _lastReportedCcbInstanceIds.Count == 0 ||
                        snapshot.Any(device => _lastReportedCcbInstanceIds.Contains(device.InstanceId)) ||
                        snapshot.Any(UsbDeviceMatcher.IsCcbUsbStorMatch))
                    {
                        return Task.CompletedTask;
                    }

                    // Only physical disappearance warrants an immediate absence. If
                    // the same node remains, let bounded metadata retries finish so
                    // a transient CD query cannot reset an authentication attempt.
                    reportedAbsence = true;
                    return PublishCurrentAsync(snapshot, null, generation, cancellationToken);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await PublishCurrentAsync([], exception, generation, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (!reportedAbsence || devices.Any(UsbDeviceMatcher.IsCcbUsbStorMatch))
        {
            await PublishCurrentAsync(devices, null, generation, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task PublishCurrentAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        Exception? error,
        long generation,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!IsCurrent(generation, cancellationToken))
            {
                return Task.CompletedTask;
            }

            if (_enableCcbMetadataRetry && error is null)
            {
                RememberMatchingInstanceIds(devices);
            }

            return PublishAsync(devices, error);
        }
    }

    private void RememberMatchingInstanceIds(IReadOnlyList<UsbDeviceDescriptor> devices)
    {
        // Retain only previously matched nodes still physically present, even
        // after metadata retry exhaustion, so a later removal ends that cycle
        // promptly. This set never substitutes for current Service/CD evidence.
        _lastReportedCcbInstanceIds.IntersectWith(devices.Select(device => device.InstanceId));
        _lastReportedCcbInstanceIds.UnionWith(devices
            .Where(UsbDeviceMatcher.IsCcbUsbStorMatch)
            .Select(device => device.InstanceId));
    }

    private bool IsCurrent(long generation, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return !_disposed &&
                   !cancellationToken.IsCancellationRequested &&
                   generation == _refreshGeneration;
        }
    }

    private void Track(Task task)
    {
        _refreshes.Add(task);
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                _refreshes.Remove(task);
            }
        }
    }

    private async Task PublishAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        Exception? error)
    {
        if (DevicesChanged is null)
        {
            return;
        }

        var args = new UsbDevicesChangedEventArgs(devices, error);
        foreach (UsbDevicesChangedEventHandler handler in DevicesChanged.GetInvocationList())
        {
            await handler(this, args).ConfigureAwait(false);
        }
    }
}
