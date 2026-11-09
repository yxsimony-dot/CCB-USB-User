using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BankUsbKeyFlowTests
{
    [Fact]
    public void FactoryCreatesOnlyTheSelectedBranch()
    {
        var ccb = new RecordingBranchFactory(
            BankFlowKind.ChinaConstructionBank);
        var psbc = new RecordingBranchFactory(
            BankFlowKind.PostalSavingsBank);
        var factory = new BankUsbKeyFlowFactory([ccb, psbc]);

        var selected = factory.Create(
            BankFlowKind.PostalSavingsBank,
            PsbcProfile());

        Assert.Same(psbc.Flow, selected);
        Assert.Equal(0, ccb.CreateCount);
        Assert.Equal(1, psbc.CreateCount);
    }

    [Fact]
    public async Task ConstructionBankFlowDoesNotConstructOrSubscribePsbcDetection()
    {
        var monitors = new List<RecordingMonitor>();
        var policies = new List<IUsbMetadataRetryPolicy>();
        var detectorCreations = 0;
        var factory = BankUsbKeyFlowFactory.CreateDefault(
            policy =>
            {
                policies.Add(policy);
                var monitor = new RecordingMonitor([OverlappingKey()]);
                monitors.Add(monitor);
                return monitor;
            },
            () =>
            {
                detectorCreations++;
                return new RecordingDetector();
            });
        var flow = factory.Create(
            BankFlowKind.ChinaConstructionBank,
            CcbProfile());
        var statuses = new List<UsbKeyDetectionStatus?>();

        await using var presence = flow.CreatePresenceObserver(statuses.Add);
        await using var session = flow.CreateSessionMonitor(() => { });
        await presence.StartAsync();
        await session.StartAsync();
        var devices = await session.GetConnectedDevicesAsync();

        Assert.Equal(0, detectorCreations);
        Assert.All(policies, policy =>
            Assert.Same(CcbUsbMetadataRetryPolicy.Instance, policy));
        Assert.All(monitors, monitor => Assert.Equal(1, monitor.Subscribers));
        Assert.Equal(UsbKeyDetectionStatus.Ready, Assert.Single(statuses));
        Assert.True(flow.CoordinatorMatcher(Assert.Single(devices)));
    }

    [Fact]
    public async Task PostalSavingsFlowUsesOnlyPsbcPolicyAndDetectionForOverlappingKey()
    {
        var monitors = new List<RecordingMonitor>();
        var policies = new List<IUsbMetadataRetryPolicy>();
        var detectorCreations = 0;
        var factory = BankUsbKeyFlowFactory.CreateDefault(
            policy =>
            {
                policies.Add(policy);
                var monitor = new RecordingMonitor([OverlappingKey()]);
                monitors.Add(monitor);
                return monitor;
            },
            () =>
            {
                detectorCreations++;
                return new RecordingDetector();
            });
        var flow = factory.Create(
            BankFlowKind.PostalSavingsBank,
            PsbcProfile());
        var statuses = new List<UsbKeyDetectionStatus?>();

        await using var presence = flow.CreatePresenceObserver(statuses.Add);
        await using var session = flow.CreateSessionMonitor(() => { });
        await presence.StartAsync();
        await session.StartAsync();
        var devices = await session.GetConnectedDevicesAsync();

        Assert.Equal(2, detectorCreations);
        Assert.All(policies, policy =>
            Assert.Same(PsbcUsbMetadataRetryPolicy.Instance, policy));
        Assert.All(monitors, monitor => Assert.Equal(1, monitor.Subscribers));
        Assert.Equal(UsbKeyDetectionStatus.Ready, Assert.Single(statuses));
        Assert.True(flow.CoordinatorMatcher(Assert.Single(devices)));
    }

    [Fact]
    public void OverlappingKeyCanMatchEachBankIndependently()
    {
        var key = OverlappingKey();
        var factory = BankUsbKeyFlowFactory.CreateDefault(
            _ => new RecordingMonitor([key]),
            () => new RecordingDetector());

        var ccb = factory.Create(
            BankFlowKind.ChinaConstructionBank,
            CcbProfile());
        var psbc = factory.Create(
            BankFlowKind.PostalSavingsBank,
            PsbcProfile());

        Assert.True(ccb.CoordinatorMatcher(key));
        Assert.True(psbc.CoordinatorMatcher(key));
    }

    [Fact]
    public async Task PostalSavingsSessionMonitorReceivesRuntimeGuard()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var guard = CurrentGuard(
            session,
            revision: 9,
            BankFlowKind.PostalSavingsBank);
        var factory = BankUsbKeyFlowFactory.CreateDefault(
            _ => new RecordingMonitor([OverlappingKey()]),
            () => new RecordingDetector());
        var flow = factory.Create(
            BankFlowKind.PostalSavingsBank,
            PsbcProfile());

        await using var monitor = flow.CreateSessionMonitor(
            () => { },
            guard);
        await monitor.StartAsync();
        await monitor.GetConnectedDevicesAsync();
        var context = Assert.IsAssignableFrom<
            IPsbcAuthenticationRuntimeContext>(monitor).Current;

        Assert.True(context.IsValid);
        Assert.Equal(session.Id, context.OwnerSessionId);
        Assert.Equal(9, context.OwnerRevision);
        Assert.Equal(
            "SYNTHETIC_PSBC_TOKEN",
            context.ReadyKey?.TokenIdentity);
    }

    [Fact]
    public async Task ConstructionBankSessionMonitorIgnoresPsbcRuntimeGuard()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var guard = CurrentGuard(
            session,
            revision: 4,
            BankFlowKind.ChinaConstructionBank);
        var factory = BankUsbKeyFlowFactory.CreateDefault(
            _ => new RecordingMonitor([OverlappingKey()]),
            () => new RecordingDetector());
        var flow = factory.Create(
            BankFlowKind.ChinaConstructionBank,
            CcbProfile());

        await using var monitor = flow.CreateSessionMonitor(
            () => { },
            guard);

        Assert.False(monitor is IPsbcAuthenticationRuntimeContext);
    }

    private static BankRuntimeCallbackGuard CurrentGuard(
        BusinessSessionScope session,
        long revision,
        BankFlowKind bank)
    {
        BankRuntimeOwner? owner = new(session, revision, bank);
        return new(owner, () => owner, () => revision, () => bank);
    }

    private static LauncherProfile CcbProfile() => new(
        "https://ccb.example.test/",
        UsbDetectionStrategy: "ccbUsbStor");

    private static LauncherProfile PsbcProfile() => new(
        "https://psbc.example.test/",
        UsbDetectionStrategy: "psbcUsbKey");

    private static UsbDeviceDescriptor OverlappingKey() => new(
        "USB\\VID_1780&PID_0401\\OVERLAP",
        ["USB\\VID_1780&PID_0401"],
        "Tendyron Token",
        ["TENDYRON U-DISK USB Device"],
        ["CCB", "PSBC"],
        ["CDFS"],
        "USBSTOR");

    private sealed class RecordingBranchFactory(BankFlowKind kind)
        : IBankUsbKeyFlowBranchFactory
    {
        public BankFlowKind Kind { get; } = kind;
        public int CreateCount { get; private set; }
        public IBankUsbKeyFlow Flow { get; } = new RecordingFlow();

        public IBankUsbKeyFlow Create(LauncherProfile profile)
        {
            CreateCount++;
            return Flow;
        }
    }

    private sealed class RecordingFlow : IBankUsbKeyFlow
    {
        public Func<UsbDeviceDescriptor, bool> CoordinatorMatcher =>
            _ => false;

        public IUsbKeyPresenceObserver CreatePresenceObserver(
            Action<UsbKeyDetectionStatus?> publish) =>
            throw new NotSupportedException();

        public IUsbDeviceMonitor CreateSessionMonitor(
            Action onDeviceRemoved,
            BankRuntimeCallbackGuard? runtimeGuard = null) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDetector : IPsbcUsbKeyDetector
    {
        public Task<IReadOnlyList<UsbKeyDetectionResult>> DetectAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            CancellationToken cancellationToken = default)
        {
            var device = Assert.Single(devices);
            return Task.FromResult<IReadOnlyList<UsbKeyDetectionResult>>(
            [
                UsbKeyDetectionResult.Ready(
                    UsbKeyVendorFamily.Tendyron,
                    device.InstanceId,
                    "SYNTHETIC_PSBC_TOKEN")
            ]);
        }
    }

    private sealed class RecordingMonitor(
        IReadOnlyList<UsbDeviceDescriptor> devices) : IUsbDeviceMonitor
    {
        private UsbDevicesChangedEventHandler? handlers;

        public event UsbDevicesChangedEventHandler? DevicesChanged
        {
            add => handlers += value;
            remove => handlers -= value;
        }

        public int Subscribers =>
            handlers?.GetInvocationList().Length ?? 0;

        public Task StartAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(devices);

        public ValueTask DisposeAsync()
        {
            handlers = null;
            return ValueTask.CompletedTask;
        }
    }
}
