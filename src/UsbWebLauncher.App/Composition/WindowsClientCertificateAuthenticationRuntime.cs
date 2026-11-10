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
        BankAuthenticationRuntimeKind runtimeKind,
        ClientCertificateAuthenticationOptions authentication,
        ICorrelatedBrowserHost browser,
        Uri target,
        IPsbcAuthenticationRuntimeContext? psbcRuntimeContext = null)
    {
        var resolvedCredentialTarget = BankCredentialTargetResolver.Resolve(
            runtimeKind,
            authentication.Id);
        if (!string.Equals(
                resolvedCredentialTarget,
                authentication.CredentialTargetName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "认证配置的凭据目标与当前银行运行时不一致。");
        }

        var stores = WindowsClientCertificateAuthenticationStores.Create(
            authentication.Id,
            useValidationStamps: !authentication.UsesBrowserNativeAuthentication);
        if (!string.Equals(
                stores.CredentialTargetName,
                resolvedCredentialTarget,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "当前银行的凭据目标与本机存储目标不一致。");
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
            nativePromptContinuation: nativePromptContinuation,
            psbcRuntimeContext: psbcRuntimeContext,
            psbcAuthenticationObserver:
                runtimeKind == BankAuthenticationRuntimeKind.PostalSavingsBank
                    ? new PsbcAuthenticationDiagnosticsObserver()
                    : null);
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            target,
            coordinator,
            coordinator,
            nativePromptContinuation);
        var manualGate = new ManualAuthenticationGate(
            new AuthenticationLauncherGate(runtimeKind, coordinator),
            coordinator);
        var setupService = new ClientCertificatePinSetupService(
            authentication,
            stores.PinStore,
            stores.ValidationStampStore,
            tokenUnlocker,
            router,
            manualGate);
        return new WindowsClientCertificateAuthenticationRuntime(
            runtimeKind,
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
        BankAuthenticationRuntimeKind runtimeKind,
        AuthenticationCoordinator coordinator,
        ClientCertificateAuthenticationFlowRouter router,
        ClientCertificatePinSetupService setupService,
        ManualAuthenticationGate manualGate,
        INativePromptBrokerPrerequisites brokerPrerequisites,
        IClientCertificatePinStore? pinStore = null)
    {
        RuntimeKind = runtimeKind;
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

    public BankAuthenticationRuntimeKind RuntimeKind { get; }

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

internal sealed class AuthenticationLauncherGate(
    BankAuthenticationRuntimeKind runtimeKind,
    AuthenticationCoordinator coordinator)
    : ILauncherAuthenticationGate, ILauncherAuthenticationMetadataInvalidationGate,
      ILauncherAuthenticationBrowserFailureGate
{
    public Task ReleaseAfterBrowserFailureAsync(CancellationToken cancellationToken = default) =>
        runtimeKind == BankAuthenticationRuntimeKind.PostalSavingsBank
            ? coordinator.ReleaseAfterBrowserFailureAsync(cancellationToken)
            : coordinator.HandleDevicePresenceAsync(false, cancellationToken);

    public Task InvalidateDeviceMetadataAsync(CancellationToken cancellationToken = default) =>
        coordinator.InvalidateDeviceMetadataAsync(cancellationToken);

    public async Task<bool> HandleDevicePresenceAsync(
        bool isTargetDevicePresent,
        CancellationToken cancellationToken = default)
    {
        await coordinator.HandleDevicePresenceAsync(isTargetDevicePresent, cancellationToken)
            .ConfigureAwait(false);
        return AllowsNavigation(
            runtimeKind,
            isTargetDevicePresent,
            coordinator.CurrentState.Stage);
    }

    internal static bool AllowsNavigation(
        BankAuthenticationRuntimeKind runtimeKind,
        bool isTargetDevicePresent,
        AuthenticationStage stage) =>
        !isTargetDevicePresent || stage is
            AuthenticationStage.AwaitingCertificate or
            AuthenticationStage.Authenticating or
            AuthenticationStage.Ready ||
        runtimeKind == BankAuthenticationRuntimeKind.PostalSavingsBank &&
        stage == AuthenticationStage.RequiresPinSetup;
}
