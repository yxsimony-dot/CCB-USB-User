using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

internal sealed record PsbcUsbKeyDetectionSnapshot(
    UsbKeyDetectionStatus Status,
    IReadOnlyList<UsbKeyDetectionResult> Results,
    IReadOnlyList<UsbDeviceDescriptor> ReadyDevices)
{
    public static PsbcUsbKeyDetectionSnapshot Create(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        IReadOnlyList<UsbKeyDetectionResult> results)
    {
        var readyIds = results
            .Where(result => result.Status == UsbKeyDetectionStatus.Ready)
            .Select(result => result.PhysicalInstanceId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readyDevices = devices
            .Where(device => readyIds.Contains(device.InstanceId))
            .ToArray();
        var status = readyDevices.Length > 0
            ? UsbKeyDetectionStatus.Ready
            : results.Any(result =>
                result.Status == UsbKeyDetectionStatus.PresentButEnvironmentUnavailable)
                ? UsbKeyDetectionStatus.PresentButEnvironmentUnavailable
                : UsbKeyDetectionStatus.Absent;
        return new(status, results.ToArray(), readyDevices);
    }
}

internal sealed class PsbcUsbKeyDetectionChangedEventArgs(
    PsbcUsbKeyDetectionSnapshot? snapshot,
    Exception? error = null) : EventArgs
{
    public PsbcUsbKeyDetectionSnapshot? Snapshot { get; } = snapshot;
    public Exception? Error { get; } = error;
}

internal sealed class PsbcUsbKeyDeviceMonitor :
    IUsbDeviceMonitor,
    IPsbcAuthenticationRuntimeContext
{
    private readonly IUsbDeviceMonitor inner;
    private readonly IPsbcUsbKeyDetector detector;
    private readonly BankRuntimeCallbackGuard? runtimeGuard;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly HashSet<Task> activeRefreshes = [];
    private readonly CancellationTokenRegistration runtimeCancellationRegistration;
    private UsbDevicesChangedEventHandler? devicesChanged;
    private EventHandler<PsbcUsbKeyDetectionChangedEventArgs>? detectionChanged;
    private EventHandler? authenticationContextChanged;
    private CancellationTokenSource? refreshCancellation;
    private RefreshOperation? currentRefresh;
    private PsbcUsbKeyDetectionSnapshot? latestSnapshot;
    private Exception? latestError;
    private PsbcAuthenticationRuntimeSnapshot authenticationContext;
    private long inputVersion;
    private long processedVersion = -1;
    private long authenticationContextRevision;
    private bool started;
    private bool subscribed;
    private bool disposed;
    private Task? disposal;

    public PsbcUsbKeyDeviceMonitor(
        IUsbDeviceMonitor inner,
        IPsbcUsbKeyDetector detector,
        BankRuntimeCallbackGuard? runtimeGuard = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.runtimeGuard = runtimeGuard;
        authenticationContext = CreateAuthenticationContext(
            snapshot: null,
            error: null,
            contextRevision: ++authenticationContextRevision);
        runtimeCancellationRegistration = runtimeGuard?.Owner.Session.Token.Register(
            static state =>
                ((PsbcUsbKeyDeviceMonitor)state!).RefreshAuthenticationContext(),
            this) ?? default;
    }

    public event UsbDevicesChangedEventHandler? DevicesChanged
    {
        add
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                devicesChanged += value;
            }
        }
        remove
        {
            lock (sync) devicesChanged -= value;
        }
    }

    public event EventHandler<PsbcUsbKeyDetectionChangedEventArgs>? DetectionChanged
    {
        add
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                detectionChanged += value;
            }
        }
        remove
        {
            lock (sync) detectionChanged -= value;
        }
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                authenticationContextChanged += value;
            }
        }
        remove
        {
            lock (sync) authenticationContextChanged -= value;
        }
    }

    public PsbcAuthenticationRuntimeSnapshot Current
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                RefreshAuthenticationContextLocked(out _);
                return authenticationContext;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (started)
            {
                return;
            }

            if (!subscribed)
            {
                inner.DevicesChanged += OnDevicesChangedAsync;
                subscribed = true;
            }

            started = true;
        }

        try
        {
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (sync) started = false;
            throw;
        }
    }

    public async Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadDetectionAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.ReadyDevices;
    }

    public async Task<PsbcUsbKeyDetectionSnapshot> ReadDetectionAsync(
        CancellationToken cancellationToken = default)
    {
        long observedVersion;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            observedVersion = inputVersion;
        }

        var devices = await inner
            .GetConnectedDevicesAsync(cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var operation = TryBeginRefresh(observedVersion, cancellationToken);
        if (operation is null)
        {
            return await WaitForCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        Track(ProcessRefreshAsync(operation, devices, error: null, publish: false));
        await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await WaitForCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task OnDevicesChangedAsync(object? sender, UsbDevicesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, inner))
        {
            return Task.CompletedTask;
        }

        var operation = BeginEventRefresh();
        if (operation is null)
        {
            return Task.CompletedTask;
        }

        var refresh = ProcessRefreshAsync(
            operation,
            args.Devices,
            args.Error,
            publish: true);
        Track(refresh);
        return refresh;
    }

    private RefreshOperation? TryBeginRefresh(
        long expectedVersion,
        CancellationToken callerToken)
    {
        CancellationTokenSource? previous;
        RefreshOperation operation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (inputVersion != expectedVersion)
            {
                return null;
            }

            previous = refreshCancellation;
            refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                callerToken);
            operation = new(expectedVersion, refreshCancellation);
            currentRefresh = operation;
        }

        Cancel(previous);
        return operation;
    }

    private RefreshOperation? BeginEventRefresh()
    {
        CancellationTokenSource? previous;
        RefreshOperation operation;
        lock (sync)
        {
            if (disposed)
            {
                return null;
            }

            previous = refreshCancellation;
            refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token);
            operation = new(++inputVersion, refreshCancellation);
            currentRefresh = operation;
        }

        Cancel(previous);
        return operation;
    }

    private async Task ProcessRefreshAsync(
        RefreshOperation operation,
        IReadOnlyList<UsbDeviceDescriptor> devices,
        Exception? error,
        bool publish)
    {
        try
        {
            await refreshGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            try
            {
                PsbcUsbKeyDetectionSnapshot? snapshot = null;
                if (error is null)
                {
                    var results = await detector
                        .DetectAsync(devices, operation.Cancellation.Token)
                        .WaitAsync(operation.Cancellation.Token)
                        .ConfigureAwait(false);
                    snapshot = PsbcUsbKeyDetectionSnapshot.Create(devices, results);
                }

                EventHandler<PsbcUsbKeyDetectionChangedEventArgs>[] detectionSubscribers;
                UsbDevicesChangedEventHandler[] deviceSubscribers;
                lock (sync)
                {
                    if (disposed || operation.Version != inputVersion)
                    {
                        operation.Completion.TrySetResult();
                        return;
                    }

                    latestSnapshot = snapshot;
                    latestError = error;
                    processedVersion = operation.Version;
                    detectionSubscribers = publish
                        ? detectionChanged?.GetInvocationList()
                            .Cast<EventHandler<PsbcUsbKeyDetectionChangedEventArgs>>()
                            .ToArray() ?? []
                        : [];
                    deviceSubscribers = publish
                        ? devicesChanged?.GetInvocationList()
                            .Cast<UsbDevicesChangedEventHandler>()
                            .ToArray() ?? []
                        : [];
                }

                RefreshAuthenticationContext();
                if (publish)
                {
                    PublishDetection(detectionSubscribers, snapshot, error);
                    await PublishDevicesAsync(
                            deviceSubscribers,
                            snapshot?.ReadyDevices ?? [],
                            error)
                        .ConfigureAwait(false);
                }

                operation.Completion.TrySetResult();
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            operation.Completion.TrySetResult();
        }
        catch (Exception exception)
        {
            EventHandler<PsbcUsbKeyDetectionChangedEventArgs>[] detectionSubscribers;
            UsbDevicesChangedEventHandler[] deviceSubscribers;
            lock (sync)
            {
                if (disposed || operation.Version != inputVersion)
                {
                    operation.Completion.TrySetResult();
                    return;
                }

                latestSnapshot = null;
                latestError = exception;
                processedVersion = operation.Version;
                detectionSubscribers = publish
                    ? detectionChanged?.GetInvocationList()
                        .Cast<EventHandler<PsbcUsbKeyDetectionChangedEventArgs>>()
                        .ToArray() ?? []
                    : [];
                deviceSubscribers = publish
                    ? devicesChanged?.GetInvocationList()
                        .Cast<UsbDevicesChangedEventHandler>()
                        .ToArray() ?? []
                    : [];
            }

            RefreshAuthenticationContext();
            if (publish)
            {
                PublishDetection(detectionSubscribers, snapshot: null, exception);
                await PublishDevicesAsync(deviceSubscribers, [], exception)
                    .ConfigureAwait(false);
            }

            operation.Completion.TrySetResult();
        }
        finally
        {
            operation.Cancellation.Dispose();
        }
    }

    private async Task<PsbcUsbKeyDetectionSnapshot> WaitForCurrentSnapshotAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? pending;
            PsbcUsbKeyDetectionSnapshot? snapshot;
            Exception? error;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (processedVersion == inputVersion)
                {
                    snapshot = latestSnapshot;
                    error = latestError;
                    pending = null;
                }
                else
                {
                    snapshot = null;
                    error = null;
                    pending = currentRefresh?.Completion.Task;
                }
            }

            if (error is not null)
            {
                throw new InvalidOperationException(
                    "Postal Savings Bank USB Key detection failed.",
                    error);
            }

            if (snapshot is not null)
            {
                return snapshot;
            }

            if (pending is null)
            {
                throw new InvalidOperationException(
                    "Postal Savings Bank USB Key detection did not produce a snapshot.");
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void PublishDetection(
        IEnumerable<EventHandler<PsbcUsbKeyDetectionChangedEventArgs>> subscribers,
        PsbcUsbKeyDetectionSnapshot? snapshot,
        Exception? error)
    {
        var args = new PsbcUsbKeyDetectionChangedEventArgs(snapshot, error);
        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.Invoke(null, args);
            }
            catch
            {
                // Presence presentation cannot interrupt device delivery.
            }
        }
    }

    private void RefreshAuthenticationContext()
    {
        EventHandler[] subscribers;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            RefreshAuthenticationContextLocked(out var changed);
            subscribers = changed
                ? authenticationContextChanged?.GetInvocationList()
                    .Cast<EventHandler>()
                    .ToArray() ?? []
                : [];
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch
            {
                // Authentication invalidation must continue for other consumers.
            }
        }
    }

    private void RefreshAuthenticationContextLocked(out bool changed)
    {
        var candidate = CreateAuthenticationContext(
            latestSnapshot,
            latestError,
            authenticationContext.ContextRevision);
        if (HasSameAuthenticationSemantics(authenticationContext, candidate))
        {
            changed = false;
            return;
        }

        authenticationContext = candidate with
        {
            ContextRevision = ++authenticationContextRevision
        };
        changed = true;
    }

    private PsbcAuthenticationRuntimeSnapshot CreateAuthenticationContext(
        PsbcUsbKeyDetectionSnapshot? snapshot,
        Exception? error,
        long contextRevision)
    {
        var readyKey = error is null
            ? SelectCompleteReadyKey(snapshot?.Results ?? [])
            : null;
        return new(
            runtimeGuard?.Owner.SessionId ?? 0,
            runtimeGuard?.Owner.Revision ?? 0,
            runtimeGuard?.CurrentSessionId ?? 0,
            runtimeGuard?.CurrentRevision ?? 0,
            runtimeGuard?.CurrentBank == BankFlowKind.PostalSavingsBank,
            runtimeGuard?.Owner.Session.IsCurrent == true,
            runtimeGuard?.OwnsCurrentRuntime == true,
            error is null
                ? snapshot?.Status ?? UsbKeyDetectionStatus.Absent
                : UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            readyKey,
            contextRevision);
    }

    private static UsbKeyDetectionResult? SelectCompleteReadyKey(
        IReadOnlyList<UsbKeyDetectionResult> results)
    {
        var ready = results
            .Where(result =>
                result.Status == UsbKeyDetectionStatus.Ready &&
                result.VendorFamily is not null &&
                !string.IsNullOrWhiteSpace(result.PhysicalInstanceId) &&
                !string.IsNullOrWhiteSpace(result.TokenIdentity))
            .Take(2)
            .ToArray();
        return ready.Length == 1 ? ready[0] : null;
    }

    private static bool HasSameAuthenticationSemantics(
        PsbcAuthenticationRuntimeSnapshot left,
        PsbcAuthenticationRuntimeSnapshot right) =>
        left.OwnerSessionId == right.OwnerSessionId &&
        left.OwnerRevision == right.OwnerRevision &&
        left.CurrentSessionId == right.CurrentSessionId &&
        left.CurrentRevision == right.CurrentRevision &&
        left.IsPostalSavingsBank == right.IsPostalSavingsBank &&
        left.IsSessionCurrent == right.IsSessionCurrent &&
        left.OwnsCurrentRuntime == right.OwnsCurrentRuntime &&
        left.DetectionStatus == right.DetectionStatus &&
        left.ReadyKey?.Status == right.ReadyKey?.Status &&
        left.ReadyKey?.VendorFamily == right.ReadyKey?.VendorFamily &&
        string.Equals(
            left.ReadyKey?.PhysicalInstanceId,
            right.ReadyKey?.PhysicalInstanceId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.ReadyKey?.TokenIdentity,
            right.ReadyKey?.TokenIdentity,
            StringComparison.Ordinal);

    private async Task PublishDevicesAsync(
        IReadOnlyList<UsbDevicesChangedEventHandler> subscribers,
        IReadOnlyList<UsbDeviceDescriptor> devices,
        Exception? error)
    {
        var args = new UsbDevicesChangedEventArgs(devices, error);
        foreach (var subscriber in subscribers)
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
            }

            try
            {
                await subscriber(this, args).ConfigureAwait(false);
            }
            catch
            {
                // A consumer failure cannot block later consumers or cleanup.
            }
        }
    }

    private void Track(Task task)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            activeRefreshes.Add(task);
        }

        _ = ObserveRefreshAsync(task);
    }

    private async Task ObserveRefreshAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Current failures are published through monitor state and events.
        }
        finally
        {
            lock (sync) activeRefreshes.Remove(task);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] refreshes;
        CancellationTokenSource? activeCancellation;
        lock (sync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }

            disposed = true;
            if (subscribed)
            {
                inner.DevicesChanged -= OnDevicesChangedAsync;
            }

            devicesChanged = null;
            detectionChanged = null;
            authenticationContextChanged = null;
            refreshes = activeRefreshes.ToArray();
            activeCancellation = refreshCancellation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }

        _ = DisposeCoreAsync(completion, refreshes, activeCancellation);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(
        TaskCompletionSource completion,
        Task[] refreshes,
        CancellationTokenSource? activeCancellation)
    {
        try
        {
            runtimeCancellationRegistration.Dispose();
            lifetime.Cancel();
            Cancel(activeCancellation);
            await inner.DisposeAsync().ConfigureAwait(false);
            foreach (var refresh in refreshes)
            {
                try
                {
                    await refresh.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            refreshGate.Dispose();
            lifetime.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record RefreshOperation(
        long Version,
        CancellationTokenSource Cancellation)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
