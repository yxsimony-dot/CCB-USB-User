using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class SessionUsbDeviceMonitorTests
{
    [Fact]
    public async Task UnrelatedDeviceRemovalDoesNotStopThePresentKey()
    {
        var source = new Source([Key("A"), Key("unrelated") with { Service = null }]);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        var deliveries = 0;
        monitor.DevicesChanged += (_, _) => { deliveries++; return Task.CompletedTask; };
        await monitor.StartAsync();
        await monitor.GetConnectedDevicesAsync();
        await source.Emit([Key("a")]);
        Assert.Equal(0, removed);
        Assert.Equal(1, deliveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalOrSingleSnapshotReplacementRevokesBeforeOldFlowReceivesAnotherDevice(bool replacement)
    {
        var source = new Source([Key("A")]);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        var deliveries = new List<string>();
        monitor.DevicesChanged += (_, args) => { deliveries.AddRange(args.Devices.Select(d => d.InstanceId)); return Task.CompletedTask; };
        await monitor.StartAsync();
        await monitor.GetConnectedDevicesAsync();

        await source.Emit(replacement ? [Key("B")] : []);
        await source.Emit([Key("A"), Key("B")]);

        Assert.Equal(1, removed);
        Assert.Empty(deliveries);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.GetConnectedDevicesAsync());
        Assert.Equal(1, source.EnumerationCount);
    }

    [Fact]
    public async Task InitialEmptyStateWaitsForFirstKeyAndMetadataLossDoesNotInventRemoval()
    {
        var source = new Source([]);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        var deliveries = 0;
        monitor.DevicesChanged += (_, _) => { deliveries++; return Task.CompletedTask; };
        await monitor.StartAsync();
        Assert.Empty(await monitor.GetConnectedDevicesAsync());
        await source.Emit([]);
        await source.Emit([Key("A")]);
        await source.Emit([Key("A") with { Service = null }]);
        await source.Emit([], new IOException("enumeration unavailable"));

        Assert.Equal(0, removed);
        Assert.Equal(4, deliveries);
        await source.Emit([]);
        Assert.Equal(1, removed);
        Assert.Equal(4, deliveries);
    }

    [Fact]
    public async Task AdditionalKeyDoesNotReplaceTheOriginalSessionIdentity()
    {
        var source = new Source([Key("A")]);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        await monitor.StartAsync();
        await monitor.GetConnectedDevicesAsync();
        await source.Emit([Key("A"), Key("B")]);
        await source.Emit([Key("B")]);
        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task RemovalRevokesEvenWhenAnEarlierConsumerIsStillAwaitingAuthentication()
    {
        var source = new Source([]);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new BusinessSessionScope(CancellationToken.None);
        await using var monitor = Create(source, () => _ = session.RevokeAsync());
        var delivered = 0;
        monitor.DevicesChanged += (_, _) => { delivered++; return blocked.Task; };
        await monitor.StartAsync();
        var authentication = source.Emit([Key("A")]);
        try
        {
            await source.Emit([Key("B")]).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(session.IsCurrent);
            Assert.True(session.Token.IsCancellationRequested);
            Assert.Equal(1, delivered);
        }
        finally { blocked.TrySetResult(); await authentication; }
    }

    [Fact]
    public async Task LateInitialEnumerationCannotResurrectAnEndedSession()
    {
        var initial = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source([], () => initial.Task);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        await monitor.StartAsync();
        var reading = monitor.GetConnectedDevicesAsync();
        await source.Emit([Key("A")]);
        await source.Emit([]);
        initial.SetResult([Key("A")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task LateInitialSnapshotDoesNotReplaceANewerSuccessfulDeviceEvent()
    {
        var initial = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source([], () => initial.Task);
        var removed = 0;
        await using var monitor = Create(source, () => removed++);
        await monitor.StartAsync();
        var reading = monitor.GetConnectedDevicesAsync();
        await source.Emit([Key("A")]);
        initial.SetResult([]);
        Assert.Equal("A", Assert.Single(await reading).InstanceId);
        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task ExplicitDisposalDoesNotReportRemovalAndDetachesOnce()
    {
        var source = new Source([Key("A")]);
        var removed = 0;
        var monitor = Create(source, () => removed++);
        await monitor.StartAsync();
        await monitor.GetConnectedDevicesAsync();
        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
        await source.Emit([]);
        Assert.Equal(0, removed);
        Assert.Equal(0, source.Subscribers);
        Assert.Equal(1, source.DisposeCount);
    }

    private static IUsbDeviceMonitor Create(Source source, Action removed) =>
        new SessionUsbDeviceMonitor(source, device => device.Service == "USBSTOR", removed);

    private static UsbDeviceDescriptor Key(string id) => new(id, [], null, [], [], [], "USBSTOR");

    private sealed class Source(IReadOnlyList<UsbDeviceDescriptor> initial, Func<Task<IReadOnlyList<UsbDeviceDescriptor>>>? read = null) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;
        public int EnumerationCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int Subscribers => DevicesChanged?.GetInvocationList().Length ?? 0;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
        {
            EnumerationCount++;
            return read?.Invoke() ?? Task.FromResult(initial);
        }
        public async Task Emit(IReadOnlyList<UsbDeviceDescriptor> devices, Exception? error = null)
        {
            foreach (var handler in DevicesChanged?.GetInvocationList().Cast<UsbDevicesChangedEventHandler>() ?? [])
                await handler(this, new(devices, error));
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
