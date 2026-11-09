using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Integration;

public sealed class PsbcBusinessAuthenticationEndToEndTests
{
    private static readonly Uri Target = new("https://corpebank.psbc.com/#/login");

    [Fact]
    public async Task KeyInsertedBeforeBusinessStart_NavigatesAndSubmitsPinExactlyOnce()
    {
        await using var harness = await Harness.CreateAsync(keyInitiallyPresent: true);

        await harness.Launcher.StartAsync();

        Assert.Equal(0, harness.Control.NavigateCount);
        Assert.Equal(0, harness.PinStore.ReadCount);
        Assert.Equal(0, harness.NativePrompt.ArmCount);

        await harness.ManualGate.AllowBusinessAsync();
        await harness.Launcher.ResumeNavigationAfterAuthenticationSetupAsync();
        await harness.Monitor.PublishAsync([PsbcDevice()]);

        AssertSuccessfulAuthentication(harness);
    }

    [Fact]
    public async Task KeyInsertedAfterBusinessStart_ContinuesSameSessionAndSubmitsPinExactlyOnce()
    {
        await using var harness = await Harness.CreateAsync(keyInitiallyPresent: false);

        await harness.Launcher.StartAsync();
        await harness.ManualGate.AllowBusinessAsync();
        await harness.Launcher.ResumeNavigationAfterAuthenticationSetupAsync();

        Assert.Equal(0, harness.Control.NavigateCount);
        Assert.Equal(0, harness.PinStore.ReadCount);
        Assert.Equal(0, harness.NativePrompt.ArmCount);

        await harness.Monitor.PublishAsync([PsbcDevice()]);
        await harness.Monitor.PublishAsync([PsbcDevice()]);

        AssertSuccessfulAuthentication(harness);
    }

