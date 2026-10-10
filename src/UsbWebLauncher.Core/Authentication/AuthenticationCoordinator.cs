using UsbWebLauncher.Core.Configuration;

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
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly object _notificationSync = new();
    private readonly Queue<AuthenticationState> _pendingStateChanges = new();
    private IClientCertificateTokenUnlockSession? _unlockSession;
    private bool _browserNativePrepared;
    private bool _businessSuspended;
    private bool HasPreparedAuthentication => _browserNativePrepared || _unlockSession is not null;
    private ClientCertificatePin? _continuationPin;
    private IClientCertificateNativePromptAttempt? _nativePromptAttempt;
    private AuthenticationAttempt? _activeAttempt;
    private ClientCertificateSelectionAuthorization? _selectionAuthorization;
    private NavigationCycleBinding? _navigationBinding;
    private bool _devicePresent;
    private bool _attempted;
    private bool _pinReuseBlocked;
    private bool _validationStampPending;
    private bool _disposed;
    private long _nextCycleId;
    private long _nextCertificateRequestId;

    public AuthenticationCoordinator(
        ClientCertificateAuthenticationOptions authentication,
        IClientCertificatePinStore pinStore,
        IClientCertificateValidationStampStore stampStore,
        IClientCertificateTokenUnlocker unlocker,
        IClientCertificateNativePromptContinuation? nativePromptContinuation = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _pinStore = pinStore ?? throw new ArgumentNullException(nameof(pinStore));
        _stampStore = stampStore ?? throw new ArgumentNullException(nameof(stampStore));
        _unlocker = unlocker ?? throw new ArgumentNullException(nameof(unlocker));
        _nativePromptContinuation = nativePromptContinuation;
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
                    await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null).ConfigureAwait(false);
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
                _activeAttempt = null;
                await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
                SetState(new(
                    AuthenticationStage.WaitingForDevice,
                    CurrentState.InsertionCycleId,
                    Message: "Waiting for the configured security device."));
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
                CurrentState.InsertionCycleId != navigationBinding.CycleId)
            {
                return ClientCertificateRequestMatchResult.InvalidRequest();
            }

            var result = ClientCertificateRequestMatcher.Match(
                request,
                _authentication,
                HasPreparedAuthentication);

            if (result.Status == ClientCertificateRequestMatchStatus.Selected ||
                result.Status == ClientCertificateRequestMatchStatus.CertificateMissing ||
                result.Status == ClientCertificateRequestMatchStatus.CertificateConflict)
            {
                if (CurrentState.Stage != AuthenticationStage.AwaitingCertificate)
                {
                    return ClientCertificateRequestMatchResult.TokenNotUnlocked();
                }

                _activeAttempt = new(
                    CurrentState.InsertionCycleId!.Value,
                    ++_nextCertificateRequestId,
                    browserOperationId);
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
            if (_businessSuspended || !_devicePresent || !HasPreparedAuthentication ||
                (CurrentState.Stage != AuthenticationStage.AwaitingCertificate &&
                 !(_browserNativePrepared && CurrentState.Stage == AuthenticationStage.Ready)) ||
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
                    await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null)
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

            if (_selectionAuthorization is not null)
            {
                return _selectionAuthorization;
            }

            if (_authentication.NativePromptContinuation is not { Enabled: true })
            {
                return _selectionAuthorization = new ClientCertificateSelectionAuthorization();
            }

            if (_nativePromptContinuation is null || _continuationPin is null)
            {
                await EndCycleWithFailureAsync(AuthenticationErrorCategory.UnknownFailure, null)
                    .ConfigureAwait(false);
                return null;
            }

            ClientCertificateNativePromptArmResult armResult;
            try
            {
                armResult = await _nativePromptContinuation
                    .ArmAsync(_authentication, attempt, _continuationPin, cancellationToken)
                    .ConfigureAwait(false);
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

            _nativePromptAttempt = armResult.Attempt;
            _selectionAuthorization = new ClientCertificateSelectionAuthorization();
            _ = ObserveNativePromptCompletionAsync(attempt, armResult.Attempt!);
            return _selectionAuthorization;
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
            _attempted = false;
            _activeAttempt = null;
            await ReleaseAuthenticationResourcesAsync().ConfigureAwait(false);
            if (_devicePresent)
            {
                await StartCycleAsync(++_nextCycleId, cancellationToken).ConfigureAwait(false);
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

        try
        {
            using var pin = await _pinStore.ReadAsync(_authentication.CredentialTargetName, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (pin is null || pin.AsMemory().IsEmpty)
            {
                SetState(new(AuthenticationStage.RequiresPinSetup, cycleId,
                    AuthenticationErrorCategory.CredentialsUnavailable,
                    Message: "未找到已保存的 PIN，请先设置 PIN，再启动业务。"));
                return;
            }

            // No local CSP, container, certificate identity, or per-Key validation stamp is consulted.
            // The browser and bank perform authentication using the actual certificate request.
            _continuationPin = ClientCertificatePin.CopyOf(pin.AsMemory());
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
        _validationStampPending = false;
        _navigationBinding = null;
        _selectionAuthorization?.Revoke();
        _selectionAuthorization = null;
        DisposeContinuationPin();
        var nativePromptAttempt = _nativePromptAttempt;
        _nativePromptAttempt = null;
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
        var pin = _continuationPin;
        _continuationPin = null;
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

    private sealed record NavigationCycleBinding(long BrowserOperationId, long CycleId);

    private void SetState(AuthenticationState state)
    {
        CurrentState = state;
        lock (_notificationSync)
        {
            _pendingStateChanges.Enqueue(state);
        }
    }

    private bool IsCurrentAttempt(AuthenticationAttempt attempt) =>
        _devicePresent &&
        CurrentState.Stage == AuthenticationStage.Authenticating &&
        Equals(_activeAttempt, attempt);

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
            _ => AuthenticationErrorCategory.UnknownFailure
        };

    private static AuthenticationErrorCategory MapNativePromptFailure(
        ClientCertificateNativePromptStatus status) => status switch
        {
            ClientCertificateNativePromptStatus.TimedOut => AuthenticationErrorCategory.TimedOut,
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
        AuthenticationErrorCategory.InvalidCertificateRequest => "The client certificate request is invalid.",
        _ => "Automatic authentication could not be completed."
    };
}
