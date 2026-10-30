using System.Diagnostics;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.Windows.Devices;

public sealed class WindowsUsbDeviceMonitor : IUsbDeviceMonitor
{
    private readonly IWindowsUsbDeviceEnumerator _enumerator;
    private readonly IDeviceChangeSource _changeSource;
    private readonly TimeSpan _debounceDelay;
    private readonly bool _enableCcbMetadataRetry;
    private readonly TimeSpan _metadataRetryDelay;
    private readonly int _maxMetadataRetries;
    private readonly Action<string, Exception> _reportFault;
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
    private UsbDevicesChangedEventHandler? _devicesChanged;
    private Task? _disposal;

    public WindowsUsbDeviceMonitor(bool enableCcbMetadataRetry = false)
        : this(
            new SetupApiUsbDeviceEnumerator(),
            new DeviceWatcherChangeSource(),
            TimeSpan.FromMilliseconds(350), enableCcbMetadataRetry,
            metadataRetryDelay: null,
            maxMetadataRetries: 5,
            reportFault: ReportDefaultFault)
    {
    }

    public WindowsUsbDeviceMonitor(
        IWindowsUsbDeviceEnumerator enumerator,
        IDeviceChangeSource changeSource,
        TimeSpan debounceDelay,
        bool enableCcbMetadataRetry = false,
        TimeSpan? metadataRetryDelay = null,
        int maxMetadataRetries = 5)
        : this(
            enumerator,
            changeSource,
            debounceDelay,
            enableCcbMetadataRetry,
            metadataRetryDelay,
            maxMetadataRetries,
            ReportDefaultFault)
    {
    }

    internal WindowsUsbDeviceMonitor(
        IWindowsUsbDeviceEnumerator enumerator,
        IDeviceChangeSource changeSource,
        TimeSpan debounceDelay,
        bool enableCcbMetadataRetry,
        TimeSpan? metadataRetryDelay,
        int maxMetadataRetries,
        Action<string, Exception> reportFault)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _changeSource = changeSource ?? throw new ArgumentNullException(nameof(changeSource));
        _reportFault = reportFault ?? throw new ArgumentNullException(nameof(reportFault));
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