    [Fact]
    public async Task MissingPinSetup_StillNavigatesWithoutAuthorizingAutomaticAuthentication()
    {
        await using var harness = await Harness.CreateAsync(
            keyInitiallyPresent: true,
            hasStoredCredential: false);

        await harness.Launcher.StartAsync();
        await harness.ManualGate.AllowBusinessAsync();
        await harness.Launcher.ResumeNavigationAfterAuthenticationSetupAsync();

        Assert.Equal([Target], harness.Control.NavigationTargets);
        Assert.Equal(1, harness.Control.NavigateCount);
        Assert.Equal(2, harness.Control.CertificateRequestCount);
        Assert.Equal(2, harness.Control.Selections.Count);
        Assert.All(harness.Control.Selections, selection =>
        {
            Assert.False(selection.Handled);
            Assert.Null(selection.SelectedCertificate);
            Assert.NotNull(selection.Authorization);
        });
        Assert.Empty(harness.Control.AppliedCertificates);
        Assert.Equal(0, harness.Control.SelectionCommitCount);
        Assert.Equal(0, harness.PinStore.ReadCount);
        Assert.Equal(0, harness.NativePrompt.ArmCount);
        Assert.Equal(0, harness.NativePrompt.PinSubmissionCount);
        Assert.Equal(
            AuthenticationStage.RequiresPinSetup,
            harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
    }

    private static void AssertSuccessfulAuthentication(Harness harness)
    {
        Assert.Equal([Target], harness.Control.NavigationTargets);
        Assert.Equal(1, harness.Control.NavigateCount);
        Assert.Equal(2, harness.Control.CertificateRequestCount);
        Assert.Single(harness.Control.AppliedCertificates);
        Assert.Same(harness.Control.ExpectedCertificate, harness.Control.AppliedCertificates[0]);
        Assert.Equal(1, harness.Control.SelectionCommitCount);
        Assert.Equal(1, harness.PinStore.ReadCount);
        Assert.Equal([Authentication().CredentialTargetName], harness.PinStore.ReadTargets);
        Assert.Equal(1, harness.NativePrompt.ArmCount);
        Assert.Equal(1, harness.NativePrompt.PinSubmissionCount);
        Assert.Equal(0, harness.Unlocker.AttemptCount);
        Assert.Equal(AuthenticationStage.Ready, harness.Authentication.CurrentState.Stage);
        Assert.Equal(LauncherStage.Ready, harness.Launcher.CurrentState.Stage);
        Assert.Equal(
            Authentication().GetNormalizedHash(),
            harness.StampStore.StoredStamp?.AuthenticationHash);
        Assert.Equal(1, harness.NativePrompt.Attempt.DisposeCount);
    }

    private static ClientCertificateAuthenticationOptions Authentication() =>
        new(
            "psbc-client-certificate",
            "corpebank.psbc.com",
            443,
            new(string.Empty, string.Empty, string.Empty),
            new(string.Empty, 0, string.Empty, string.Empty),
            "UsbWebLauncher/ClientCertificate/psbc-client-certificate",
            NativePromptContinuation: new(
                true,
                "test-pipe",
                "psbc-uk-password-v1",
                @"C:\Program Files\Test Vendor\Prompt.exe",
                new string('A', 64),
                new string('B', 40),
                "Synthetic PSBC Prompt",
                "#32770",
                ["Edit"],
                10_000),
            AuthenticationMode: "BrowserNative");

    private static LauncherProfile Profile() =>
        new(
            Target.AbsoluteUri,
            new("1780", "0401", "Synthetic PSBC USB Key"),
            Authentication(),
            UsbDetectionStrategy: "psbcUsbKey");

    private static UsbDeviceDescriptor PsbcDevice() =>
        new(
            @"USB\VID_1780&PID_0401\SYNTHETIC",
            [@"USB\VID_1780&PID_0401"],
            "Synthetic PSBC USB Key",
            [],
            [],
            []);

    private static bool IsPsbcDevice(UsbDeviceDescriptor device) =>
        UsbDeviceMatcher.IsMatch(device, Profile().UsbDevice!);

    private static PsbcAuthenticationRuntimeSnapshot RuntimeSnapshot() =>
        new(
            OwnerSessionId: 10,
            OwnerRevision: 2,
            CurrentSessionId: 10,
            CurrentRevision: 2,
            IsPostalSavingsBank: true,
            IsSessionCurrent: true,
            OwnsCurrentRuntime: true,
            DetectionStatus: UsbKeyDetectionStatus.Ready,
            ReadyKey: UsbKeyDetectionResult.Ready(
                UsbKeyVendorFamily.Tendyron,
                PsbcDevice().InstanceId,
                "SYNTHETIC_PSBC_CONTAINER"),
            ContextRevision: 1);

    private static ClientCertificateCandidate PsbcCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            "1111111111111111111111111111111111111111",
            "Synthetic PSBC Certificate",
            "Synthetic PSBC Issuer",
            "CN=Synthetic PSBC Certificate",
            "CN=Synthetic PSBC Issuer",
            now.AddDays(-1),
            now.AddDays(1),
            "01020304",
            [new("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            new(
                ClientCertificateProviderKind.Csp,
                "Tendyron OnKey193 CSP PSBC v1.0",
                1,
                "SYNTHETIC_PSBC_CONTAINER",
                "AT_SIGNATURE",
                ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
                ClientCertificateProviderMetadataFailure.None));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            RecordingUsbMonitor monitor,
            RecordingPinStore pinStore,
            RecordingStampStore stampStore,
            RecordingUnlocker unlocker,
            TestNativePromptContinuation nativePrompt,
            PsbcWebViewControl control,
            WebView2BrowserHost browser,
            AuthenticationCoordinator authentication,
            ClientCertificateAuthenticationFlowRouter router,
            ManualAuthenticationGate manualGate,
            LauncherCoordinator launcher)
        {
            Monitor = monitor;
            PinStore = pinStore;
            StampStore = stampStore;
            Unlocker = unlocker;
            NativePrompt = nativePrompt;
            Control = control;
            Browser = browser;
            Authentication = authentication;
            Router = router;
            ManualGate = manualGate;
            Launcher = launcher;
        }

