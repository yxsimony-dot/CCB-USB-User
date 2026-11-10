using System.Diagnostics;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Core.Authentication;

public sealed class AuthenticationCoordinator :
    IAsyncDisposable,
    IBrowserClientCertificateRequestHandler,
    IBrowserClientCertificateAuthenticationReporter,
    IClientCertificatePinSetupLifecycleObserver,
    IClientCertificateBusinessControl
{
    private readonly ClientCertificateAuthenticationOptions _authentication;
    private readonly IClientCertificatePinStore _pinStore;
    private readonly IClientCertificateValidationStampStore _stampStore;
    private readonly IClientCertificateTokenUnlocker _unlocker;
    private readonly IClientCertificateNativePromptContinuation? _nativePromptContinuation;
    private readonly IPsbcAuthenticationRuntimeContext? _psbcRuntimeContext;
    private readonly IPsbcAuthenticationObserver? _psbcAuthenticationObserver;
    private readonly EventHandler? _psbcRuntimeContextChangedHandler;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly object _notificationSync = new();
    private readonly Queue<AuthenticationState> _pendingStateChanges = new();
    private IClientCertificateTokenUnlockSession? _unlockSession;
    private bool _browserNativePrepared;
    private bool _psbcManualPinAttempt;
    private bool _businessSuspended;
    private bool HasPreparedAuthentication => _browserNativePrepared || _unlockSession is not null;
    private ClientCertificatePin? _continuationPin;
    private IClientCertificateNativePromptAttempt? _nativePromptAttempt;
    private AuthenticationAttempt? _activeAttempt;
    private ClientCertificateSelectionAuthorization? _selectionAuthorization;
    private CancellationTokenSource? _psbcArmCancellation;
    private PsbcAuthenticationBinding? _psbcPreparedBinding;
    private PsbcAuthenticationBinding? _psbcAttemptBinding;
    private NavigationCycleBinding? _navigationBinding;
    private bool _devicePresent;
    private bool _attempted;
    private bool _psbcAttemptConsumed;
    private bool _pinReuseBlocked;
    private bool _validationStampPending;
    private bool _disposed;
    private int _psbcCleanupQueued;
    private long _nextCycleId;
    private long _nextCertificateRequestId;
    private long _psbcCycleStartedTimestamp;
    private UsbKeyVendorFamily? _psbcCycleVendorFamily;

    public AuthenticationCoordinator(
        ClientCertificateAuthenticationOptions authentication,
        IClientCertificatePinStore pinStore,
        IClientCertificateValidationStampStore stampStore,
        IClientCertificateTokenUnlocker unlocker,
        IClientCertificateNativePromptContinuation? nativePromptContinuation = null,
        IPsbcAuthenticationRuntimeContext? psbcRuntimeContext = null,
        IPsbcAuthenticationObserver? psbcAuthenticationObserver = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _pinStore = pinStore ?? throw new ArgumentNullException(nameof(pinStore));
        _stampStore = stampStore ?? throw new ArgumentNullException(nameof(stampStore));
        _unlocker = unlocker ?? throw new ArgumentNullException(nameof(unlocker));
        _nativePromptContinuation = nativePromptContinuation;
        _psbcRuntimeContext = psbcRuntimeContext;
        _psbcAuthenticationObserver = psbcAuthenticationObserver;
        if (_psbcRuntimeContext is not null)
        {
            _psbcRuntimeContextChangedHandler = OnPsbcRuntimeContextChanged;
            _psbcRuntimeContext.Changed += _psbcRuntimeContextChangedHandler;
        }
    }

    public event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged;

    public async Task SetBusinessSuspendedAsync(bool suspended, CancellationToken cancellationToken = default)
    {
        if (!_authentication.UsesBrowserNativeAuthentication) return;
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _businessSuspended = suspended;
            if (suspended)
            {
                _navigationBinding = null;
                if (_activeAttempt is not null)
                {
                    await EndCycleWithFailureAsync(AuthenticationErrorCategory.Cancelled, null).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task InvalidateDeviceMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_devicePresent)
            {
                return;
            }

            // Metadata loss is not a physical removal. Preserve this insertion's attempt budget.
            _activeAttempt = null;
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            SetState(new(
                AuthenticationStage.Failed,
                CurrentState.InsertionCycleId,
                AuthenticationErrorCategory.DeviceUnavailable,
                Message: "Device information is unavailable. Remove and reinsert the USB Key before trying again."));
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public AuthenticationState CurrentState { get; private set; } = new(
        AuthenticationStage.WaitingForDevice,
        null,
        Message: "Waiting for the configured security device.");

    public async Task ReleaseAfterBrowserFailureAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // A failed navigation is not a removal. Keep presence and the attempt
            // budget, and do not overwrite a more specific authentication failure.
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            _activeAttempt = null;
            if (_devicePresent && CurrentState.Stage is not (
                AuthenticationStage.Failed or AuthenticationStage.Incompatible or
                AuthenticationStage.RequiresPinSetup))
                SetState(new(
                    AuthenticationStage.Failed,
                    CurrentState.InsertionCycleId,
                    AuthenticationErrorCategory.UnknownFailure,
                    Message: "银行页面加载未完成，本轮认证已停止。"));
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task HandleDevicePresenceAsync(
        bool isTargetDevicePresent,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!isTargetDevicePresent)
            {
                if (!_devicePresent)
                {
                    return;
                }

                _devicePresent = false;
                _attempted = false;
                _psbcAttemptConsumed = false;
                _activeAttempt = null;
                await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                SetState(new(
                    AuthenticationStage.WaitingForDevice,
                    CurrentState.InsertionCycleId,
                    AuthenticationErrorCategory.DeviceUnavailable,
                    Message: GetFailureMessage(AuthenticationErrorCategory.DeviceUnavailable)));
                return;
            }

            if (_devicePresent)
            {
                return;
            }

            _devicePresent = true;
            _attempted = false;
            var cycleId = ++_nextCycleId;
            await StartCycleAsync(cycleId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
        ClientCertificateRequestSnapshot request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_businessSuspended || request.BrowserOperationId is not long browserOperationId ||
                _navigationBinding is not { } navigationBinding ||
                navigationBinding.BrowserOperationId != browserOperationId ||
                CurrentState.InsertionCycleId != navigationBinding.CycleId ||
                (_psbcRuntimeContext is not null && _psbcAttemptConsumed))
            {
                return ClientCertificateRequestMatchResult.InvalidRequest();
            }

            var result = _psbcRuntimeContext is null
                ? ClientCertificateRequestMatcher.Match(
                    request,
                    _authentication,
                    HasPreparedAuthentication)
                : MatchPsbcCertificateRequest(request);

            if (result.Status == ClientCertificateRequestMatchStatus.BrowserSelectionRequired)
            {
                return result;
            }

            if (_psbcRuntimeContext is not null)
            {
                _psbcAttemptConsumed = true;
                if (result.Status is
                    ClientCertificateRequestMatchStatus.CertificateMissing or
                    ClientCertificateRequestMatchStatus.CertificateConflict)
                {
                    await EndCycleWithFailureAsync(
                            MapRequestFailure(result.Status),
                            null)
                        .ConfigureAwait(false);
                    return result;
                }
            }

            if (result.Status == ClientCertificateRequestMatchStatus.Selected ||
                result.Status == ClientCertificateRequestMatchStatus.CertificateMissing ||
                result.Status == ClientCertificateRequestMatchStatus.CertificateConflict)
            {
                var useManualPin = result.Status == ClientCertificateRequestMatchStatus.Selected &&
                    CanSelectPsbcCertificateWithManualPin;
                if (CurrentState.Stage != AuthenticationStage.AwaitingCertificate && !useManualPin)
                {
                    return ClientCertificateRequestMatchResult.TokenNotUnlocked();
                }

                _psbcManualPinAttempt = useManualPin;
                _activeAttempt = new(
                    CurrentState.InsertionCycleId!.Value,
                    ++_nextCertificateRequestId,
                    browserOperationId);
                _psbcAttemptBinding = _psbcPreparedBinding;
                SetState(new(
                    AuthenticationStage.Authenticating,
                    CurrentState.InsertionCycleId,
                    Message: "Completing client-certificate authentication.",
                    Attempt: _activeAttempt));
                return result with { Attempt = _activeAttempt };
            }

            if (_devicePresent && HasPreparedAuthentication)
            {
                await EndCycleWithFailureAsync(MapRequestFailure(result.Status), null).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public ValueTask<ClientCertificateRequestMatchResult> HandleCertificateRequestAsync(
        ClientCertificateRequestSnapshot request,
        CancellationToken cancellationToken = default) =>
        HandleAsync(request, cancellationToken);

    public async Task ReportTlsCompletedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            if (_validationStampPending)
            {
                try
                {
                    await _stampStore.SaveAsync(
                            new(_authentication.Id, _authentication.GetNormalizedHash()),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                    _activeAttempt = null;
                    throw;
                }
                catch
                {
                    await EndCycleWithFailureAsync(
                            AuthenticationErrorCategory.UnknownFailure,
                            null)
                        .ConfigureAwait(false);
                    return;
                }
            }

            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            _activeAttempt = null;
            SetState(new(
                AuthenticationStage.Ready,
                CurrentState.InsertionCycleId,
                Message: "Client-certificate authentication is ready."));
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task ReportNavigationStartedAsync(
        long browserOperationId,
        CancellationToken cancellationToken = default)
    {
        if (browserOperationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(browserOperationId));
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var canSelectWithManualPin = CanSelectPsbcCertificateWithManualPin;
            if (_businessSuspended || !_devicePresent ||
                (!HasPreparedAuthentication && !canSelectWithManualPin) ||
                (_psbcRuntimeContext is not null && _psbcAttemptConsumed) ||
                (CurrentState.Stage != AuthenticationStage.AwaitingCertificate &&
                 !(_browserNativePrepared && CurrentState.Stage == AuthenticationStage.Ready) &&
                 !canSelectWithManualPin) ||
                CurrentState.InsertionCycleId is not long cycleId)
            {
                return;
            }

            _navigationBinding = new(browserOperationId, cycleId);
            if (_browserNativePrepared && CurrentState.Stage == AuthenticationStage.Ready)
            {
                SetState(new(AuthenticationStage.AwaitingCertificate, cycleId,
                    Message: "正在等待银行页面的证书请求。"));
            }
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        try
        {
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_disposed && IsCurrentAttempt(attempt))
                {
                    await EndCycleWithFailureAsync(AuthenticationErrorCategory.Cancelled, null)
                        .ConfigureAwait(false);
                }

                return null;
            }
            finally
            {
                _transitionGate.Release();
                PublishPendingStateChanges();
            }
        }

        try
        {
            ThrowIfDisposed();
            // The request handler is the only state transition into Authenticating.
            // This acknowledgement is deliberately attempt-scoped to ignore stale browser callbacks.
            if (!IsCurrentAttempt(attempt))
            {
                return null;
            }

            if (Volatile.Read(ref _selectionAuthorization) is { } existingAuthorization)
            {
                return existingAuthorization;
            }

            // Certificate authorization does not require an automatically supplied PIN.
            if (_psbcManualPinAttempt ||
                _authentication.NativePromptContinuation is not { Enabled: true })
            {
                var authorization = new ClientCertificateSelectionAuthorization();
                Interlocked.Exchange(ref _selectionAuthorization, authorization);
                if (!IsCurrentAttempt(attempt))
                {
                    authorization.Revoke();
                    Interlocked.CompareExchange(
                        ref _selectionAuthorization,
                        null,
                        authorization);
                    return null;
                }

                return authorization;
            }

            var continuationPin = Volatile.Read(ref _continuationPin);
            if (_nativePromptContinuation is null || continuationPin is null)
            {
                await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null)
                    .ConfigureAwait(false);
                return null;
            }

            CancellationTokenSource? armCancellation = null;
            var armToken = cancellationToken;
            if (_psbcRuntimeContext is not null)
            {
                if (!IsCurrentPsbcAttempt())
                {
                    await EndCycleWithFailureAsync(
                            GetPsbcContextFailureCategory(),
                            null)
                        .ConfigureAwait(false);
                    return null;
                }

                armCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                var previousCancellation = Interlocked.Exchange(
                    ref _psbcArmCancellation,
                    armCancellation);
                Cancel(previousCancellation);
                previousCancellation?.Dispose();
                armToken = armCancellation.Token;
                if (!IsCurrentPsbcAttempt())
                {
                    Cancel(armCancellation);
                }
            }

            ClientCertificateNativePromptArmResult armResult;
            try
            {
                armResult = await _nativePromptContinuation
                    .ArmAsync(
                        _authentication,
                        attempt,
                        continuationPin,
                        armToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var category = cancellationToken.IsCancellationRequested
                    ? AuthenticationErrorCategory.Cancelled
                    : _psbcRuntimeContext is not null
                        ? GetPsbcContextFailureCategory()
                        : AuthenticationErrorCategory.Cancelled;
                await EndCycleWithFailureAsync(category, null)
                    .ConfigureAwait(false);
                return null;
            }
            catch
            {
                await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null)
                    .ConfigureAwait(false);
                return null;
            }
            finally
            {
                DisposeContinuationPin();
                if (armCancellation is not null)
                {
                    Interlocked.CompareExchange(
                        ref _psbcArmCancellation,
                        null,
                        armCancellation);
                    armCancellation.Dispose();
                }
            }

            if (!armResult.IsArmed)
            {
                if (armResult.Attempt is not null)
                {
                    await DisposeNativePromptAttemptAsync(armResult.Attempt).ConfigureAwait(false);
                }

                await EndCycleWithFailureAsync(
                        MapNativePromptArmFailure(armResult.Status),
                        armResult.PublicErrorCode)
                    .ConfigureAwait(false);
                return null;
            }

            if (IsDisposed())
            {
                await DisposeNativePromptAttemptAsync(armResult.Attempt!).ConfigureAwait(false);
                await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                _activeAttempt = null;
                return null;
            }

            if (!IsCurrentAttempt(attempt))
            {
                await DisposeNativePromptAttemptAsync(armResult.Attempt!).ConfigureAwait(false);
                if (_psbcRuntimeContext is not null)
                {
                    await EndCycleWithFailureAsync(
                            GetPsbcContextFailureCategory(),
                            null)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                    _activeAttempt = null;
                }

                return null;
            }

            Interlocked.Exchange(ref _nativePromptAttempt, armResult.Attempt);
            var selectionAuthorization =
                new ClientCertificateSelectionAuthorization();
            Interlocked.Exchange(
                ref _selectionAuthorization,
                selectionAuthorization);
            if (!IsCurrentAttempt(attempt))
            {
                selectionAuthorization.Revoke();
                if (_psbcRuntimeContext is not null)
                {
                    await EndCycleWithFailureAsync(
                            GetPsbcContextFailureCategory(),
                            null)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                    _activeAttempt = null;
                }

                return null;
            }

            _ = ObserveNativePromptCompletionAsync(attempt, armResult.Attempt!);
            return selectionAuthorization;
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task ReportTlsFailureAsync(
        AuthenticationAttempt attempt,
        int? publicErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            await EndCycleWithFailureAsync(AuthenticationErrorCategory.TlsFailed, publicErrorCode)
                .ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task ReportTimeoutAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            await EndCycleWithFailureAsync(AuthenticationErrorCategory.TimedOut, null)
                .ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task ReportNavigationCompletedWithoutCertificateAsync(
        long browserOperationId,
        bool isSuccess,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_devicePresent || !HasPreparedAuthentication ||
                CurrentState.Stage != AuthenticationStage.AwaitingCertificate)
            {
                return;
            }

            if (_navigationBinding is not { } binding ||
                binding.BrowserOperationId != browserOperationId ||
                CurrentState.InsertionCycleId != binding.CycleId)
            {
                return;
            }

            if (isSuccess)
            {
                _navigationBinding = null;
                if (_browserNativePrepared)
                {
                    // A page can load before its subsequent certificate-login navigation.
                    // Allow page login while retaining the one prepared PIN for that request.
                    SetState(new(AuthenticationStage.Ready, CurrentState.InsertionCycleId,
                        Message: "银行页面已打开，等待页面登录。"));
                }
                return;
            }

            await EndCycleWithFailureAsync(AuthenticationErrorCategory.TlsFailed, null)
                .ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public async Task BeginCompatibilityValidationAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_devicePresent || _pinReuseBlocked || HasPreparedAuthentication ||
                _authentication.UsesBrowserNativeAuthentication)
            {
                return;
            }

            SetState(new(
                AuthenticationStage.ValidatingCompatibility,
                CurrentState.InsertionCycleId,
                Message: "Validating token and browser compatibility."));
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public Task ReportPinSetupSucceededAsync(CancellationToken cancellationToken = default) =>
        ReportPinSetupChangedAsync(wasDeleted: false, cancellationToken);

    public async Task ReportPinProvisionedAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _pinReuseBlocked = false;
            var psbcAttemptConsumed =
                _psbcRuntimeContext is not null && _psbcAttemptConsumed;
            _attempted = psbcAttemptConsumed;
            _activeAttempt = null;
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            if (_devicePresent)
            {
                if (psbcAttemptConsumed)
                {
                    SetState(new(
                        AuthenticationStage.Ready,
                        CurrentState.InsertionCycleId,
                        Message: "PIN saved. Restart the business session or reinsert the USB Key to authenticate again."));
                }
                else
                {
                    await StartCycleAsync(++_nextCycleId, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                SetState(new(
                    AuthenticationStage.WaitingForDevice,
                    CurrentState.InsertionCycleId,
                    Message: "Fixed local credential saved. Waiting for the configured security device."));
            }
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    public Task ReportPinSetupDeletedAsync(CancellationToken cancellationToken = default) =>
        ReportPinSetupChangedAsync(wasDeleted: true, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_psbcRuntimeContext is not null &&
            _psbcRuntimeContextChangedHandler is not null)
        {
            _psbcRuntimeContext.Changed -=
                _psbcRuntimeContextChangedHandler;
        }
        RevokePsbcAuthenticationResourcesImmediately();

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task StartCycleAsync(long cycleId, CancellationToken cancellationToken)
    {
        if (_pinReuseBlocked)
        {
            BeginPsbcObservationCycle();
            SetState(new(
                AuthenticationStage.RequiresPinSetup,
                cycleId,
                AuthenticationErrorCategory.PinRejected,
                Message: "The stored PIN was rejected and must be set up again."));
            return;
        }

        if (_attempted)
        {
            return;
        }

        _attempted = true;
        BeginPsbcObservationCycle();
        if (_authentication.UsesBrowserNativeAuthentication)
        {
            await PrepareBrowserNativeAuthenticationAsync(cycleId, cancellationToken).ConfigureAwait(false);
            return;
        }

        ClientCertificateValidationStamp? stamp;
        try
        {
            stamp = await _stampStore.ReadAsync(_authentication.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null).ConfigureAwait(false);
            return;
        }

        var hasMatchingStamp = HasMatchingStamp(stamp);
        var hasPendingStamp = HasPendingStamp(stamp);
        if (!hasMatchingStamp && !hasPendingStamp)
        {
            SetState(new(
                AuthenticationStage.RequiresPinSetup,
                cycleId,
                stamp is null
                    ? AuthenticationErrorCategory.CredentialsUnavailable
                    : AuthenticationErrorCategory.ValidationStampInvalid,
                Message: "A fixed local credential must be configured before automatic authentication."));
            return;
        }

        ClientCertificatePin? pin;
        try
        {
            pin = await _pinStore
                .ReadAsync(_authentication.CredentialTargetName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null).ConfigureAwait(false);
            return;
        }

        if (pin is null)
        {
            SetState(new(
                AuthenticationStage.RequiresPinSetup,
                cycleId,
                AuthenticationErrorCategory.CredentialsUnavailable,
                Message: "A validated PIN setup is required before automatic authentication."));
            return;
        }

        _validationStampPending = hasPendingStamp;
        if (_validationStampPending)
        {
            SetState(new(
                AuthenticationStage.ValidatingCompatibility,
                cycleId,
                stamp is null
                    ? AuthenticationErrorCategory.CredentialsUnavailable
                    : AuthenticationErrorCategory.ValidationStampInvalid,
                Message: "Validating the fixed local credential for this authentication configuration."));
        }

        SetState(new(
            AuthenticationStage.UnlockingToken,
            cycleId,
            Message: "Unlocking the security token."));

        using (pin)
        {
            ClientCertificateTokenUnlockResult result;
            try
            {
                result = await _unlocker.UnlockAsync(_authentication, pin, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null).ConfigureAwait(false);
                return;
            }

            if (result.Status == ClientCertificateTokenUnlockStatus.Success && result.Session is not null)
            {
                if (_authentication.NativePromptContinuation is { Enabled: true })
                {
                    DisposeContinuationPin();
                    _continuationPin = ClientCertificatePin.CopyOf(pin.AsMemory());
                }

                _unlockSession = result.Session;
                SetState(new(
                    AuthenticationStage.AwaitingCertificate,
                    cycleId,
                    Message: "Waiting for a client-certificate request."));
                return;
            }

            if (result.Status == ClientCertificateTokenUnlockStatus.PinRejected)
            {
                _pinReuseBlocked = true;
                await ClearRejectedPinAsync().ConfigureAwait(false);
                SetState(new(
                    AuthenticationStage.RequiresPinSetup,
                    cycleId,
                    AuthenticationErrorCategory.PinRejected,
                    result.ProviderErrorCode,
                    "The stored PIN was rejected and must be set up again."));
                return;
            }

            var category = MapUnlockFailure(result.Status);
            if (category is AuthenticationErrorCategory.ProviderUnavailable or AuthenticationErrorCategory.Unsupported)
            {
                await EndCycleWithIncompatibilityAsync(category, result.ProviderErrorCode).ConfigureAwait(false);
                return;
            }

            await EndCycleWithFailureAsync(category, result.ProviderErrorCode).ConfigureAwait(false);
        }
    }

    private async Task PrepareBrowserNativeAuthenticationAsync(long cycleId, CancellationToken cancellationToken)
    {
        if (_authentication.NativePromptContinuation is not { Enabled: true } ||
            _nativePromptContinuation is null)
        {
            await EndCycleWithIncompatibilityAsync(AuthenticationErrorCategory.Unsupported, null).ConfigureAwait(false);
            return;
        }

        if (_psbcRuntimeContext is not null)
        {
            if (!TryCreatePsbcBinding(
                    _psbcRuntimeContext.Current,
                    out _psbcPreparedBinding))
            {
                await EndCycleWithFailureAsync(
                        GetPsbcContextFailureCategory(),
                        null)
                    .ConfigureAwait(false);
                return;
            }
        }

        try
        {
            // Browser-native stores intentionally have no local-unlock validation stamp.
            // Authorization is bound to the certificate request and trusted prompt instead.
            using var pin = await _pinStore.ReadAsync(_authentication.CredentialTargetName, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_psbcRuntimeContext is not null &&
                !IsCurrentPsbcBinding(_psbcPreparedBinding))
            {
                await EndCycleWithFailureAsync(
                        GetPsbcContextFailureCategory(),
                        null)
                    .ConfigureAwait(false);
                return;
            }

            if (pin is null || pin.AsMemory().IsEmpty)
            {
                SetState(new(AuthenticationStage.RequiresPinSetup, cycleId,
                    AuthenticationErrorCategory.CredentialsUnavailable,
                    Message: "未设置邮储 PIN；将自动选择匹配证书，密码请在安全窗口中手动输入。"));
                return;
            }

            _validationStampPending = false;
            Interlocked.Exchange(
                ref _continuationPin,
                ClientCertificatePin.CopyOf(pin.AsMemory()))?.Dispose();
            _browserNativePrepared = true;
            SetState(new(AuthenticationStage.AwaitingCertificate, cycleId,
                Message: "已读取统一 PIN，正在打开银行登录页面。"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null).ConfigureAwait(false);
        }
    }

    private async Task ReportPinSetupChangedAsync(
        bool wasDeleted,
        CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _pinReuseBlocked = false;
            _attempted = _devicePresent;
            _activeAttempt = null;
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            SetState(_devicePresent
                ? new(
                    wasDeleted ? AuthenticationStage.RequiresPinSetup : AuthenticationStage.Ready,
                    CurrentState.InsertionCycleId,
                    Message: wasDeleted
                        ? "The local PIN was deleted and must be set up again."
                        : "The local PIN setup was validated.")
                : new(
                    AuthenticationStage.WaitingForDevice,
                    CurrentState.InsertionCycleId,
                    Message: "Waiting for the configured security device."));
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    private bool HasMatchingStamp(ClientCertificateValidationStamp? stamp) =>
        stamp is not null &&
        string.Equals(stamp.AuthenticationId, _authentication.Id, StringComparison.Ordinal) &&
        string.Equals(stamp.AuthenticationHash, _authentication.GetNormalizedHash(), StringComparison.Ordinal);

    private bool HasPendingStamp(ClientCertificateValidationStamp? stamp) =>
        stamp?.IsPendingFor(_authentication.Id, _authentication.GetNormalizedHash()) == true;

    private async Task ClearRejectedPinAsync()
    {
        try
        {
            await _pinStore.DeleteAsync(_authentication.CredentialTargetName, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _stampStore.DeleteAsync(_authentication.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async ValueTask EndCycleWithFailureAsync(AuthenticationErrorCategory category, int? publicErrorCode)
    {
        await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
        _activeAttempt = null;
        SetState(new(
            AuthenticationStage.Failed,
            CurrentState.InsertionCycleId,
            category,
            publicErrorCode,
            GetFailureMessage(category)));
    }

    private async ValueTask EndCycleWithIncompatibilityAsync(AuthenticationErrorCategory category, int? publicErrorCode)
    {
        await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
        _activeAttempt = null;
        SetState(new(
            AuthenticationStage.Incompatible,
            CurrentState.InsertionCycleId,
            category,
            publicErrorCode,
            GetFailureMessage(category)));
    }

    private async ValueTask ReleaseAuthenticationResourcesAsync()
    {
        _browserNativePrepared = false;
        _psbcManualPinAttempt = false;
        _validationStampPending = false;
        _navigationBinding = null;
        _psbcPreparedBinding = null;
        _psbcAttemptBinding = null;
        var armCancellation = Interlocked.Exchange(
            ref _psbcArmCancellation,
            null);
        Cancel(armCancellation);
        armCancellation?.Dispose();
        var selectionAuthorization = Interlocked.Exchange(
            ref _selectionAuthorization,
            null);
        selectionAuthorization?.Revoke();
        DisposeContinuationPin();
        var nativePromptAttempt = Interlocked.Exchange(
            ref _nativePromptAttempt,
            null);
        var session = _unlockSession;
        _unlockSession = null;
        try
        {
            if (nativePromptAttempt is not null)
            {
                await DisposeNativePromptAttemptAsync(nativePromptAttempt).ConfigureAwait(false);
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private void DisposeContinuationPin()
    {
        var pin = Interlocked.Exchange(ref _continuationPin, null);
        pin?.Dispose();
    }

    private static async ValueTask DisposeNativePromptAttemptAsync(
        IClientCertificateNativePromptAttempt attempt)
    {
        try
        {
            await attempt.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ObserveNativePromptCompletionAsync(
        AuthenticationAttempt authenticationAttempt,
        IClientCertificateNativePromptAttempt nativePromptAttempt)
    {
        ClientCertificateNativePromptResult result;
        try
        {
            result = await nativePromptAttempt.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new(ClientCertificateNativePromptStatus.Cancelled);
        }
        catch
        {
            result = new(ClientCertificateNativePromptStatus.UnknownFailure);
        }

        if (result.IsSuccess)
        {
            return;
        }

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed ||
                !ReferenceEquals(_nativePromptAttempt, nativePromptAttempt) ||
                !IsCurrentAttempt(authenticationAttempt))
            {
                return;
            }

            if (result.Status == ClientCertificateNativePromptStatus.PinRejected)
            {
                _pinReuseBlocked = true;
                await ClearRejectedPinAsync().ConfigureAwait(false);
                await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                _activeAttempt = null;
                SetState(new(
                    AuthenticationStage.RequiresPinSetup,
                    CurrentState.InsertionCycleId,
                    AuthenticationErrorCategory.PinRejected,
                    result.PublicErrorCode,
                    "The stored PIN was rejected and must be set up again."));
                return;
            }

            await EndCycleWithFailureAsync(
                    MapNativePromptFailure(result.Status),
                    result.PublicErrorCode)
                .ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
            PublishPendingStateChanges();
        }
    }

    private ClientCertificateRequestMatchResult MatchPsbcCertificateRequest(
        ClientCertificateRequestSnapshot request)
    {
        var snapshot = _psbcRuntimeContext!.Current;
        var selection = PsbcClientCertificateSelector.Select(
            request,
            _authentication,
            new(
                snapshot.IsPostalSavingsBank,
                snapshot.OwnerSessionId,
                snapshot.OwnerRevision,
                snapshot.CurrentSessionId,
                snapshot.CurrentRevision,
                snapshot.ReadyKey,
                DateTimeOffset.UtcNow,
                snapshot.IsSessionCurrent,
                snapshot.OwnsCurrentRuntime));

        if (selection.Status == PsbcClientCertificateSelectionStatus.Selected)
        {
            if ((!HasPreparedAuthentication && !CanSelectPsbcCertificateWithManualPin) ||
                !IsCurrentPsbcBinding(_psbcPreparedBinding))
            {
                return ClientCertificateRequestMatchResult.TokenNotUnlocked();
            }
        }

        return selection.Status switch
        {
            PsbcClientCertificateSelectionStatus.Selected =>
                ClientCertificateRequestMatchResult.Selected(
                    selection.SelectedCertificate!),
            PsbcClientCertificateSelectionStatus.InvalidRequest =>
                ClientCertificateRequestMatchResult.InvalidRequest(),
            PsbcClientCertificateSelectionStatus.CertificateMissing =>
                ClientCertificateRequestMatchResult.CertificateMissing(),
            PsbcClientCertificateSelectionStatus.CertificateConflict =>
                ClientCertificateRequestMatchResult.CertificateConflict(),
            PsbcClientCertificateSelectionStatus.KeyNotReady =>
                ClientCertificateRequestMatchResult.TokenNotUnlocked(),
            _ => ClientCertificateRequestMatchResult.UnauthorizedTarget()
        };
    }

    private void OnPsbcRuntimeContextChanged(
        object? sender,
        EventArgs eventArgs)
    {
        if (!ReferenceEquals(sender, _psbcRuntimeContext) || IsDisposed())
        {
            return;
        }

        var preparedBinding = Volatile.Read(ref _psbcPreparedBinding);
        var attemptBinding = Volatile.Read(ref _psbcAttemptBinding);
        var hadResources =
            preparedBinding is not null ||
            attemptBinding is not null ||
            Volatile.Read(ref _selectionAuthorization) is not null ||
            Volatile.Read(ref _continuationPin) is not null ||
            Volatile.Read(ref _psbcArmCancellation) is not null ||
            Volatile.Read(ref _nativePromptAttempt) is not null;
        var failureCategory = GetPsbcContextFailureCategory();
        RevokePsbcAuthenticationResourcesImmediately();
        if (!hadResources ||
            Interlocked.Exchange(ref _psbcCleanupQueued, 1) != 0)
        {
            return;
        }

        _ = CleanupInvalidPsbcContextAsync(
            preparedBinding,
            attemptBinding,
            failureCategory);
    }

    private void RevokePsbcAuthenticationResourcesImmediately()
    {
        var authorization = Interlocked.Exchange(
            ref _selectionAuthorization,
            null);
        authorization?.Revoke();
        DisposeContinuationPin();
        var armCancellation = Interlocked.Exchange(
            ref _psbcArmCancellation,
            null);
        Cancel(armCancellation);
        var nativePromptAttempt = Interlocked.Exchange(
            ref _nativePromptAttempt,
            null);
        if (nativePromptAttempt is not null)
        {
            _ = DisposeNativePromptAttemptAsync(nativePromptAttempt).AsTask();
        }
    }

    private async Task CleanupInvalidPsbcContextAsync(
        PsbcAuthenticationBinding? preparedBinding,
        PsbcAuthenticationBinding? attemptBinding,
        AuthenticationErrorCategory failureCategory)
    {
        try
        {
            await _transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                var ownsInvalidatedCycle =
                    preparedBinding is not null &&
                    ReferenceEquals(_psbcPreparedBinding, preparedBinding) ||
                    attemptBinding is not null &&
                    ReferenceEquals(_psbcAttemptBinding, attemptBinding);
                if (!ownsInvalidatedCycle)
                {
                    return;
                }

                await EndCycleWithFailureAsync(
                        failureCategory,
                        null)
                    .ConfigureAwait(false);
            }
            finally
            {
                _transitionGate.Release();
                PublishPendingStateChanges();
            }
        }
        catch
        {
            // Context invalidation already revoked all sensitive resources.
        }
        finally
        {
            Interlocked.Exchange(ref _psbcCleanupQueued, 0);
        }
    }

    private bool IsCurrentPsbcAttempt() =>
        IsCurrentPsbcBinding(_psbcAttemptBinding);

    private AuthenticationErrorCategory GetPsbcContextFailureCategory()
    {
        if (_psbcRuntimeContext is null)
        {
            return AuthenticationErrorCategory.Cancelled;
        }

        var snapshot = _psbcRuntimeContext.Current;
        if (!snapshot.IsPostalSavingsBank)
        {
            return AuthenticationErrorCategory.BankChanged;
        }

        if (!snapshot.IsSessionCurrent ||
            !snapshot.OwnsCurrentRuntime ||
            snapshot.OwnerSessionId <= 0 ||
            snapshot.OwnerRevision <= 0 ||
            snapshot.OwnerSessionId != snapshot.CurrentSessionId ||
            snapshot.OwnerRevision != snapshot.CurrentRevision)
        {
            return AuthenticationErrorCategory.Cancelled;
        }

        return AuthenticationErrorCategory.DeviceUnavailable;
    }

    private bool CanSelectPsbcCertificateWithManualPin =>
        _psbcRuntimeContext is not null &&
        _authentication.UsesBrowserNativeAuthentication &&
        _devicePresent &&
        !_pinReuseBlocked &&
        CurrentState.Stage == AuthenticationStage.RequiresPinSetup &&
        IsCurrentPsbcBinding(_psbcPreparedBinding);

    private bool IsCurrentPsbcBinding(
        PsbcAuthenticationBinding? binding)
    {
        if (_psbcRuntimeContext is null)
        {
            return true;
        }

        if (binding is null)
        {
            return false;
        }

        var snapshot = _psbcRuntimeContext.Current;
        return snapshot.IsValid &&
            snapshot.ContextRevision == binding.ContextRevision &&
            snapshot.OwnerSessionId == binding.SessionId &&
            snapshot.OwnerRevision == binding.Revision &&
            snapshot.ReadyKey is { } readyKey &&
            readyKey.VendorFamily == binding.VendorFamily &&
            string.Equals(
                readyKey.PhysicalInstanceId,
                binding.PhysicalInstanceId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                readyKey.TokenIdentity,
                binding.TokenIdentity,
                StringComparison.Ordinal);
    }

    private static bool TryCreatePsbcBinding(
        PsbcAuthenticationRuntimeSnapshot snapshot,
        out PsbcAuthenticationBinding? binding)
    {
        if (!snapshot.IsValid || snapshot.ReadyKey is not { } readyKey)
        {
            binding = null;
            return false;
        }

        binding = new(
            snapshot.ContextRevision,
            snapshot.OwnerSessionId,
            snapshot.OwnerRevision,
            readyKey.VendorFamily!.Value,
            readyKey.PhysicalInstanceId!,
            readyKey.TokenIdentity!);
        return true;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record NavigationCycleBinding(long BrowserOperationId, long CycleId);

    private sealed record PsbcAuthenticationBinding(
        long ContextRevision,
        long SessionId,
        long Revision,
        UsbKeyVendorFamily VendorFamily,
        string PhysicalInstanceId,
        string TokenIdentity);

    private void SetState(AuthenticationState state)
    {
        CurrentState = state;
        RecordPsbcAuthenticationObservation(state);
        lock (_notificationSync)
        {
            _pendingStateChanges.Enqueue(state);
        }
    }

    private void BeginPsbcObservationCycle()
    {
        if (_psbcRuntimeContext is null)
        {
            return;
        }

        _psbcCycleStartedTimestamp = Stopwatch.GetTimestamp();
        _psbcCycleVendorFamily =
            _psbcRuntimeContext.Current.ReadyKey?.VendorFamily;
    }

    private void RecordPsbcAuthenticationObservation(
        AuthenticationState state)
    {
        if (_psbcRuntimeContext is null ||
            _psbcAuthenticationObserver is null ||
            _psbcCycleStartedTimestamp == 0)
        {
            return;
        }

        try
        {
            _psbcAuthenticationObserver.Record(new(
                state.Stage,
                "psbc",
                _psbcCycleVendorFamily,
                state.ErrorCategory,
                Stopwatch.GetElapsedTime(_psbcCycleStartedTimestamp)));
        }
        catch
        {
            // Observability must never change authentication behavior.
        }
    }

    private bool IsCurrentAttempt(AuthenticationAttempt attempt) =>
        _devicePresent &&
        CurrentState.Stage == AuthenticationStage.Authenticating &&
        Equals(_activeAttempt, attempt) &&
        (_psbcRuntimeContext is null || IsCurrentPsbcAttempt());

    private void PublishPendingStateChanges()
    {
        while (true)
        {
            AuthenticationState? state;
            lock (_notificationSync)
            {
                state = _pendingStateChanges.Count == 0 ? null : _pendingStateChanges.Dequeue();
            }

            if (state is null)
            {
                return;
            }

            var subscribers = StateChanged;
            if (subscribers is null)
            {
                continue;
            }

            var eventArgs = new AuthenticationStateChangedEventArgs(state);
            foreach (EventHandler<AuthenticationStateChangedEventArgs> subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    subscriber(this, eventArgs);
                }
                catch
                {
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed(), this);
    }

    private bool IsDisposed()
    {
        lock (_stateSync)
        {
            return _disposed;
        }
    }

    private static AuthenticationErrorCategory MapUnlockFailure(ClientCertificateTokenUnlockStatus status) => status switch
    {
        ClientCertificateTokenUnlockStatus.DeviceUnavailable => AuthenticationErrorCategory.DeviceUnavailable,
        ClientCertificateTokenUnlockStatus.ProviderUnavailable => AuthenticationErrorCategory.ProviderUnavailable,
        ClientCertificateTokenUnlockStatus.Unsupported => AuthenticationErrorCategory.Unsupported,
        _ => AuthenticationErrorCategory.UnknownFailure
    };

    private static AuthenticationErrorCategory MapNativePromptArmFailure(
        ClientCertificateNativePromptArmStatus status) => status switch
        {
            ClientCertificateNativePromptArmStatus.TimedOut => AuthenticationErrorCategory.TimedOut,
            ClientCertificateNativePromptArmStatus.Cancelled => AuthenticationErrorCategory.Cancelled,
            _ => AuthenticationErrorCategory.UnknownFailure
        };

    private static AuthenticationErrorCategory MapNativePromptFailure(
        ClientCertificateNativePromptStatus status) => status switch
        {
            ClientCertificateNativePromptStatus.PinRejected => AuthenticationErrorCategory.PinRejected,
            ClientCertificateNativePromptStatus.TargetMismatch => AuthenticationErrorCategory.WindowConflict,
            ClientCertificateNativePromptStatus.FocusLost => AuthenticationErrorCategory.WindowConflict,
            ClientCertificateNativePromptStatus.InputFailed => AuthenticationErrorCategory.WindowConflict,
            ClientCertificateNativePromptStatus.TimedOut => AuthenticationErrorCategory.TimedOut,
            ClientCertificateNativePromptStatus.Cancelled => AuthenticationErrorCategory.Cancelled,
            _ => AuthenticationErrorCategory.UnknownFailure
        };

    private static AuthenticationErrorCategory MapRequestFailure(ClientCertificateRequestMatchStatus status) => status switch
    {
        ClientCertificateRequestMatchStatus.UnauthorizedTarget => AuthenticationErrorCategory.UnauthorizedTarget,
        ClientCertificateRequestMatchStatus.CertificateMissing => AuthenticationErrorCategory.CertificateMissing,
        ClientCertificateRequestMatchStatus.CertificateConflict => AuthenticationErrorCategory.CertificateConflict,
        _ => AuthenticationErrorCategory.InvalidCertificateRequest
    };

    private static string GetFailureMessage(AuthenticationErrorCategory category) => category switch
    {
        AuthenticationErrorCategory.DeviceUnavailable => "The security device is unavailable.",
        AuthenticationErrorCategory.ProviderUnavailable => "The configured cryptographic provider is unavailable.",
        AuthenticationErrorCategory.Unsupported => "Automatic authentication is not supported by this token.",
        AuthenticationErrorCategory.TlsFailed => "Client-certificate authentication failed.",
        AuthenticationErrorCategory.TimedOut => "Client-certificate authentication timed out.",
        AuthenticationErrorCategory.UnauthorizedTarget => "The certificate request target is not authorized.",
        AuthenticationErrorCategory.CertificateMissing => "The configured client certificate is unavailable.",
        AuthenticationErrorCategory.CertificateConflict => "The client certificate match is ambiguous.",
        AuthenticationErrorCategory.WindowConflict => "The secure PIN window could not be uniquely authorized.",
        AuthenticationErrorCategory.Cancelled => "Client-certificate authentication was cancelled.",
        AuthenticationErrorCategory.BankChanged => "Client-certificate authentication stopped because the selected bank changed.",
        AuthenticationErrorCategory.InvalidCertificateRequest => "The client certificate request is invalid.",
        _ => "Automatic authentication could not be completed."
    };
}
