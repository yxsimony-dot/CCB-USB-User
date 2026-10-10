using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Coordination;

public sealed class LauncherCoordinatorTests
{
    [Fact]
    public async Task CcbMetadataLoss_UnsupportedGateFailsClosedWithoutInventingRemoval()
    {
        var order = new List<string>();
        var device = CreateMatchingDevice() with { InstanceId = "USB\\VID_9999&PID_9999\\KEY",
            HardwareIds = ["USB\\VID_9999&PID_9999"], Service = "USBSTOR", CdRomNames = ["CCB"], VolumeLabels = [] };
        var monitor = new FakeUsbDeviceMonitor([device]);
        var browser = new RecordingBrowserHost();
        var gate = new RecordingAuthenticationGate(order, true);
        var profile = new LauncherProfile("https://example.com/", UsbDetectionStrategy: "ccbUsbStor");
        await using var coordinator = new LauncherCoordinator(monitor, browser, profile, gate);
        await coordinator.StartAsync();

        await monitor.PublishAsync([device with { CdRomNames = [] }]);
        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);
        Assert.False(coordinator.CurrentState.IsUsbPresent);
        Assert.DoesNotContain("auth-absent", order);
        await monitor.PublishAsync([device]);
        await coordinator.RetryAsync();
        coordinator.ReportExternalNavigationReady();

        Assert.NotEqual(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Single(browser.NavigatedTargets);
        Assert.DoesNotContain("auth-absent", order);
    }

