using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class UsbKeyPresenceObserverTests
{
    private static UsbDeviceDescriptor Key => new("key", [], null, [], [], [], "USBSTOR");

    [Fact]
    public async Task StartupAndPlugEventsPublishMatchingPresenceOnly()
    {
        var source = new Source(() => Task.FromResult<IReadOnlyList<UsbDeviceDescriptor>>([]));
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        await observer.StartAsync();
        await source.Emit([Key]);
        await source.Emit([Key with { Service = "other" }]);
        Assert.Equal(new bool?[] { false, true, false }, values);
    }

    [Fact]
    public async Task NewerEventWinsOverLateInitialSnapshot()
    {
        var read = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(() => read.Task);
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        var starting = observer.StartAsync();
        await source.ReadEntered.Task;
        await source.Emit([Key]);
        read.SetResult([]);
        await starting;
        Assert.Equal(new bool?[] { true }, values);
    }

    [Fact]
    public async Task SnapshotRequestedAfterStartupEventMayPublishNewerPresence()
    {
        var source = new Source(() => Task.FromResult<IReadOnlyList<UsbDeviceDescriptor>>([Key]));
        source.Starting = () => source.Emit([]);
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);

        await observer.StartAsync();

        Assert.Equal(new bool?[] { false, true }, values);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupFailurePublishesUnknownAndEventsCanRecover(bool startFailure)
    {
        var source = new Source(() => Task.FromException<IReadOnlyList<UsbDeviceDescriptor>>(new IOException("read failed")), startFailure);
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        await observer.StartAsync();
        await source.Emit([Key]);
        await source.Emit([], new IOException("monitor failed"));
        await source.Emit([]);
        Assert.Equal(new bool?[] { null, true, null, false }, values);
    }

    [Fact]
    public async Task LateInitialFailureCannotReplaceRecoveredPresence()
    {
        var read = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(() => read.Task);
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        var starting = observer.StartAsync();
        await source.ReadEntered.Task;
        await source.Emit([Key]);
        read.SetException(new IOException("old failed read"));
        await starting;
        Assert.Equal(new bool?[] { true }, values);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForUncooperativeReadOrPublishItsLateResult()
    {
        var read = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(() => read.Task);
        var values = new List<bool?>();
        var observer = Create(source, values.Add);
        var starting = observer.StartAsync();
        await source.ReadEntered.Task;
        await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        read.SetResult([Key]);
        await starting.WaitAsync(TimeSpan.FromSeconds(1));
        await source.Emit([Key]);
        await observer.DisposeAsync();
        Assert.Empty(values);
        Assert.Equal(0, source.Subscribers);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task ConcurrentStartSharesOneMonitorSubscriptionAndInitialRead()
    {
        var read = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(() => read.Task);
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        var first = observer.StartAsync();
        var second = observer.StartAsync();
        Assert.Same(first, second);
        read.SetResult([Key]);
        await Task.WhenAll(first, second);
        Assert.Equal(1, source.Starts);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, source.Subscribers);
        Assert.Equal(new bool?[] { true }, values);
    }

    [Fact]
    public async Task CancellationStopsHintPublicationWithoutInventingADeviceError()
    {
        var source = new Source(() => Task.FromResult<IReadOnlyList<UsbDeviceDescriptor>>([Key]));
        var values = new List<bool?>();
        await using var observer = Create(source, values.Add);
        using var cancellation = new CancellationTokenSource();
        await observer.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await source.Emit([]);
        Assert.Equal(new bool?[] { true }, values);
    }

    private static UsbKeyPresenceObserver Create(Source source, Action<bool?> callback) =>
        new(source, device => device.Service == "USBSTOR", callback);

    private sealed class Source(Func<Task<IReadOnlyList<UsbDeviceDescriptor>>> read, bool startFailure = false) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;
        public int Subscribers => DevicesChanged?.GetInvocationList().Length ?? 0;
        public int Starts { get; private set; }
        public int Reads { get; private set; }
        public int Disposals { get; private set; }
        public Func<Task>? Starting { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return startFailure ? Task.FromException(new IOException("start failed")) : Starting?.Invoke() ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
        {
            Reads++; ReadEntered.TrySetResult(); return read();
        }
        public async Task Emit(IReadOnlyList<UsbDeviceDescriptor> devices, Exception? error = null)
        {
            foreach (var handler in DevicesChanged?.GetInvocationList().Cast<UsbDevicesChangedEventHandler>() ?? [])
                await handler(this, new(devices, error));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
