using System.Collections.Concurrent;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Composition;

public sealed class LauncherApplicationComposerTests
{
    [Fact]
    public async Task ComposeAsync_WhenCancelledDuringConfiguration_DiscardsLateRuntimeAndReleasesOwners()
    {
        var order = new ConcurrentQueue<string>();
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new RecordingConfigurableBrowser(order, configure: () => configured.Task);
        var composer = new LauncherApplicationComposer(new RecordingAuthenticationRuntimeFactory(order));
        using var cancellation = new CancellationTokenSource();
        var pending = composer.ComposeAsync(Profile(Authentication()), new RecordingMonitor(order, []), browser, cancellation.Token);
        cancellation.Cancel();
        configured.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(new[] { "auth-dispose", "browser-dispose", "monitor-dispose" },
            order.Where(value => value.EndsWith("-dispose")).OrderBy(value => value));
        Assert.DoesNotContain("monitor-start", order);
    }

    [Fact]
    public async Task DisposeAsync_WhenMonitorWaitsForBrowserRelease_StartsOtherOwnerCleanup()
    {
        var order = new ConcurrentQueue<string>();
        var browserReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new RecordingConfigurableBrowser(order, () => { browserReleased.SetResult(); return ValueTask.CompletedTask; });
        var monitor = new RecordingMonitor(order, [], () => new ValueTask(browserReleased.Task));
        var composer = new LauncherApplicationComposer(new RecordingAuthenticationRuntimeFactory(order));
        var runtime = await composer.ComposeAsync(Profile(Authentication()), monitor, browser);

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("monitor-dispose", order);
        Assert.Contains("browser-dispose", order);
        Assert.Contains("auth-dispose", order);
    }

    [Fact]
    public async Task DisposeAsync_WhenOneOwnerThrows_StillReleasesOtherOwners()
    {
        var order = new ConcurrentQueue<string>();
        var browser = new RecordingConfigurableBrowser(order, () => throw new InvalidOperationException("test"));
        var composer = new LauncherApplicationComposer(new RecordingAuthenticationRuntimeFactory(order));
        var runtime = await composer.ComposeAsync(Profile(Authentication()), new RecordingMonitor(order, []), browser);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DisposeAsync().AsTask());