    public event UsbDevicesChangedEventHandler? DevicesChanged
    {
        add
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _devicesChanged += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                _devicesChanged -= value;
            }
        }
    }

    internal int TrackedRefreshCount
    {
        get
        {
            lock (_sync) return _refreshes.Count;
        }
    }

    internal bool DebounceDisposed { get; private set; }

    internal bool LifetimeDisposed { get; private set; }

    internal bool InitialReadGateDisposed { get; private set; }

    public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return _enableCcbMetadataRetry
            ? GetCurrentCcbSnapshotAsync(cancellationToken)
            : _enumerator.EnumerateAsync(cancellationToken);
    }

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
        lock (_sync)
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
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] refreshes;
        CancellationTokenSource? debounce;
        bool shouldStop;
        lock (_sync)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            _disposed = true;
            _devicesChanged = null;
            refreshes = _refreshes.ToArray();
            debounce = _debounce;
            shouldStop = _started;
            _started = false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }

        _ = DisposeCoreAsync(completion, refreshes, debounce, shouldStop);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(
        TaskCompletionSource completion,
        Task[] refreshes,
        CancellationTokenSource? debounce,
        bool shouldStop)
    {
        try
        {
            await CleanupStageRunner.RunAsync(
            [
                new("detach-change-source", () =>
                {
                    _changeSource.Changed -= OnDeviceChanged;
                    _changeSource.Faulted -= OnChangeSourceFaulted;
                    return ValueTask.CompletedTask;
                }),
                new("cancel-monitor", () =>
                {
                    _lifetime.Cancel();
                    debounce?.Cancel();
                    return ValueTask.CompletedTask;
                }),
                new("stop-change-source", () =>
                {
                    if (shouldStop) _changeSource.Stop();
                    return ValueTask.CompletedTask;
                }),
                new("settle-refreshes", () => new(SettleRefreshesAsync(refreshes))),
                new("settle-initial-read", () => new(SettleInitialReadAsync())),
                new("dispose-debounce", () =>
                {
                    debounce?.Dispose();
                    DebounceDisposed = true;
                    return ValueTask.CompletedTask;
                }),
                new("dispose-lifetime", () =>
                {
                    _lifetime.Dispose();
                    LifetimeDisposed = true;
                    return ValueTask.CompletedTask;
                }),
                new("dispose-initial-read-gate", () =>
                {
                    _initialReadGate.Dispose();
                    InitialReadGateDisposed = true;
                    return ValueTask.CompletedTask;
                }),
                new("dispose-change-source", () =>
                {
                    _changeSource.Dispose();
                    return ValueTask.CompletedTask;
                })
            ]).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task SettleRefreshesAsync(IEnumerable<Task> refreshes)
    {
        foreach (var refresh in refreshes)
        {
            try
            {
                await refresh.ConfigureAwait(false);
            }
            catch
            {
                // The per-task observer owns fault reporting. Disposal only settles it.
            }
        }
    }

    private async Task SettleInitialReadAsync()
    {
        await _initialReadGate.WaitAsync().ConfigureAwait(false);
        _initialReadGate.Release();
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
        ReportFault("usb-change-source", eventArgs.Exception);

        _changeSource.Changed -= OnDeviceChanged;
        _changeSource.Faulted -= OnChangeSourceFaulted;
        try
        {
            _changeSource.Stop();
        }
        catch (Exception exception)
        {
            ReportFault("usb-change-source-stop", exception);
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                Track(PublishAsync(SnapshotSubscribers(), [], eventArgs.Exception));
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
                var shouldPublishAbsence = false;
                lock (_sync)
                {
                    if (IsCurrent(generation, cancellationToken) &&
                        _lastReportedCcbInstanceIds.Count != 0 &&
                        !snapshot.Any(device => _lastReportedCcbInstanceIds.Contains(device.InstanceId)) &&
                        !snapshot.Any(UsbDeviceMatcher.IsCcbUsbStorMatch))
                    {
                        reportedAbsence = true;
                        shouldPublishAbsence = true;
                    }
                }

                // Only physical disappearance warrants an immediate absence. If
                // the same node remains, let bounded metadata retries finish so
                // a transient CD query cannot reset an authentication attempt.
                return shouldPublishAbsence
                    ? PublishCurrentAsync(snapshot, null, generation, cancellationToken)
                    : Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            ReportFault("usb-enumeration", exception);
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
        UsbDevicesChangedEventHandler[] subscribers;
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

            subscribers = SnapshotSubscribers();
        }

        return PublishAsync(subscribers, devices, error);
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
        catch (Exception exception)
        {
            ReportFault("usb-refresh", exception);
        }
        finally
        {
            lock (_sync)
            {
                _refreshes.Remove(task);
            }
        }
    }

    private UsbDevicesChangedEventHandler[] SnapshotSubscribers() =>
        _devicesChanged?.GetInvocationList()
            .Cast<UsbDevicesChangedEventHandler>()
            .ToArray() ?? [];

    private async Task PublishAsync(
        IReadOnlyList<UsbDevicesChangedEventHandler> subscribers,
        IReadOnlyList<UsbDeviceDescriptor> devices,
        Exception? error)
    {
        await Task.Yield();
        if (subscribers.Count == 0)
        {
            return;
        }

        var args = new UsbDevicesChangedEventArgs(devices, error);
        for (var index = 0; index < subscribers.Count; index++)
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            try
            {
                await subscribers[index](this, args).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportFault($"usb-subscriber-{index}", exception);
            }
        }
    }

    private void ReportFault(string operation, Exception exception)
    {
        try
        {
            _reportFault(operation, exception);
        }
        catch
        {
            // Diagnostics cannot alter monitor ownership or publication.
        }
    }

    private static void ReportDefaultFault(string operation, Exception exception) =>
        Trace.TraceError($"{operation}: {exception.GetType().Name}");
}