        public RecordingUsbMonitor Monitor { get; }
        public RecordingPinStore PinStore { get; }
        public RecordingStampStore StampStore { get; }
        public RecordingUnlocker Unlocker { get; }
        public TestNativePromptContinuation NativePrompt { get; }
        public PsbcWebViewControl Control { get; }
        public WebView2BrowserHost Browser { get; }
        public AuthenticationCoordinator Authentication { get; }
        public ClientCertificateAuthenticationFlowRouter Router { get; }
        public ManualAuthenticationGate ManualGate { get; }
        public LauncherCoordinator Launcher { get; }

        public static async Task<Harness> CreateAsync(
            bool keyInitiallyPresent,
            bool hasStoredCredential = true)
        {
            var authenticationOptions = Authentication();
            var monitor = new RecordingUsbMonitor(
                keyInitiallyPresent ? [PsbcDevice()] : []);
            var pinStore = new RecordingPinStore(
                hasStoredCredential
                    ? "integration-psbc-pin".ToCharArray()
                    : null);
            var stampStore = new RecordingStampStore(
                hasStoredCredential
                    ? ClientCertificateValidationStamp.CreatePending(
                        authenticationOptions.Id,
                        authenticationOptions.GetNormalizedHash())
                    : null);
            var unlocker = new RecordingUnlocker();
            var nativePrompt = new TestNativePromptContinuation();
            var context = new FixedPsbcRuntimeContext(RuntimeSnapshot());
            var certificate = new WebClientCertificate(PsbcCandidate(), new object());
            var control = new PsbcWebViewControl(certificate);
            var browser = new WebView2BrowserHost(control);
            var authentication = new AuthenticationCoordinator(
                authenticationOptions,
                pinStore,
                stampStore,
                unlocker,
                nativePrompt,
                context);
            var router = new ClientCertificateAuthenticationFlowRouter(
                authenticationOptions,
                browser,
                Target,
                authentication,
                authentication,
                nativePrompt);
            await browser.ConfigureClientCertificateAuthenticationAsync(
                router,
                router,
                TimeSpan.FromSeconds(30));
            var manualGate = new ManualAuthenticationGate(
                new AuthenticationLauncherGate(
                    BankAuthenticationRuntimeKind.PostalSavingsBank,
                    authentication),
                authentication);
            var launcher = new LauncherCoordinator(
                monitor,
                browser,
                Profile(),
                manualGate,
                IsPsbcDevice);
            return new(
                monitor,
                pinStore,
                stampStore,
                unlocker,
                nativePrompt,
                control,
                browser,
                authentication,
                router,
                manualGate,
                launcher);
        }

