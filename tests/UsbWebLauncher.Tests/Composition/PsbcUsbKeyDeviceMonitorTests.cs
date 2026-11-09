using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class PsbcUsbKeyDeviceMonitorTests
{
    [Theory]
    [InlineData(UsbKeyDetectionStatus.Absent, 0)]
    [InlineData(UsbKeyDetectionStatus.PresentButEnvironmentUnavailable, 0)]
    [InlineData(UsbKeyDetectionStatus.Ready, 1)]
    public async Task InitialSnapshotMapsDetectionStateAndExposesOnlyReadyDevices(
        UsbKeyDetectionStatus status,
        int expectedReadyDevices)
    {
        var source = new SourceMonitor([Key()]);
        var detector = new MutableDetector(status);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);

        await monitor.StartAsync();
        var snapshot = await monitor.ReadDetectionAsync();

        Assert.Equal(status, snapshot.Status);
        Assert.Equal(expectedReadyDevices, snapshot.ReadyDevices.Count);
    }

    [Fact]
    public async Task DeviceEventsPublishEnvironmentStateButOnlyReadyDevices()
    {
        var source = new SourceMonitor([]);
        var detector = new MutableDetector(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var statuses = new List<UsbKeyDetectionStatus?>();
        var deliveries = new List<IReadOnlyList<UsbDeviceDescriptor>>();
        monitor.DetectionChanged += (_, args) =>
            statuses.Add(args.Snapshot?.Status);
        monitor.DevicesChanged += (_, args) =>
        {
            deliveries.Add(args.Devices);
            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        await source.EmitAsync([Key()]);
        detector.Status = UsbKeyDetectionStatus.Ready;
        await source.EmitAsync([Key()]);

        Assert.Equal(
            [
                UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
                UsbKeyDetectionStatus.Ready
            ],
            statuses);
        Assert.Empty(deliveries[0]);
        Assert.Equal(Key().InstanceId, Assert.Single(deliveries[1]).InstanceId);
    }

    [Fact]
    public async Task EnvironmentFailureDoesNotEndSessionUntilPhysicalKeyLeaves()
    {
        var source = new SourceMonitor([Key()]);
        var detector = new MutableDetector(UsbKeyDetectionStatus.Ready);
        var removals = 0;
        await using var session = new SessionUsbDeviceMonitor(
            source,
            PsbcUsbKeyMatcher.IsCandidate,
            () => removals++);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(session, detector);
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());

        detector.Status =
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable;
        await source.EmitAsync([Key()]);

        Assert.Equal(0, removals);

        await source.EmitAsync([]);

        Assert.Equal(1, removals);
    }

    [Fact]
    public async Task NewerDeviceEventSuppressesLateProbeResult()
    {
        var source = new SourceMonitor([]);
        var firstProbe = new TaskCompletionSource<
            IReadOnlyList<UsbKeyDetectionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector((call, devices, _) =>
        {
            if (call == 1)
            {
                firstEntered.TrySetResult();
                return firstProbe.Task;
            }

            return Task.FromResult<IReadOnlyList<UsbKeyDetectionResult>>(
            [
                UsbKeyDetectionResult.Ready(
                    UsbKeyVendorFamily.Tendyron,
                    Assert.Single(devices).InstanceId)
            ]);
        });
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var statuses = new List<UsbKeyDetectionStatus>();
        monitor.DetectionChanged += (_, args) =>
        {
            if (args.Snapshot is { } snapshot)
            {
                statuses.Add(snapshot.Status);
            }
        };
        await monitor.StartAsync();

        var stale = source.EmitAsync([Key()]);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var current = source.EmitAsync([Key()]);
        await current.WaitAsync(TimeSpan.FromSeconds(2));
        firstProbe.TrySetResult(
        [
            UsbKeyDetectionResult.EnvironmentUnavailable(
                UsbKeyVendorFamily.Tendyron,
                UsbKeyDiagnosticCode.ProviderUnavailable,
                Key().InstanceId)
        ]);
        await stale.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([UsbKeyDetectionStatus.Ready], statuses);
    }

    [Fact]
    public async Task DisposalCancelsProbeAndSuppressesLatePublication()
    {
        var source = new SourceMonitor([]);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ScriptedDetector(async (_, _, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }

            return [];
        });
        var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var publications = 0;
        monitor.DetectionChanged += (_, _) => publications++;
        await monitor.StartAsync();

        var refresh = source.EmitAsync([Key()]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, publications);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task PresenceObserverUsesFreshSnapshotWithoutReinsertion()
    {
        var source = new SourceMonitor([Key()]);
        var detector = new MutableDetector(UsbKeyDetectionStatus.Ready);
        var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var statuses = new List<UsbKeyDetectionStatus?>();
        await using var observer = new PsbcUsbKeyPresenceObserver(
            monitor,
            statuses.Add);

        await observer.StartAsync();

        Assert.Equal(UsbKeyDetectionStatus.Ready, Assert.Single(statuses));
        Assert.Equal(0, source.EventCount);
    }

    [Fact]
    public async Task CompleteReadyKeyIsExposedToAuthenticationContext()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var guard = CurrentPsbcGuard(session, revision: 7);
        var source = new SourceMonitor([Key()]);
        var detector = new MutableDetector(UsbKeyDetectionStatus.Ready);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(
            source,
            detector,
            guard);

        await monitor.StartAsync();
        await monitor.ReadDetectionAsync();
        var context = monitor.Current;

        Assert.True(context.IsValid);
        Assert.Equal(session.Id, context.OwnerSessionId);
        Assert.Equal(7, context.OwnerRevision);
        Assert.Equal(Key().InstanceId, context.ReadyKey?.PhysicalInstanceId);
        Assert.Equal("SYNTHETIC_PSBC_TOKEN", context.ReadyKey?.TokenIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleOrIncompleteReadyKeysFailClosed(
        bool multipleReadyKeys)
    {
        var source = new SourceMonitor([Key()]);
        var detector = new ScriptedDetector((_, devices, _) =>
        {
            var instanceId = Assert.Single(devices).InstanceId;
            IReadOnlyList<UsbKeyDetectionResult> results = multipleReadyKeys
                ?
                [
                    UsbKeyDetectionResult.Ready(
                        UsbKeyVendorFamily.Tendyron,
                        instanceId,
                        "SYNTHETIC_TOKEN_A"),
                    UsbKeyDetectionResult.Ready(
                        UsbKeyVendorFamily.Tendyron,
                        instanceId,
                        "SYNTHETIC_TOKEN_B")
                ]
                :
                [
                    UsbKeyDetectionResult.Ready(
                        UsbKeyVendorFamily.Tendyron,
                        instanceId)
                ];
            return Task.FromResult(results);
        });
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);

        await monitor.StartAsync();
        var detection = await monitor.ReadDetectionAsync();

        Assert.Equal(UsbKeyDetectionStatus.Ready, detection.Status);
        Assert.Null(monitor.Current.ReadyKey);
        Assert.False(monitor.Current.IsValid);
    }

    [Fact]
    public async Task DuplicateDeviceNotificationsDoNotChangeAuthenticationContext()
    {
        var source = new SourceMonitor([]);
        var detector = new MutableDetector(UsbKeyDetectionStatus.Ready);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var changes = 0;
        monitor.Changed += (_, _) => changes++;
        await monitor.StartAsync();

        await source.EmitAsync([Key()]);
        var revision = monitor.Current.ContextRevision;
        await source.EmitAsync([Key()]);

        Assert.Equal(1, changes);
        Assert.Equal(revision, monitor.Current.ContextRevision);
    }

    [Fact]
    public async Task KeyIdentityAndDetectionStatusChangesInvalidateContext()
    {
        var source = new SourceMonitor([]);
        var detector = new ScriptedDetector((call, devices, _) =>
        {
            var instanceId = Assert.Single(devices).InstanceId;
            IReadOnlyList<UsbKeyDetectionResult> results = call switch
            {
                1 =>
                [
                    UsbKeyDetectionResult.Ready(
                        UsbKeyVendorFamily.Tendyron,
                        instanceId,
                        "SYNTHETIC_TOKEN_A")
                ],
                2 =>
                [
                    UsbKeyDetectionResult.Ready(
                        UsbKeyVendorFamily.Tendyron,
                        instanceId,
                        "SYNTHETIC_TOKEN_B")
                ],
                _ =>
                [
                    UsbKeyDetectionResult.EnvironmentUnavailable(
                        UsbKeyVendorFamily.Tendyron,
                        UsbKeyDiagnosticCode.ProviderUnavailable,
                        instanceId)
                ]
            };
            return Task.FromResult(results);
        });
        await using var monitor = new PsbcUsbKeyDeviceMonitor(source, detector);
        var revisions = new List<long>();
        monitor.Changed += (_, _) =>
            revisions.Add(monitor.Current.ContextRevision);
        await monitor.StartAsync();

        await source.EmitAsync([Key()]);
        await source.EmitAsync([Key()]);
        await source.EmitAsync([Key()]);

        Assert.Equal(3, revisions.Count);
        Assert.True(revisions.SequenceEqual(revisions.OrderBy(value => value)));
        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            monitor.Current.DetectionStatus);
        Assert.Null(monitor.Current.ReadyKey);
    }

    [Fact]
    public async Task SessionCancellationInvalidatesAuthenticationContext()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var guard = CurrentPsbcGuard(session, revision: 3);
        var source = new SourceMonitor([Key()]);
        var detector = new MutableDetector(UsbKeyDetectionStatus.Ready);
        await using var monitor = new PsbcUsbKeyDeviceMonitor(
            source,
            detector,
            guard);
        await monitor.StartAsync();
        await monitor.ReadDetectionAsync();
        var initialRevision = monitor.Current.ContextRevision;
        var changes = 0;
        monitor.Changed += (_, _) => changes++;

        await session.RevokeAsync();

        Assert.Equal(1, changes);
        Assert.False(monitor.Current.IsSessionCurrent);
        Assert.False(monitor.Current.IsValid);
        Assert.True(monitor.Current.ContextRevision > initialRevision);
    }

    private static BankRuntimeCallbackGuard CurrentPsbcGuard(
        BusinessSessionScope session,
        long revision)
    {
        BankRuntimeOwner? owner = new(
            session,
            revision,
            BankFlowKind.PostalSavingsBank);
        return new(
            owner,
            () => owner,
            () => revision,
            () => BankFlowKind.PostalSavingsBank);
    }

    private static UsbDeviceDescriptor Key() => new(
        "USB\\VID_1780&PID_0401\\PSBC",
        ["USB\\VID_1780&PID_0401"],
        "Tendyron Token",
        ["TENDYRON U-DISK USB Device"],
        ["PSBC"],
        ["CDFS"],
        "USBSTOR");

    private sealed class MutableDetector(UsbKeyDetectionStatus status)
        : IPsbcUsbKeyDetector
    {
        public UsbKeyDetectionStatus Status { get; set; } = status;

        public Task<IReadOnlyList<UsbKeyDetectionResult>> DetectAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            CancellationToken cancellationToken = default)
        {
            if (Status == UsbKeyDetectionStatus.Absent || devices.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<UsbKeyDetectionResult>>([]);
            }

            var instanceId = Assert.Single(devices).InstanceId;
            var result = Status == UsbKeyDetectionStatus.Ready
                ? UsbKeyDetectionResult.Ready(
                    UsbKeyVendorFamily.Tendyron,
                    instanceId,
                    "SYNTHETIC_PSBC_TOKEN")
                : UsbKeyDetectionResult.EnvironmentUnavailable(
                    UsbKeyVendorFamily.Tendyron,
                    UsbKeyDiagnosticCode.ProviderUnavailable,
                    instanceId);
            return Task.FromResult<IReadOnlyList<UsbKeyDetectionResult>>(
                [result]);
        }
    }

    private sealed class ScriptedDetector(
        Func<
            int,
            IReadOnlyList<UsbDeviceDescriptor>,
            CancellationToken,
            Task<IReadOnlyList<UsbKeyDetectionResult>>> detect)
        : IPsbcUsbKeyDetector
    {
        private int calls;

        public Task<IReadOnlyList<UsbKeyDetectionResult>> DetectAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            CancellationToken cancellationToken = default) =>
            detect(Interlocked.Increment(ref calls), devices, cancellationToken);
    }

    private sealed class SourceMonitor(
        IReadOnlyList<UsbDeviceDescriptor> initial) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;

        public int DisposeCount { get; private set; }
        public int EventCount { get; private set; }

        public Task StartAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public async Task EmitAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            Exception? error = null)
        {
            EventCount++;
            var args = new UsbDevicesChangedEventArgs(devices, error);
            foreach (var handler in DevicesChanged?.GetInvocationList()
                         .Cast<UsbDevicesChangedEventHandler>() ?? [])
            {
                await handler(this, args);
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            DevicesChanged = null;
            return ValueTask.CompletedTask;
        }
    }
}
