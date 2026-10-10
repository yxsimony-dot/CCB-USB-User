using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Windows.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.App.Composition;

public sealed class WindowsClientCertificateAuthenticationRuntimeFactory
    : IClientCertificateAuthenticationRuntimeFactory
{
    public IClientCertificateAuthenticationRuntime Create(
        ClientCertificateAuthenticationOptions authentication,
        ICorrelatedBrowserHost browser,
        Uri target)
    {
        var stores = WindowsClientCertificateAuthenticationStores.Create(
            authentication.Id,
            useValidationStamps: !authentication.UsesBrowserNativeAuthentication);
        if (!string.Equals(
                stores.CredentialTargetName,
                authentication.CredentialTargetName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("认证配置的凭据目标与本机存储目标不一致。");
        }

        var tokenUnlocker = WindowsClientCertificateTokenUnlockerFactory.Create(authentication);
        IClientCertificateNativePromptContinuation? nativePromptContinuation =
            authentication.NativePromptContinuation is { Enabled: true }
                ? new WindowsNativePromptContinuationClient()
                : null;
        var coordinator = new AuthenticationCoordinator(
            authentication,
            stores.PinStore,
            stores.ValidationStampStore,
            tokenUnlocker,
            nativePromptContinuation: nativePromptContinuation);
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            target,
            coordinator,
            coordinator,
            nativePromptContinuation);
        var manualGate = new ManualAuthenticationGate(new AuthenticationLauncherGate(coordinator), coordinator);
        var setupService = new ClientCertificatePinSetupService(
            authentication,
            stores.PinStore,
            stores.ValidationStampStore,
            tokenUnlocker,
            router,
            manualGate);
        return new WindowsClientCertificateAuthenticationRuntime(
            coordinator,
            router,
            setupService,
            manualGate,
            new WindowsNativePromptBrokerPrerequisites(
                nativePromptContinuation is not null),
            stores.PinStore);
    }
}

public sealed class WindowsClientCertificateAuthenticationRuntime :
    IClientCertificateAuthenticationRuntime
{
    private readonly AuthenticationCoordinator _coordinator;
    private readonly ClientCertificateAuthenticationFlowRouter _router;
    private readonly ClientCertificatePinSetupService _setupService;
    private readonly ManualAuthenticationGate _launchGate;
    private int _disposed;

    internal WindowsClientCertificateAuthenticationRuntime(
        AuthenticationCoordinator coordinator,
        ClientCertificateAuthenticationFlowRouter router,
        ClientCertificatePinSetupService setupService,
        ManualAuthenticationGate manualGate,
        INativePromptBrokerPrerequisites brokerPrerequisites,
        IClientCertificatePinStore? pinStore = null)
    {
        _coordinator = coordinator;
        _router = router;
        _setupService = setupService;
        BrokerPrerequisites = brokerPrerequisites;
        PinStore = pinStore;
        _launchGate = manualGate;
        _coordinator.StateChanged += ForwardStateChanged;
        _router.StateChanged += ForwardStateChanged;
        _setupService.StateChanged += ForwardStateChanged;
    }

    public event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged;

    public AuthenticationState CurrentState => _coordinator.CurrentState;

    public IBrowserClientCertificateRequestHandler Handler => _router;

    public IBrowserClientCertificateAuthenticationReporter Reporter => _router;

    public ILauncherAuthenticationGate LaunchGate => _launchGate;
    public IManualAuthenticationControl ManualControl => _launchGate;

    public IClientCertificatePinSetupService PinSetupService => _setupService;
    public IClientCertificatePinStore? PinStore { get; }

    public INativePromptBrokerPrerequisites BrokerPrerequisites { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _coordinator.StateChanged -= ForwardStateChanged;
        _router.StateChanged -= ForwardStateChanged;
        _setupService.StateChanged -= ForwardStateChanged;
        await _router.DisposeAsync().ConfigureAwait(false);
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private void ForwardStateChanged(object? sender, AuthenticationStateChangedEventArgs eventArgs) =>
        StateChanged?.Invoke(this, eventArgs);
}

internal sealed class AuthenticationLauncherGate(AuthenticationCoordinator coordinator)
    : ILauncherAuthenticationGate, ILauncherAuthenticationMetadataInvalidationGate
{
    public Task InvalidateDeviceMetadataAsync(CancellationToken cancellationToken = default) =>
        coordinator.InvalidateDeviceMetadataAsync(cancellationToken);

    public async Task<bool> HandleDevicePresenceAsync(
        bool isTargetDevicePresent,
        CancellationToken cancellationToken = default)
    {
        await coordinator.HandleDevicePresenceAsync(isTargetDevicePresent, cancellationToken)
            .ConfigureAwait(false);
        return AllowsNavigation(isTargetDevicePresent, coordinator.CurrentState.Stage);
    }

    internal static bool AllowsNavigation(
        bool isTargetDevicePresent,
        AuthenticationStage stage) =>
        !isTargetDevicePresent || stage is
            AuthenticationStage.AwaitingCertificate or
            AuthenticationStage.Authenticating or
            AuthenticationStage.Ready;
}