        public async ValueTask DisposeAsync()
        {
            await Launcher.DisposeAsync();
            await Router.DisposeAsync();
            await Authentication.DisposeAsync();
            await Browser.DisposeAsync();
        }
    }

    private sealed class RecordingUsbMonitor(
        IReadOnlyList<UsbDeviceDescriptor> initialDevices) : IUsbDeviceMonitor
    {
        private IReadOnlyList<UsbDeviceDescriptor> _devices = initialDevices;

        public event UsbDevicesChangedEventHandler? DevicesChanged;

        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_devices);

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task PublishAsync(IReadOnlyList<UsbDeviceDescriptor> devices)
        {
            _devices = devices;
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

    private sealed class RecordingPinStore(char[]? storedPin) : IClientCertificatePinStore
    {
        public int ReadCount { get; private set; }
        public List<string> ReadTargets { get; } = [];

        public ValueTask<ClientCertificatePin?> ReadAsync(
            string targetName,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            ReadTargets.Add(targetName);
            if (storedPin is null)
            {
                return ValueTask.FromResult<ClientCertificatePin?>(null);
            }

            return ValueTask.FromResult<ClientCertificatePin?>(
                ClientCertificatePin.CopyOf(storedPin.AsMemory()));
        }

        public ValueTask SaveAsync(
            string targetName,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string targetName,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingStampStore(
        ClientCertificateValidationStamp? initialStamp) :
        IClientCertificateValidationStampStore
    {
        public ClientCertificateValidationStamp? StoredStamp { get; private set; } = initialStamp;

        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(
            string authenticationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StoredStamp);

        public ValueTask SaveAsync(
            ClientCertificateValidationStamp stamp,
            CancellationToken cancellationToken = default)
        {
            StoredStamp = stamp;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string authenticationId,
            CancellationToken cancellationToken = default)
        {
            StoredStamp = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingUnlocker : IClientCertificateTokenUnlocker
    {
        public int AttemptCount { get; private set; }

        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            return ValueTask.FromResult(
                ClientCertificateTokenUnlockResult.Failed(
                    ClientCertificateTokenUnlockStatus.Unsupported));
        }
    }

    private sealed class TestNativePromptContinuation :
        IClientCertificateNativePromptContinuation
    {
        public SuccessfulNativePromptAttempt Attempt { get; } = new();
        public int ArmCount { get; private set; }
        public int PinSubmissionCount { get; private set; }

        public ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
            ClientCertificateAuthenticationOptions authentication,
            AuthenticationAttempt attempt,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            ArmCount++;
            if (!pin.AsMemory().IsEmpty)
            {
                PinSubmissionCount++;
            }

            return ValueTask.FromResult(
                new ClientCertificateNativePromptArmResult(
                    ClientCertificateNativePromptArmStatus.Armed,
                    Attempt));
        }
    }

    private sealed class SuccessfulNativePromptAttempt :
        IClientCertificateNativePromptAttempt
    {
        public Task<ClientCertificateNativePromptResult> Completion { get; } =
            Task.FromResult(
                new ClientCertificateNativePromptResult(
                    ClientCertificateNativePromptStatus.Success));

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedPsbcRuntimeContext(
        PsbcAuthenticationRuntimeSnapshot snapshot) :
        IPsbcAuthenticationRuntimeContext
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public PsbcAuthenticationRuntimeSnapshot Current { get; } = snapshot;
    }

    private sealed class PsbcWebViewControl(
        WebClientCertificate expectedCertificate) : IWebView2Control
    {
        private WebClientCertificateRequestHandler? _handler;
        private long _nextNavigationId;

        public event EventHandler<WebNavigationStartingEventArgs>? NavigationStarting;
        public event EventHandler<WebNavigationCompletedEventArgs>? NavigationCompleted;

        public WebClientCertificate ExpectedCertificate { get; } = expectedCertificate;
        public List<Uri> NavigationTargets { get; } = [];
        public List<WebClientCertificateSelection> Selections { get; } = [];
        public List<WebClientCertificate> AppliedCertificates { get; } = [];
        public int NavigateCount { get; private set; }
        public int CertificateRequestCount { get; private set; }
        public int SelectionCommitCount { get; private set; }

        public Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RegisterClientCertificateRequestHandlerAsync(
            WebClientCertificateRequestHandler handler,
            CancellationToken cancellationToken)
        {
            _handler = handler;
            return Task.CompletedTask;
        }

        public Task<long> StartNavigationAsync(
            Uri target,
            CancellationToken cancellationToken)
        {
            NavigationTargets.Add(target);
            NavigateCount++;
            var navigationId = Interlocked.Increment(ref _nextNavigationId);
            _ = RunNavigationAsync(navigationId);
            return Task.FromResult(navigationId);
        }

        public Task StopNavigationAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task RunNavigationAsync(long navigationId)
        {
            await Task.Yield();
            await DispatchCertificateRequestAsync();
            await DispatchCertificateRequestAsync();
            NavigationCompleted?.Invoke(this, new(navigationId, true));
        }

        private async Task DispatchCertificateRequestAsync()
        {
            CertificateRequestCount++;
            var selection = await _handler!(
                new WebClientCertificateRequest(
                    Target,
                    false,
                    [ExpectedCertificate]),
                CancellationToken.None);
            Selections.Add(selection);
            if (selection.SelectedCertificate is null ||
                selection.Authorization is null)
            {
                return;
            }

            var committed = selection.Authorization.TryCommit(() =>
            {
                AppliedCertificates.Add(selection.SelectedCertificate);
            });
            if (committed)
            {
                SelectionCommitCount++;
            }
        }
    }
}
