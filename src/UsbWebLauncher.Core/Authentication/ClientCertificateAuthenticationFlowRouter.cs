using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Authentication;

public sealed class ClientCertificateAuthenticationFlowRouter :
    IClientCertificateSetupVerifier,
    IBrowserClientCertificateRequestHandler,
    IBrowserClientCertificateAuthenticationReporter,
    IAsyncDisposable
{
    private readonly ClientCertificateAuthenticationOptions _authentication;
    private readonly ICorrelatedBrowserHost _browser;
    private readonly Uri _target;
    private readonly IBrowserClientCertificateRequestHandler _normalHandler;
    private readonly IBrowserClientCertificateAuthenticationReporter _normalReporter;
    private readonly IClientCertificateNativePromptContinuation? _nativePromptContinuation;
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private readonly object _sync = new();
    private SetupContext? _setup;
    private bool _disposed;
    private long _nextSetupId;

    public event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged;

    public ClientCertificateAuthenticationFlowRouter(
        ClientCertificateAuthenticationOptions authentication,
        ICorrelatedBrowserHost browser,
        Uri target,
        IBrowserClientCertificateRequestHandler normalHandler,
        IBrowserClientCertificateAuthenticationReporter normalReporter,
        IClientCertificateNativePromptContinuation? nativePromptContinuation = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _normalHandler = normalHandler ?? throw new ArgumentNullException(nameof(normalHandler));
        _normalReporter = normalReporter ?? throw new ArgumentNullException(nameof(normalReporter));
        _nativePromptContinuation = nativePromptContinuation;
    }

    public async ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
        ClientCertificateAuthenticationOptions authentication,
        IClientCertificateTokenUnlockSession session,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pin);
        if (!string.Equals(authentication.GetNormalizedHash(), _authentication.GetNormalizedHash(), StringComparison.Ordinal))
        {
            return new(ClientCertificateSetupValidationStatus.Incompatible);
        }

        await _setupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetupContext? context = null;
        try
        {
            var browserOperationId = BrowserOperationIdentity.Create();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                context = new SetupContext(
                    Interlocked.Increment(ref _nextSetupId),
                    browserOperationId,
                    session,
                    pin,
                    _authentication.NativePromptContinuation is { Enabled: true });
                _setup = context;
            }

            using var cancellationRegistration = cancellationToken.Register(
                static state => _ = ((SetupContext)state!).CompleteAsync(
                    new(ClientCertificateSetupValidationStatus.Cancelled)),
                context);

            try
            {
                await _browser.NavigateAsync(_target, browserOperationId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await context.CompleteAsync(new(ClientCertificateSetupValidationStatus.Cancelled))
                    .ConfigureAwait(false);
            }
            catch
            {
                await context.CompleteAsync(new(ClientCertificateSetupValidationStatus.TlsFailed))
                    .ConfigureAwait(false);
            }

            return await context.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (context is not null)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_setup, context))
                    {
                        _setup = null;
                    }
                }

                await context.CompleteAsync(new(ClientCertificateSetupValidationStatus.Cancelled))
                    .ConfigureAwait(false);
            }

            _setupGate.Release();
        }
    }

    public async ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
        ClientCertificateRequestSnapshot request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var setup = GetSetupForOperation(request.BrowserOperationId);
        if (setup is null)
        {
            return await _normalHandler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var result = ClientCertificateRequestMatcher.Match(request, _authentication, isTokenUnlocked: true);
        if (result.Status != ClientCertificateRequestMatchStatus.Selected)
        {
            await setup.CompleteAsync(new(ClientCertificateSetupValidationStatus.CertificateRejected))
                .ConfigureAwait(false);
            return result;
        }

        var attempt = setup.CreateAttempt();
        if (attempt is null)
        {
            return ClientCertificateRequestMatchResult.TokenNotUnlocked();
        }

        PublishState(new(
            AuthenticationStage.Authenticating,
            attempt.InsertionCycleId,
            Message: "正在完成客户端证书验证。",
            Attempt: attempt));
        return result with { Attempt = attempt };
    }

    public Task ReportNavigationStartedAsync(
        long browserOperationId,
        CancellationToken cancellationToken = default)
    {
        SetupContext? setup;
        lock (_sync)
        {
            setup = _setup;
            if (setup is not null && setup.IsOperation(browserOperationId))
            {
                return Task.CompletedTask;
            }
        }

        return _normalReporter.ReportNavigationStartedAsync(browserOperationId, cancellationToken);
    }

    public async Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        var setup = GetSetupForAttempt(attempt);
        if (setup is null)
        {
            return await _normalReporter.ReportCertificateSelectedAsync(attempt, cancellationToken)
                .ConfigureAwait(false);
        }

        return await setup.ArmAsync(
                _authentication,
                attempt,
                _nativePromptContinuation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReportTlsCompletedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        var setup = GetSetupForAttempt(attempt);
        if (setup is null)
        {
            await _normalReporter.ReportTlsCompletedAsync(attempt, cancellationToken).ConfigureAwait(false);
            return;
        }

        await setup.CompleteTlsSuccessAsync().ConfigureAwait(false);
    }

    public async Task ReportTlsFailureAsync(
        AuthenticationAttempt attempt,
        int? publicErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        var setup = GetSetupForAttempt(attempt);
        if (setup is null)
        {
            await _normalReporter.ReportTlsFailureAsync(attempt, publicErrorCode, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await setup.CompleteAsync(new(ClientCertificateSetupValidationStatus.TlsFailed, publicErrorCode))
            .ConfigureAwait(false);
    }

    public async Task ReportTimeoutAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        var setup = GetSetupForAttempt(attempt);
        if (setup is null)
        {
            await _normalReporter.ReportTimeoutAsync(attempt, cancellationToken).ConfigureAwait(false);
            return;
        }

        await setup.CompleteAsync(new(ClientCertificateSetupValidationStatus.TimedOut))
            .ConfigureAwait(false);
    }

    public async Task ReportNavigationCompletedWithoutCertificateAsync(
        long browserOperationId,
        bool isSuccess,
        CancellationToken cancellationToken = default)
    {
        var setup = GetSetupForOperation(browserOperationId);
        if (setup is null)
        {
            await _normalReporter.ReportNavigationCompletedWithoutCertificateAsync(
                    browserOperationId, isSuccess, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await setup.CompleteAsync(new(
                isSuccess
                    ? ClientCertificateSetupValidationStatus.CertificateRejected
                    : ClientCertificateSetupValidationStatus.TlsFailed))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        SetupContext? setup;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            setup = _setup;
            _setup = null;
        }

        if (setup is not null)
        {
            await setup.CompleteAsync(new(ClientCertificateSetupValidationStatus.Cancelled))
                .ConfigureAwait(false);
        }
    }

    private SetupContext? GetSetupForOperation(long? browserOperationId)
    {
        lock (_sync)
        {
            return browserOperationId is long operationId &&
                   _setup is { } setup &&
                   setup.IsOperation(operationId)
                ? setup
                : null;
        }
    }

    private SetupContext? GetSetupForAttempt(AuthenticationAttempt attempt)
    {
        lock (_sync)
        {
            return _setup is { } setup && setup.IsAttempt(attempt) ? setup : null;
        }
    }

    private void PublishState(AuthenticationState state)
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        var eventArgs = new AuthenticationStateChangedEventArgs(state);
        foreach (EventHandler<AuthenticationStateChangedEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(this, eventArgs); }
            catch { }
        }
    }

    private sealed class SetupContext(
        long setupId,
        long browserOperationId,
        IClientCertificateTokenUnlockSession session,
        ClientCertificatePin pin,
        bool continuationEnabled)
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _armGate = new(1, 1);
        private ClientCertificateSelectionAuthorization? _authorization;
        private AuthenticationAttempt? _attempt;
        private IClientCertificateNativePromptAttempt? _nativePromptAttempt;
        private bool _nativePromptSucceeded;
        private bool _completed;

        public TaskCompletionSource<ClientCertificateSetupValidationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsOperation(long operationId)
        {
            lock (_sync) return !_completed && browserOperationId == operationId;
        }

        public AuthenticationAttempt? CreateAttempt()
        {
            lock (_sync)
            {
                if (_completed || _attempt is not null)
                {
                    return null;
                }

                _attempt = new(setupId, 1, browserOperationId);
                return _attempt;
            }
        }

        public bool IsAttempt(AuthenticationAttempt attempt)
        {
            lock (_sync) return Equals(_attempt, attempt);
        }

        public async Task<ClientCertificateSelectionAuthorization?> ArmAsync(
            ClientCertificateAuthenticationOptions authentication,
            AuthenticationAttempt attempt,
            IClientCertificateNativePromptContinuation? continuation,
            CancellationToken cancellationToken)
        {
            var armGateAcquired = false;
            try
            {
                try
                {
                    await _armGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    armGateAcquired = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await CompleteAsync(new(ClientCertificateSetupValidationStatus.Cancelled))
                        .ConfigureAwait(false);
                    return null;
                }

                lock (_sync)
                {
                    if (_completed || !Equals(_attempt, attempt))
                    {
                        return null;
                    }

                    if (_authorization is not null)
                    {
                        return _authorization;
                    }
                }

                if (!continuationEnabled)
                {
                    lock (_sync)
                    {
                        return _completed
                            ? null
                            : _authorization = new ClientCertificateSelectionAuthorization();
                    }
                }

                if (continuation is null)
                {
                    await CompleteAsync(new(ClientCertificateSetupValidationStatus.Incompatible))
                        .ConfigureAwait(false);
                    return null;
                }

                ClientCertificateNativePromptArmResult armResult;
                try
                {
                    armResult = await continuation.ArmAsync(authentication, attempt, pin, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await CompleteAsync(new(ClientCertificateSetupValidationStatus.Cancelled))
                        .ConfigureAwait(false);
                    return null;
                }
                catch
                {
                    await CompleteAsync(new(ClientCertificateSetupValidationStatus.Incompatible))
                        .ConfigureAwait(false);
                    return null;
                }

                if (!armResult.IsArmed)
                {
                    if (armResult.Attempt is not null)
                    {
                        await DisposeAttemptAsync(armResult.Attempt).ConfigureAwait(false);
                    }

                    await CompleteAsync(MapArmFailure(armResult)).ConfigureAwait(false);
                    return null;
                }

                var disposeReturnedAttempt = false;
                ClientCertificateSelectionAuthorization? authorization;
                lock (_sync)
                {
                    if (_completed)
                    {
                        disposeReturnedAttempt = true;
                        authorization = null;
                    }
                    else
                    {
                        _nativePromptAttempt = armResult.Attempt;
                        authorization = _authorization = new ClientCertificateSelectionAuthorization();
                    }
                }

                if (disposeReturnedAttempt)
                {
                    await DisposeAttemptAsync(armResult.Attempt!).ConfigureAwait(false);
                    return null;
                }

                _ = ObserveNativePromptCompletionAsync(armResult.Attempt!);
                return authorization;
            }
            finally
            {
                if (armGateAcquired)
                {
                    _armGate.Release();
                }
            }
        }

        public async Task CompleteTlsSuccessAsync()
        {
            Task<ClientCertificateNativePromptResult>? pendingPrompt = null;
            lock (_sync)
            {
                if (!_completed && continuationEnabled && !_nativePromptSucceeded)
                {
                    pendingPrompt = _nativePromptAttempt?.Completion;
                }
            }

            if (pendingPrompt is not null)
            {
                ClientCertificateNativePromptResult promptResult;
                try
                {
                    promptResult = await pendingPrompt.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    promptResult = new(ClientCertificateNativePromptStatus.Cancelled);
                }
                catch
                {
                    promptResult = new(ClientCertificateNativePromptStatus.UnknownFailure);
                }

                if (!promptResult.IsSuccess)
                {
                    await CompleteAsync(MapCompletionFailure(promptResult)).ConfigureAwait(false);
                    return;
                }

                lock (_sync)
                {
                    if (!_completed)
                    {
                        _nativePromptSucceeded = true;
                    }
                }
            }

            ClientCertificateSetupValidationResult result;
            lock (_sync)
            {
                if (_completed)
                {
                    result = new(ClientCertificateSetupValidationStatus.Cancelled);
                }
                else
                {
                    result = !continuationEnabled || _nativePromptSucceeded
                        ? ClientCertificateSetupValidationResult.Succeeded()
                        : new(ClientCertificateSetupValidationStatus.Incompatible);
                }
            }

            await CompleteAsync(result).ConfigureAwait(false);
        }

        public async Task CompleteAsync(ClientCertificateSetupValidationResult result)
        {
            IClientCertificateNativePromptAttempt? nativePromptAttempt;
            Task? existingCompletion = null;
            lock (_sync)
            {
                if (_completed)
                {
                    existingCompletion = Completion.Task;
                    nativePromptAttempt = null;
                }
                else
                {
                    _completed = true;
                    _authorization?.Revoke();
                    nativePromptAttempt = _nativePromptAttempt;
                    _nativePromptAttempt = null;
                }
            }

            if (existingCompletion is not null)
            {
                await existingCompletion.ConfigureAwait(false);
                return;
            }

            if (nativePromptAttempt is not null)
            {
                await DisposeAttemptAsync(nativePromptAttempt).ConfigureAwait(false);
            }

            _ = session;
            Completion.TrySetResult(result);
        }

        private async Task ObserveNativePromptCompletionAsync(
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
                lock (_sync)
                {
                    if (!_completed && ReferenceEquals(_nativePromptAttempt, nativePromptAttempt))
                    {
                        _nativePromptSucceeded = true;
                    }
                }

                return;
            }

            await CompleteAsync(MapCompletionFailure(result)).ConfigureAwait(false);
        }

        private static ClientCertificateSetupValidationResult MapArmFailure(
            ClientCertificateNativePromptArmResult result) => result.Status switch
            {
                ClientCertificateNativePromptArmStatus.TimedOut =>
                    new(ClientCertificateSetupValidationStatus.TimedOut, result.PublicErrorCode),
                ClientCertificateNativePromptArmStatus.Cancelled =>
                    new(ClientCertificateSetupValidationStatus.Cancelled, result.PublicErrorCode),
                _ => new(ClientCertificateSetupValidationStatus.Incompatible, result.PublicErrorCode)
            };

        private static ClientCertificateSetupValidationResult MapCompletionFailure(
            ClientCertificateNativePromptResult result) => result.Status switch
            {
                ClientCertificateNativePromptStatus.TimedOut =>
                    new(ClientCertificateSetupValidationStatus.TimedOut, result.PublicErrorCode),
                ClientCertificateNativePromptStatus.Cancelled =>
                    new(ClientCertificateSetupValidationStatus.Cancelled, result.PublicErrorCode),
                _ => new(ClientCertificateSetupValidationStatus.Incompatible, result.PublicErrorCode)
            };

        private static async ValueTask DisposeAttemptAsync(
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
    }
}
