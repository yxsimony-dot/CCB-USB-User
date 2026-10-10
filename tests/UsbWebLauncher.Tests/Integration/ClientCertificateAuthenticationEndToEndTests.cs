using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Integration;

public sealed class ClientCertificateAuthenticationEndToEndTests
{
    [Fact]
    public async Task CcbMetadataExhaustionAndLateRecovery_PreserveSpentAttemptUntilPhysicalReinsertion()
    {
        var firstSession = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Success(MatchingRequest()), NavigationPlan.Success(MatchingRequest())],
            [ClientCertificateTokenUnlockResult.Succeeded(firstSession),
             ClientCertificateTokenUnlockResult.Succeeded(new RecordingSession())],
            ccbMode: true);
        await harness.Launcher.StartAsync();
        var cycle = harness.Authentication.CurrentState.InsertionCycleId;

        await harness.Monitor.PublishAsync([CcbDevice() with { CdRomNames = [] }]);
        Assert.False(harness.Launcher.CurrentState.IsUsbPresent);
        Assert.NotEqual(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
        await harness.Monitor.PublishAsync([CcbDevice()]);
        await harness.ManualGate!.AllowBusinessAsync();
        await harness.Launcher.ResumeNavigationAfterAuthenticationSetupAsync();

        Assert.Equal(cycle, harness.Authentication.CurrentState.InsertionCycleId);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Equal(1, harness.Control.NavigateCount);
        Assert.Equal(AuthenticationStage.Failed, harness.Authentication.CurrentState.Stage);
        Assert.Contains("reinsert", harness.Authentication.CurrentState.Message!, StringComparison.OrdinalIgnoreCase);

        await harness.Monitor.PublishAsync([]);
        await harness.ManualGate.AllowBusinessAsync();
        await harness.Monitor.PublishAsync([CcbDevice()]);

        Assert.NotEqual(cycle, harness.Authentication.CurrentState.InsertionCycleId);
        Assert.Equal(2, harness.PinStore.ReadCount);
        Assert.Equal(2, harness.Unlocker.AttemptCount);
        Assert.Equal(2, harness.Control.NavigateCount);
    }

    [Fact]
    public async Task CcbMetadataExhaustionDuringNavigation_CancelsAndReleasesWithoutResettingCycle()
    {
        var session = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Pending(MatchingRequest())],
            [ClientCertificateTokenUnlockResult.Succeeded(session)], ccbMode: true);
        var start = harness.Launcher.StartAsync();
        await harness.Control.FirstRequestProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cycle = harness.Authentication.CurrentState.InsertionCycleId;
        var attempt = harness.Authentication.CurrentState.Attempt!;

        await harness.Monitor.PublishAsync([CcbDevice() with { CdRomNames = [] }]).WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Authentication.ReportTlsCompletedAsync(attempt);

        Assert.False(harness.Launcher.CurrentState.IsUsbPresent);
        Assert.NotEqual(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
        Assert.Equal(AuthenticationStage.Failed, harness.Authentication.CurrentState.Stage);
        Assert.Equal(cycle, harness.Authentication.CurrentState.InsertionCycleId);
        Assert.Null(harness.Authentication.CurrentState.Attempt);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
    }

    private static UsbDeviceDescriptor CcbDevice() => MatchingDevice() with { Service = "USBSTOR", CdRomNames = ["CCB virtual CD"] };

    [Fact]
    public async Task MatchingUsb_ReadsAndUnlocksOnce_SelectsExactCertificate_AndCompletesTls()
    {
        var session = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Success(MatchingRequest())],
            [ClientCertificateTokenUnlockResult.Succeeded(session)]);
        session.Order = harness.Order;

        await harness.Launcher.StartAsync();
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Single(harness.Control.Selections);
        Assert.Same(
            harness.Control.Plans[0].Request.AvailableCertificates[0],
            harness.Control.Selections[0].SelectedCertificate);
        Assert.Equal(AuthenticationStage.Ready, harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
        AssertOrdered(
            harness.Order,
            "monitor-start",
            "monitor-read",
            "stamp-read",
            "pin-read",
            "unlock",
            "browser-initialize",
            "certificate-handler-register",
            "navigate",
            "certificate-request",
            "certificate-selected",
            "session-dispose");
    }

    [Fact]
    public async Task UnauthorizedBrowserRequest_FailsClosedWithoutASecondPinSubmission()
    {
        var session = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Success(MatchingRequest(new Uri("https://unauthorized.example.test/")))],
            [ClientCertificateTokenUnlockResult.Succeeded(session)]);
        session.Order = harness.Order;

        await harness.Launcher.StartAsync();
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Monitor.PublishAsync([MatchingDevice()]);

        Assert.Equal(AuthenticationStage.Failed, harness.Authentication.CurrentState.Stage);
        Assert.Equal(
            AuthenticationErrorCategory.UnauthorizedTarget,
            harness.Authentication.CurrentState.ErrorCategory);
        Assert.Null(harness.Control.Selections.Single().SelectedCertificate);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Equal(1, harness.Control.NavigateCount);
    }

    [Theory]
    [InlineData(FailureKind.CertificateMissing)]
    [InlineData(FailureKind.CertificateConflict)]
    public async Task AutomaticCertificateMatchUnavailable_PassesThroughToBrowserPicker(
        FailureKind failure)
    {
        var session = new RecordingSession();
        var request = failure switch
        {
            FailureKind.CertificateMissing => MatchingRequest(certificates: []),
            FailureKind.CertificateConflict => MatchingRequest(
                certificates: [MatchingCertificate(), MatchingCertificate()]),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Pending(request)],
            [ClientCertificateTokenUnlockResult.Succeeded(session)]);
        session.Order = harness.Order;

        var start = harness.Launcher.StartAsync();
        await harness.Control.FirstRequestProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AuthenticationStage.Authenticating, harness.Authentication.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.None, harness.Authentication.CurrentState.ErrorCategory);
        Assert.Null(harness.Control.Selections.Single().SelectedCertificate);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Equal(1, harness.Control.NavigateCount);
        Assert.False(session.Disposed.Task.IsCompleted);

        harness.Control.CompleteNavigation(0, success: false);
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(ClientCertificateTokenUnlockStatus.PinRejected, AuthenticationStage.RequiresPinSetup)]
    [InlineData(ClientCertificateTokenUnlockStatus.ProviderUnavailable, AuthenticationStage.Incompatible)]
    [InlineData(ClientCertificateTokenUnlockStatus.Unsupported, AuthenticationStage.Incompatible)]
    public async Task TokenFailure_DoesNotNavigateOrRetryWithinOrAcrossInsertionCyclesWhenPinWasRejected(
        ClientCertificateTokenUnlockStatus status,
        AuthenticationStage expectedStage)
    {
        await using var harness = await Harness.CreateAsync(
            [],
            [
                ClientCertificateTokenUnlockResult.Failed(status),
                ClientCertificateTokenUnlockResult.Failed(status)
            ]);

        await harness.Launcher.StartAsync();
        await harness.Monitor.PublishAsync([MatchingDevice()]);
        await harness.Monitor.PublishAsync([]);
        await harness.Monitor.PublishAsync([MatchingDevice()]);

        Assert.Equal(expectedStage, harness.Authentication.CurrentState.Stage);
        Assert.Equal(0, harness.Control.NavigateCount);
        if (status == ClientCertificateTokenUnlockStatus.PinRejected)
        {
            Assert.Equal(1, harness.PinStore.ReadCount);
            Assert.Equal(1, harness.Unlocker.AttemptCount);
            Assert.Equal(1, harness.PinStore.DeleteCount);
            Assert.Null(harness.StampStore.StoredStamp);
        }
        else
        {
            Assert.Equal(2, harness.PinStore.ReadCount);
            Assert.Equal(2, harness.Unlocker.AttemptCount);
        }
    }

    [Fact]
    public async Task HandshakeTimeout_ReleasesSessionAndDoesNotSubmitPinAgain()
    {
        var session = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Pending(MatchingRequest())],
            [ClientCertificateTokenUnlockResult.Succeeded(session)],
            new ImmediateTimeoutScheduler());
        session.Order = harness.Order;
        using var cancellation = new CancellationTokenSource();

        var start = harness.Launcher.StartAsync(cancellation.Token);
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Control.FirstRequestProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Monitor.PublishAsync([MatchingDevice()]);

        Assert.Equal(AuthenticationStage.Failed, harness.Authentication.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.TimedOut, harness.Authentication.CurrentState.ErrorCategory);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Equal(1, harness.Control.NavigateCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    [Fact]
    public async Task RemovalAndReinsert_CreateANewCycleAndAuthenticateExactlyOncePerCycle()
    {
        var firstSession = new RecordingSession();
        var secondSession = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [
                NavigationPlan.Success(MatchingRequest()),
                NavigationPlan.Success(MatchingRequest())
            ],
            [
                ClientCertificateTokenUnlockResult.Succeeded(firstSession),
                ClientCertificateTokenUnlockResult.Succeeded(secondSession)
            ]);
        firstSession.Order = harness.Order;
        secondSession.Order = harness.Order;

        await harness.Launcher.StartAsync();
        await firstSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Monitor.PublishAsync([]);

        Assert.Equal(LauncherStage.WaitingForUsb, harness.Launcher.CurrentState.Stage);
        Assert.Equal(AuthenticationStage.WaitingForDevice, harness.Authentication.CurrentState.Stage);

        await harness.Monitor.PublishAsync([MatchingDevice()]);
        await secondSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, harness.PinStore.ReadCount);
        Assert.Equal(2, harness.Unlocker.AttemptCount);
        Assert.Equal(2, harness.Control.NavigateCount);
        Assert.Equal(2, harness.Control.Selections.Count(selection => selection.SelectedCertificate is not null));
        Assert.Equal(AuthenticationStage.Ready, harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
    }

    [Fact]
    public async Task RemovalAndReinsert_DuringActiveNavigation_WaitsForTheStaleNavigationThenStartsTheNewCycle()
    {
        var firstSession = new RecordingSession();
        var secondSession = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [
                NavigationPlan.Pending(MatchingRequest()),
                NavigationPlan.Success(MatchingRequest())
            ],
            [
                ClientCertificateTokenUnlockResult.Succeeded(firstSession),
                ClientCertificateTokenUnlockResult.Succeeded(secondSession)
            ]);
        firstSession.Order = harness.Order;
        secondSession.Order = harness.Order;

        var initialStart = harness.Launcher.StartAsync();
        await harness.Control.FirstRequestProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Monitor.PublishAsync([]);
        await firstSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var reinsert = harness.Monitor.PublishAsync([MatchingDevice()]);

        await initialStart.WaitAsync(TimeSpan.FromSeconds(2));
        await reinsert.WaitAsync(TimeSpan.FromSeconds(2));
        await secondSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, harness.PinStore.ReadCount);
        Assert.Equal(2, harness.Unlocker.AttemptCount);
        Assert.Equal(2, harness.Control.NavigateCount);
        Assert.Equal(1, harness.Control.StopCount);
        Assert.Equal(AuthenticationStage.Ready, harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
    }

    [Fact]
    public async Task RemovalAndReinsert_WhenStoppedNavigationNeverTerminates_FailsClosedWithoutNewSession()
    {
        var firstSession = new RecordingSession();
        await using var harness = await Harness.CreateAsync(
            [NavigationPlan.Pending(MatchingRequest())],
            [ClientCertificateTokenUnlockResult.Succeeded(firstSession)],
            cancelledNavigationStopTimeout: TimeSpan.FromMilliseconds(10));
        firstSession.Order = harness.Order;
        harness.Control.CompleteNavigationOnStop = false;

        var initialStart = harness.Launcher.StartAsync();
        await harness.Control.FirstRequestProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var removal = harness.Monitor.PublishAsync([]);
        await harness.Control.NavigationStopInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reinsert = harness.Monitor.PublishAsync([MatchingDevice()]);

        await removal.WaitAsync(TimeSpan.FromSeconds(2));
        await initialStart.WaitAsync(TimeSpan.FromSeconds(2));
        await reinsert.WaitAsync(TimeSpan.FromSeconds(2));
        await firstSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal(1, harness.Unlocker.AttemptCount);
        Assert.Equal(1, harness.Control.NavigateCount);
        Assert.Equal(1, harness.Control.StopCount);
        Assert.Equal(AuthenticationStage.WaitingForDevice, harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Error, harness.Launcher.CurrentState.Stage);
        Assert.Contains("重新启动应用", harness.Launcher.CurrentState.ErrorDetail);
    }

    private static readonly ClientCertificateAuthenticationOptions Authentication = new(
        "integration-auth",
        "example.test",
        443,
        new(
            "00112233445566778899AABBCCDDEEFF00112233",
            "CN=Integration Test",
            "CN=Integration Issuer"),
        new("Test Provider", 1, "Test Container", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/integration-auth");

    private static LauncherProfile Profile() => new(
        "https://example.test/entry",
        new("1234", "5678", "Integration USB"),
        Authentication);

    private static UsbDeviceDescriptor MatchingDevice() => new(
        "USB\\VID_1234&PID_5678\\INTEGRATION",
        ["USB\\VID_1234&PID_5678"],
        "Integration USB",
        [],
        [],
        []);

    private static WebClientCertificate MatchingCertificate() => new(
        Authentication.Certificate.Sha1Fingerprint,
        Authentication.Certificate.Subject,
        Authentication.Certificate.Issuer);

    private static WebClientCertificateRequest MatchingRequest(
        Uri? requestUri = null,
        IReadOnlyList<WebClientCertificate>? certificates = null) => new(
        requestUri ?? new Uri("https://example.test/auth"),
        false,
        certificates ?? [MatchingCertificate()]);

    private static void AssertOrdered(IReadOnlyList<string> actual, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var index = actual.ToList().IndexOf(item);
            Assert.True(index > previous, $"Expected '{item}' after index {previous}. Actual: {string.Join(", ", actual)}");
            previous = index;
        }
    }

    public enum FailureKind
    {
        UnauthorizedTarget,
        CertificateMissing,
        CertificateConflict
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            List<string> order,
            FakeUsbMonitor monitor,
            FakePinStore pinStore,
            FakeStampStore stampStore,
            FakeUnlocker unlocker,
            ScriptedWebViewControl control,
            WebView2BrowserHost browser,
            AuthenticationCoordinator authentication,
            ClientCertificateAuthenticationFlowRouter router,
            LauncherCoordinator launcher)
        {
            Order = order;
            Monitor = monitor;
            PinStore = pinStore;
            StampStore = stampStore;
            Unlocker = unlocker;
            Control = control;
            Browser = browser;
            Authentication = authentication;
            Router = router;
            Launcher = launcher;
        }

        public List<string> Order { get; }
        public FakeUsbMonitor Monitor { get; }
        public FakePinStore PinStore { get; }
        public FakeStampStore StampStore { get; }
        public FakeUnlocker Unlocker { get; }
        public ScriptedWebViewControl Control { get; }
        public WebView2BrowserHost Browser { get; }
        public AuthenticationCoordinator Authentication { get; }
        public ClientCertificateAuthenticationFlowRouter Router { get; }
        public LauncherCoordinator Launcher { get; }
        public ManualAuthenticationGate? ManualGate { get; private init; }

        public static async Task<Harness> CreateAsync(
            IReadOnlyList<NavigationPlan> plans,
            IReadOnlyList<ClientCertificateTokenUnlockResult> unlockResults,
            IBrowserTimeoutScheduler? timeoutScheduler = null,
            TimeSpan? cancelledNavigationStopTimeout = null,
            bool ccbMode = false)
        {
            var order = new List<string>();
            var monitor = new FakeUsbMonitor(order, [ccbMode ? CcbDevice() : MatchingDevice()]);
            var pinStore = new FakePinStore(order, "integration-test-pin".ToCharArray());
            var stampStore = new FakeStampStore(order, new(
                ClientCertificateAuthenticationEndToEndTests.Authentication.Id,
                ClientCertificateAuthenticationEndToEndTests.Authentication.GetNormalizedHash()));
            var unlocker = new FakeUnlocker(order, unlockResults);
            var control = new ScriptedWebViewControl(order, plans);
            var scheduler = timeoutScheduler ?? new BrowserTimeoutScheduler();
            var browser = cancelledNavigationStopTimeout is { } stopTimeout
                ? new WebView2BrowserHost(control, scheduler, stopTimeout)
                : new WebView2BrowserHost(control, scheduler);
            var authentication = new AuthenticationCoordinator(
                ClientCertificateAuthenticationEndToEndTests.Authentication,
                pinStore,
                stampStore,
                unlocker);
            var router = new ClientCertificateAuthenticationFlowRouter(
                ClientCertificateAuthenticationEndToEndTests.Authentication,
                browser,
                new Uri(Profile().TargetUrl),
                authentication,
                authentication);
            await browser.ConfigureClientCertificateAuthenticationAsync(
                router,
                router,
                TimeSpan.FromSeconds(30));
            var authenticationGate = new AuthenticationLauncherGate(authentication);
            ManualAuthenticationGate? manualGate = null;
            if (ccbMode)
            {
                manualGate = new(authenticationGate, authentication);
                await manualGate.AllowBusinessAsync();
            }
            var launcher = new LauncherCoordinator(
                monitor,
                browser,
                ccbMode ? Profile() with { UsbDetectionStrategy = "ccbUsbStor" } : Profile(),
                (ILauncherAuthenticationGate?)manualGate ?? authenticationGate);
            return new(
                order,
                monitor,
                pinStore,
                stampStore,
                unlocker,
                control,
                browser,
                authentication,
                router,
                launcher) { ManualGate = manualGate };
        }

        public async ValueTask DisposeAsync()
        {
            await Launcher.DisposeAsync();
            await Router.DisposeAsync();
            await Authentication.DisposeAsync();
            await Browser.DisposeAsync();
        }
    }

    private sealed class FakeUsbMonitor(
        List<string> order,
        IReadOnlyList<UsbDeviceDescriptor> initialDevices) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            order.Add("monitor-read");
            return Task.FromResult(initialDevices);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            order.Add("monitor-start");
            return Task.CompletedTask;
        }

        public async Task PublishAsync(IReadOnlyList<UsbDeviceDescriptor> devices)
        {
            if (DevicesChanged is null)
            {
                return;
            }

            var eventArgs = new UsbDevicesChangedEventArgs(devices);
            foreach (UsbDevicesChangedEventHandler subscriber in DevicesChanged.GetInvocationList())
            {
                await subscriber(this, eventArgs);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePinStore(List<string> order, char[] storedPin) : IClientCertificatePinStore
    {
        private char[]? _storedPin = storedPin;

        public int ReadCount { get; private set; }
        public int DeleteCount { get; private set; }

        public ValueTask<ClientCertificatePin?> ReadAsync(
            string targetName,
            CancellationToken cancellationToken = default)
        {
            order.Add("pin-read");
            ReadCount++;
            return ValueTask.FromResult(
                _storedPin is null ? null : ClientCertificatePin.CopyOf(_storedPin.AsMemory()));
        }

        public ValueTask SaveAsync(
            string targetName,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string targetName,
            CancellationToken cancellationToken = default)
        {
            order.Add("pin-delete");
            DeleteCount++;
            _storedPin = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStampStore(
        List<string> order,
        ClientCertificateValidationStamp? stamp) : IClientCertificateValidationStampStore
    {
        public ClientCertificateValidationStamp? StoredStamp { get; private set; } = stamp;

        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(
            string authenticationId,
            CancellationToken cancellationToken = default)
        {
            order.Add("stamp-read");
            return ValueTask.FromResult(StoredStamp);
        }

        public ValueTask SaveAsync(
            ClientCertificateValidationStamp value,
            CancellationToken cancellationToken = default)
        {
            StoredStamp = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string authenticationId,
            CancellationToken cancellationToken = default)
        {
            order.Add("stamp-delete");
            StoredStamp = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeUnlocker(
        List<string> order,
        IReadOnlyList<ClientCertificateTokenUnlockResult> results) : IClientCertificateTokenUnlocker
    {
        private readonly Queue<ClientCertificateTokenUnlockResult> _results = new(results);

        public int AttemptCount { get; private set; }

        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            order.Add("unlock");
            AttemptCount++;
            return ValueTask.FromResult(
                _results.Count == 0
                    ? ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.UnknownFailure)
                    : _results.Dequeue());
        }
    }

    private sealed class RecordingSession : IClientCertificateTokenUnlockSession
    {
        private int _disposed;

        public List<string>? Order { get; set; }
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Order?.Add("session-dispose");
            Disposed.TrySetResult();
        }
    }

    private sealed record NavigationPlan(
        WebClientCertificateRequest Request,
        bool Complete,
        bool IsSuccess)
    {
        public static NavigationPlan Success(WebClientCertificateRequest request) =>
            new(request, Complete: true, IsSuccess: true);

        public static NavigationPlan Pending(WebClientCertificateRequest request) =>
            new(request, Complete: false, IsSuccess: false);
    }

    private sealed class ScriptedWebViewControl(
        List<string> order,
        IReadOnlyList<NavigationPlan> plans) : IWebView2Control
    {
        private readonly Queue<NavigationPlan> _pendingPlans = new(plans);
        private WebClientCertificateRequestHandler? _handler;
        private long _nextNavigationId;
        private readonly List<long> _navigationIds = [];

        public event EventHandler<WebNavigationStartingEventArgs>? NavigationStarting;

        public event EventHandler<WebNavigationCompletedEventArgs>? NavigationCompleted;

        public IReadOnlyList<NavigationPlan> Plans { get; } = plans;
        public List<WebClientCertificateSelection> Selections { get; } = [];
        public int NavigateCount { get; private set; }
        public int StopCount { get; private set; }
        public bool CompleteNavigationOnStop { get; set; } = true;
        public TaskCompletionSource FirstRequestProcessed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NavigationStopInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            order.Add("browser-initialize");
            return Task.CompletedTask;
        }

        public Task RegisterClientCertificateRequestHandlerAsync(
            WebClientCertificateRequestHandler handler,
            CancellationToken cancellationToken)
        {
            order.Add("certificate-handler-register");
            _handler = handler;
            return Task.CompletedTask;
        }

        public Task<long> StartNavigationAsync(Uri target, CancellationToken cancellationToken)
        {
            order.Add("navigate");
            NavigateCount++;
            var navigationId = Interlocked.Increment(ref _nextNavigationId);
            _navigationIds.Add(navigationId);
            var plan = _pendingPlans.Dequeue();
            _ = RunNavigationAsync(navigationId, plan);
            return Task.FromResult(navigationId);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task StopNavigationAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            NavigationStopInvoked.TrySetResult();
            if (CompleteNavigationOnStop && _navigationIds.Count > 0)
            {
                NavigationCompleted?.Invoke(
                    this,
                    new(_navigationIds[^1], false, "OperationCanceled"));
            }

            return Task.CompletedTask;
        }

        public void CompleteNavigation(int index, bool success) =>
            NavigationCompleted?.Invoke(
                this,
                new(_navigationIds[index], success, success ? null : "test-failure"));

        private async Task RunNavigationAsync(long navigationId, NavigationPlan plan)
        {
            await Task.Yield();
            order.Add("certificate-request");
            var selection = await _handler!(plan.Request, CancellationToken.None);
            Selections.Add(selection);
            if (selection.SelectedCertificate is not null)
            {
                order.Add("certificate-selected");
            }

            FirstRequestProcessed.TrySetResult();
            if (plan.Complete)
            {
                order.Add(plan.IsSuccess ? "navigation-success" : "navigation-failure");
                NavigationCompleted?.Invoke(
                    this,
                    new(navigationId, plan.IsSuccess, plan.IsSuccess ? null : "test-failure"));
            }
        }
    }

    private sealed class ImmediateTimeoutScheduler : IBrowserTimeoutScheduler
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