    [Fact]
    public async Task CcbMode_OpensForNewModelWithoutLegacyAllowlist()
    {
        var device = new UsbDeviceDescriptor("USB\\NEW_MODEL\\SYNTHETIC", [], "Future model", ["CCB virtual CD"], [], [], "USBSTOR");
        var monitor = new FakeUsbDeviceMonitor([device]);
        var browser = new RecordingBrowserHost();
        var profile = new LauncherProfile("https://example.com/", UsbDetectionStrategy: "ccbUsbStor");
        await using var coordinator = new LauncherCoordinator(monitor, browser, profile);

        await coordinator.StartAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task CcbMode_LegacyAllowlistCannotOpenForDeviceWithoutBankEvidence()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice() with {
            InstanceId = "USB\\VID_9999&PID_9999\\KEY", HardwareIds = ["USB\\VID_9999&PID_9999"],
            CdRomNames = [], VolumeLabels = [], Service = "USBSTOR" }]);
        var browser = new RecordingBrowserHost();
        var profile = new LauncherProfile("https://example.com/", new("9999", "9999", "uKeyCI800-K156"),
            AdditionalUsbDevices: [new("9999", "9999", "uKeyCI800-K156")], UsbDetectionStrategy: "ccbUsbStor");
        await using var coordinator = new LauncherCoordinator(monitor, browser, profile);

        await coordinator.StartAsync();

        Assert.Equal(LauncherStage.WaitingForUsb, coordinator.CurrentState.Stage);
        Assert.Empty(browser.NavigatedTargets);
    }

    [Fact]
    public async Task StartAsync_OpensConfiguredPageWhenMatchingDeviceIsAlreadyPresent()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);

        await coordinator.StartAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.IsUsbPresent);
        Assert.Equal(1, browser.InitializeCount);
        Assert.Equal([new Uri("https://b2b.ccb.com/")], browser.NavigatedTargets);
    }

    [Fact]
    public async Task DeviceArrival_OpensConfiguredPageAfterWaiting()
    {
        var monitor = new FakeUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        await monitor.PublishAsync([CreateMatchingDevice()]);

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task StartAsync_OpensConfiguredPageForAdditionalWhitelistedDevice()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateTendyronDevice()]);
        var browser = new RecordingBrowserHost();
        var profile = new LauncherProfile(
            "https://b2b.ccb.com/",
            new("1677", "0054", "uKeyCI800-K156"),
            AdditionalUsbDevices: [new("1780", "0401", "Tendyron Token")]);
        await using var coordinator = new LauncherCoordinator(monitor, browser, profile);

        await coordinator.StartAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task StartAsync_WithAuthentication_PreparesAuthenticationBeforeBrowserNavigation()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);

        await coordinator.StartAsync();

        Assert.Equal(new[] { "auth-present", "initialize", "navigate" }, order);
    }

    [Fact]
    public async Task StartAsync_WhenAuthenticationNeedsPin_DoesNotInitializeOrNavigateBrowser()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: false);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);

        await coordinator.StartAsync();

        Assert.Equal(new[] { "auth-present" }, order);
        Assert.Equal(LauncherStage.UsbDetected, coordinator.CurrentState.Stage);
    }

    [Fact]
    public async Task DeviceRemoval_NotifiesAuthenticationBeforeUpdatingLauncherState()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);
        await coordinator.StartAsync();
        order.Clear();

        await monitor.PublishAsync([]);

        Assert.Equal(new[] { "auth-absent" }, order);
        Assert.False(coordinator.CurrentState.IsUsbPresent);
    }

    [Fact]
    public async Task DeviceReinsert_WithAuthentication_StartsANewNavigationCycle()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);

        await coordinator.StartAsync();
        await monitor.PublishAsync([]);

        Assert.Equal(LauncherStage.WaitingForUsb, coordinator.CurrentState.Stage);

        await monitor.PublishAsync([CreateMatchingDevice()]);

        Assert.Equal(2, order.Count(item => item == "navigate"));
        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.IsUsbPresent);
    }

    [Fact]
    public async Task InvalidatedAuthenticationNavigationFailure_ReturnsToWaitingAndAllowsReinsert()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new FailureOnCancellationBrowserHost();
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);

        var start = coordinator.StartAsync();
        await browser.FirstNavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await monitor.PublishAsync([]).WaitAsync(TimeSpan.FromSeconds(1));
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(LauncherStage.WaitingForUsb, coordinator.CurrentState.Stage);
        Assert.False(coordinator.CurrentState.CanRetry);

        await monitor.PublishAsync([CreateMatchingDevice()]).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, browser.NavigateCount);
        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
    }

    [Fact]
    public async Task ReportExternalNavigationReady_MarksSetupNavigationVisibleAndPreventsSecondNavigation()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: false);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);
        await coordinator.StartAsync();

        coordinator.ReportExternalNavigationReady();
        await monitor.PublishAsync([CreateMatchingDevice()]);

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.IsBrowserVisible);
        Assert.DoesNotContain("navigate", order);
    }

    [Fact]
    public async Task ResumeNavigationAfterAuthenticationSetupAsync_WhenUsbRemainsInserted_ActuallyNavigates()
    {
        var order = new List<string>();
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new OrderedBrowserHost(order);
        var authentication = new RecordingAuthenticationGate(order, allowNavigation: false);
        await using var coordinator = CreateCoordinator(monitor, browser, authentication);
        await coordinator.StartAsync();
        authentication.AllowNavigation = true;

        await coordinator.ResumeNavigationAfterAuthenticationSetupAsync();

        Assert.Equal(
            ["auth-present", "auth-present", "initialize", "navigate"],
            order);
        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.IsBrowserVisible);
    }

    [Fact]
    public async Task DeviceArrival_IgnoresUnrelatedDevice()
    {
        var monitor = new FakeUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();
        var unrelated = CreateMatchingDevice() with
        {
            HardwareIds = ["USB\\VID_9999&PID_9999"]
        };

        await monitor.PublishAsync([unrelated]);

        Assert.Equal(LauncherStage.WaitingForUsb, coordinator.CurrentState.Stage);
        Assert.Empty(browser.NavigatedTargets);
    }

    [Fact]
    public async Task DuplicateArrivalDuringNavigation_DoesNotOpenAgain()
    {
        var monitor = new FakeUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost(blockNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        var firstArrival = monitor.PublishAsync([CreateMatchingDevice()]);
        await browser.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PublishAsync([CreateMatchingDevice()]);

        Assert.Equal(1, browser.InitializeCount);
        Assert.Single(browser.NavigatedTargets);

        browser.AllowNavigationToComplete();
        await firstArrival;
        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
    }

    [Fact]
    public async Task DeviceRemovalAfterReady_KeepsBrowserReadyAndMarksDeviceAbsent()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        await monitor.PublishAsync([]);

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.False(coordinator.CurrentState.IsUsbPresent);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task DeviceRemovalDuringNavigation_ReadyStateKeepsLatestAbsentStatus()
    {
        var monitor = new FakeUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost(blockNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        var arrival = monitor.PublishAsync([CreateMatchingDevice()]);
        await browser.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PublishAsync([]);
        browser.AllowNavigationToComplete();
        await arrival;

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.False(coordinator.CurrentState.IsUsbPresent);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task RetryAfterEnumerationNotificationFailure_ReenumeratesWithoutNavigatingAgain()
    {
        var monitor = new MutableUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        await monitor.PublishErrorAsync(new InvalidOperationException("重新枚举失败"));
        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);

        await coordinator.RetryAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(2, monitor.EnumerationCount);
        Assert.Equal(1, browser.InitializeCount);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task DisposeAsync_DuringNavigationCancelsPendingDeviceCallback()
    {
        var monitor = new FakeUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost(blockNavigation: true);
        var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        var arrival = monitor.PublishAsync([CreateMatchingDevice()]);
        await browser.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await arrival.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MonitoringFaultDuringNavigation_RemainsRetryableAfterNavigationCompletes()
    {
        var monitor = new MutableUsbDeviceMonitor([]);
        var browser = new RecordingBrowserHost(blockNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser);
        await coordinator.StartAsync();

        monitor.CurrentDevices = [CreateMatchingDevice()];
        var arrival = monitor.PublishAsync();
        await browser.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.PublishErrorAsync(new InvalidOperationException("监听中止"));
        browser.AllowNavigationToComplete();
        await arrival;

        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.CanRetry);

        await monitor.PublishAsync();
        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);

        await coordinator.RetryAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(2, monitor.StartCount);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task RetryAsync_RecoversFromDeviceEnumerationFailure()
    {
        var monitor = new FaultOnceUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new RecordingBrowserHost();
        await using var coordinator = CreateCoordinator(monitor, browser);

        await coordinator.StartAsync();

        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);
        Assert.True(coordinator.CurrentState.CanRetry);

        await coordinator.RetryAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Single(browser.NavigatedTargets);
    }

    [Fact]
    public async Task RetryAsync_RecoversFromBrowserInitializationFailure()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new FaultOnceBrowserHost(failInitialization: true);
        await using var coordinator = CreateCoordinator(monitor, browser);

        await coordinator.StartAsync();
        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);

        await coordinator.RetryAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(2, browser.InitializeCount);
        Assert.Equal(1, browser.NavigateCount);
    }

    [Fact]
    public async Task RetryAsync_RecoversFromNavigationFailure()
    {
        var monitor = new FakeUsbDeviceMonitor([CreateMatchingDevice()]);
        var browser = new FaultOnceBrowserHost(failNavigation: true);
        await using var coordinator = CreateCoordinator(monitor, browser);

        await coordinator.StartAsync();
        Assert.Equal(LauncherStage.Error, coordinator.CurrentState.Stage);

        await coordinator.RetryAsync();

        Assert.Equal(LauncherStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(2, browser.NavigateCount);
        Assert.Equal(1, browser.InitializeCount);
    }

    private static LauncherCoordinator CreateCoordinator(
        IUsbDeviceMonitor monitor,
        IBrowserHost browser,
        ILauncherAuthenticationGate? authentication = null) => new(
            monitor,
            browser,
            new LauncherProfile(
                "https://b2b.ccb.com/",
                new UsbMatchOptions("1677", "0054", "uKeyCI800-K156")),
            authentication);

    private static UsbDeviceDescriptor CreateMatchingDevice() => new(
        "USB\\VID_1677&PID_0054\\6&PORT&0&1",
        ["USB\\VID_1677&PID_0054&REV_0103"],
        "uKeyCI800-K156",
        [],
        [],
        []);

    private static UsbDeviceDescriptor CreateTendyronDevice() => new(
        "USB\\VID_1780&PID_0401\\0123456789ABCDEE",
        ["USB\\VID_1780&PID_0401&REV_0001"],
        "Tendyron Token",
        ["TENDYRON U-DISK USB Device"],
        ["CCB"],
        ["CDFS"]);

    private sealed class FakeUsbDeviceMonitor(
        IReadOnlyList<UsbDeviceDescriptor> initialDevices) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(initialDevices);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task PublishAsync(IReadOnlyList<UsbDeviceDescriptor> devices)
        {
            if (DevicesChanged is null)
            {
                return;
            }

            var args = new UsbDevicesChangedEventArgs(devices);
            foreach (UsbDevicesChangedEventHandler handler in DevicesChanged.GetInvocationList())
            {
                await handler(this, args);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultOnceUsbDeviceMonitor(
        IReadOnlyList<UsbDeviceDescriptor> recoveredDevices) : IUsbDeviceMonitor
    {
        private bool _hasFailed;

        public event UsbDevicesChangedEventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            if (!_hasFailed)
            {
                _hasFailed = true;
                return Task.FromException<IReadOnlyList<UsbDeviceDescriptor>>(
                    new InvalidOperationException("枚举失败"));
            }

            return Task.FromResult(recoveredDevices);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableUsbDeviceMonitor(
        IReadOnlyList<UsbDeviceDescriptor> currentDevices) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;

        public int EnumerationCount { get; private set; }

        public int StartCount { get; private set; }

        public IReadOnlyList<UsbDeviceDescriptor> CurrentDevices { get; set; } = currentDevices;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            EnumerationCount++;
            return Task.FromResult(CurrentDevices);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public async Task PublishAsync()
        {
            if (DevicesChanged is null)
            {
                return;
            }

            var args = new UsbDevicesChangedEventArgs(CurrentDevices);
            foreach (UsbDevicesChangedEventHandler handler in DevicesChanged.GetInvocationList())
            {
                await handler(this, args);
            }
        }

        public async Task PublishErrorAsync(Exception exception)
        {
            if (DevicesChanged is null)
            {
                return;
            }

            var args = new UsbDevicesChangedEventArgs([], exception);
            foreach (UsbDevicesChangedEventHandler handler in DevicesChanged.GetInvocationList())
            {
                await handler(this, args);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingBrowserHost(bool blockNavigation = false) : IBrowserHost
    {
        private readonly TaskCompletionSource _continueNavigation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InitializeCount { get; private set; }

        public List<Uri> NavigatedTargets { get; } = [];

        public TaskCompletionSource NavigationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public async Task NavigateAsync(
            Uri target,
            CancellationToken cancellationToken = default)
        {
            NavigatedTargets.Add(target);
            NavigationStarted.TrySetResult();

            if (blockNavigation)
            {
                await _continueNavigation.Task.WaitAsync(cancellationToken);
            }
        }

        public void AllowNavigationToComplete() => _continueNavigation.TrySetResult();
    }

    private sealed class FaultOnceBrowserHost(
        bool failInitialization = false,
        bool failNavigation = false) : IBrowserHost
    {
        private bool _initializationFailed;
        private bool _navigationFailed;

        public int InitializeCount { get; private set; }

        public int NavigateCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            if (failInitialization && !_initializationFailed)
            {
                _initializationFailed = true;
                return Task.FromException(new InvalidOperationException("初始化失败"));
            }

            return Task.CompletedTask;
        }

        public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default)
        {
            NavigateCount++;
            if (failNavigation && !_navigationFailed)
            {
                _navigationFailed = true;
                return Task.FromException(new InvalidOperationException("导航失败"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class OrderedBrowserHost(List<string> order) : IBrowserHost
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            order.Add("initialize");
            return Task.CompletedTask;
        }

        public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default)
        {
            order.Add("navigate");
            return Task.CompletedTask;
        }
    }

    private sealed class FailureOnCancellationBrowserHost : IBrowserHost
    {
        public int NavigateCount { get; private set; }

        public TaskCompletionSource FirstNavigationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task NavigateAsync(
            Uri target,
            CancellationToken cancellationToken = default)
        {
            NavigateCount++;
            if (NavigateCount != 1)
            {
                return;
            }

            var terminal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
                terminal.TrySetException(new InvalidOperationException(
                    "The invalidated native navigation failed while stopping.")));
            FirstNavigationStarted.TrySetResult();
            await terminal.Task;
        }
    }

    private sealed class RecordingAuthenticationGate : ILauncherAuthenticationGate
    {
        private readonly List<string> _order;

        public RecordingAuthenticationGate(List<string> order, bool allowNavigation)
        {
            _order = order;
            AllowNavigation = allowNavigation;
        }

        public bool AllowNavigation { get; set; }

        public Task<bool> HandleDevicePresenceAsync(
            bool isTargetDevicePresent,
            CancellationToken cancellationToken = default)
        {
            _order.Add(isTargetDevicePresent ? "auth-present" : "auth-absent");
            return Task.FromResult(isTargetDevicePresent && AllowNavigation);
        }
    }
}