        Assert.Contains("monitor-dispose", order);
        Assert.Contains("auth-dispose", order);
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledAgainWhileCleaning_DoesNotReportFalseCompletion()
    {
        var order = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingAuthenticationRuntimeFactory(order, () => new ValueTask(release.Task));
        var composer = new LauncherApplicationComposer(factory);
        var runtime = await composer.ComposeAsync(Profile(Authentication()), new RecordingMonitor(order, []), new RecordingConfigurableBrowser(order));
        var first = runtime.DisposeAsync().AsTask();
        try
        {
            var repeated = runtime.DisposeAsync().AsTask();
            Assert.False(repeated.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, repeated);
            Assert.Equal(1, order.Count(value => value == "auth-dispose"));
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    [Theory]
    [InlineData(AuthenticationStage.AwaitingCertificate, true)]
    [InlineData(AuthenticationStage.Authenticating, true)]
    [InlineData(AuthenticationStage.Ready, true)]
    [InlineData(AuthenticationStage.RequiresPinSetup, false)]
    [InlineData(AuthenticationStage.Incompatible, false)]
    [InlineData(AuthenticationStage.Failed, false)]
    public void AuthenticationLaunchGate_OnlyBlocksStatesThatMustNotNavigate(
        AuthenticationStage stage,
        bool expected)
    {
        Assert.Equal(expected, AuthenticationLauncherGate.AllowsNavigation(
            isTargetDevicePresent: true,
            stage));
    }

    [Fact]
    public async Task ComposeAsync_WithoutAuthentication_DoesNotCreateOrConfigureAuthentication()
    {
        var order = new ConcurrentQueue<string>();
        var browser = new RecordingConfigurableBrowser(order);
        var monitor = new RecordingMonitor(order, [MatchingDevice()]);
        var factory = new RecordingAuthenticationRuntimeFactory(order);
        var composer = new LauncherApplicationComposer(factory);

        await using var runtime = await composer.ComposeAsync(Profile(authentication: null), monitor, browser);
        await runtime.Launcher.StartAsync();

        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, browser.ConfigureCount);
        Assert.DoesNotContain(order, item => item.StartsWith("auth-", StringComparison.Ordinal));
        Assert.Contains("navigate", order);
    }

    [Fact]
    public async Task ComposeAsync_WithAuthentication_ConfiguresHandlerBeforePresenceAndNavigation()
    {
        var order = new ConcurrentQueue<string>();
        var browser = new RecordingConfigurableBrowser(order);
        var monitor = new RecordingMonitor(order, [MatchingDevice()]);
        var factory = new RecordingAuthenticationRuntimeFactory(order);
        var composer = new LauncherApplicationComposer(factory);

        await using var runtime = await composer.ComposeAsync(Profile(Authentication()), monitor, browser);
        await runtime.Launcher.StartAsync();

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(new[]
        {
            "auth-create", "configure-auth", "monitor-start", "monitor-read",
            "auth-present", "initialize", "navigate"
        }, order);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesAllOwnersExactlyOnce()
    {
        var order = new ConcurrentQueue<string>();
        var browser = new RecordingConfigurableBrowser(order);
        var monitor = new RecordingMonitor(order, []);
        var factory = new RecordingAuthenticationRuntimeFactory(order);
        var composer = new LauncherApplicationComposer(factory);
        var runtime = await composer.ComposeAsync(Profile(Authentication()), monitor, browser);
        await runtime.Launcher.StartAsync();
        order.Clear();

        await runtime.DisposeAsync();

        Assert.Equal(new[] { "auth-dispose", "browser-dispose", "monitor-dispose" }, order.OrderBy(value => value));
    }

    private static LauncherProfile Profile(ClientCertificateAuthenticationOptions? authentication) => new(
        "https://example.test/entry",
        new("1234", "5678", "Test USB"),
        authentication);

    private static ClientCertificateAuthenticationOptions Authentication() => new(
        "test-auth", "example.test", 443,
        new("00112233445566778899AABBCCDDEEFF00112233", "CN=Test", "CN=Issuer"),
        new("Test Provider", 1, "Test Container", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/test-auth");

    private static UsbDeviceDescriptor MatchingDevice() => new(
        "USB\\VID_1234&PID_5678\\TEST",
        ["USB\\VID_1234&PID_5678"],
        "Test USB",
        [], [], []);

    private sealed class RecordingAuthenticationRuntimeFactory(ConcurrentQueue<string> order, Func<ValueTask>? dispose = null)
        : IClientCertificateAuthenticationRuntimeFactory
    {
        public int CreateCount { get; private set; }

        public IClientCertificateAuthenticationRuntime Create(
            ClientCertificateAuthenticationOptions authentication,
            ICorrelatedBrowserHost browser,
            Uri target)
        {
            CreateCount++;
            order.Enqueue("auth-create");
            return new RecordingAuthenticationRuntime(order, dispose);
        }
    }

    private sealed class RecordingAuthenticationRuntime(ConcurrentQueue<string> order, Func<ValueTask>? dispose = null)
        : IClientCertificateAuthenticationRuntime
    {
        private readonly RejectingBrowserAuthentication _browserAuthentication = new();
        public event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged { add { } remove { } }
        public AuthenticationState CurrentState { get; } = new(AuthenticationStage.WaitingForDevice, null);
        public IBrowserClientCertificateRequestHandler Handler => _browserAuthentication;
        public IBrowserClientCertificateAuthenticationReporter Reporter => _browserAuthentication;
        public ILauncherAuthenticationGate LaunchGate { get; } = new RecordingGate(order);
        public IClientCertificatePinSetupService PinSetupService { get; } = new RecordingSetupService();
        public INativePromptBrokerPrerequisites BrokerPrerequisites { get; } =
            NullNativePromptBrokerPrerequisites.Instance;
        public ValueTask DisposeAsync() { order.Enqueue("auth-dispose"); return dispose?.Invoke() ?? ValueTask.CompletedTask; }
    }

    private sealed class RecordingGate(ConcurrentQueue<string> order) : ILauncherAuthenticationGate
    {
        public Task<bool> HandleDevicePresenceAsync(bool isTargetDevicePresent, CancellationToken cancellationToken = default)
        {
            order.Enqueue(isTargetDevicePresent ? "auth-present" : "auth-absent");
            return Task.FromResult(isTargetDevicePresent);
        }
    }

    private sealed class RecordingSetupService : IClientCertificatePinSetupService
    {
        public Task<bool> SaveFixedAsync(ClientCertificatePin pin, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<ClientCertificateSetupValidationResult> ValidateAndSaveAsync(ClientCertificatePin pin, CancellationToken cancellationToken = default) => Task.FromResult(ClientCertificateSetupValidationResult.Succeeded());
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingBrowserAuthentication : IBrowserClientCertificateRequestHandler, IBrowserClientCertificateAuthenticationReporter
    {
        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(ClientCertificateRequestSnapshot request, CancellationToken cancellationToken = default) => ValueTask.FromResult(ClientCertificateRequestMatchResult.InvalidRequest());
        public Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) => Task.FromResult<ClientCertificateSelectionAuthorization?>(null);
        public Task ReportTlsCompletedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportTlsFailureAsync(AuthenticationAttempt attempt, int? publicErrorCode = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportTimeoutAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportNavigationCompletedWithoutCertificateAsync(long browserOperationId, bool isSuccess, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingConfigurableBrowser(ConcurrentQueue<string> order, Func<ValueTask>? dispose = null, Func<Task>? configure = null) : IClientCertificateConfigurableBrowserHost, IAsyncDisposable
    {
        public int ConfigureCount { get; private set; }
        public Task ConfigureClientCertificateAuthenticationAsync(IBrowserClientCertificateRequestHandler handler, IBrowserClientCertificateAuthenticationReporter reporter, TimeSpan timeout, CancellationToken cancellationToken = default) { ConfigureCount++; order.Enqueue("configure-auth"); return configure?.Invoke() ?? Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) { order.Enqueue("initialize"); return Task.CompletedTask; }
        public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default) { order.Enqueue("navigate"); return Task.CompletedTask; }
        public Task NavigateAsync(Uri target, long browserOperationId, CancellationToken cancellationToken = default) { order.Enqueue("navigate"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { order.Enqueue("browser-dispose"); return dispose?.Invoke() ?? ValueTask.CompletedTask; }
    }

    private sealed class RecordingMonitor(ConcurrentQueue<string> order, IReadOnlyList<UsbDeviceDescriptor> devices, Func<ValueTask>? dispose = null) : IUsbDeviceMonitor
    {
        public event UsbDevicesChangedEventHandler? DevicesChanged { add { } remove { } }
        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default) { order.Enqueue("monitor-read"); return Task.FromResult(devices); }
        public Task StartAsync(CancellationToken cancellationToken = default) { order.Enqueue("monitor-start"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { order.Enqueue("monitor-dispose"); return dispose?.Invoke() ?? ValueTask.CompletedTask; }
    }
}
