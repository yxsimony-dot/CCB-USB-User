using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Browser;

namespace UsbWebLauncher.App.Browser;

public sealed class WebView2BrowserHost :
    IClientCertificateConfigurableBrowserHost,
    IBrowserHostAvailability,
    IAsyncDisposable
{
    private static readonly TimeSpan DefaultCancelledNavigationStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IWebView2Control _control;
    private readonly IBrowserTimeoutScheduler _timeoutScheduler;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _physicalNavigationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly WebClientCertificateRequestHandler _certificateHandlerDelegate;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TimeSpan _cancelledNavigationStopTimeout;
    private AuthenticationConfiguration? _authentication;
    private NavigationOperation? _activeOperation;
    private bool _initialized;
    private bool _certificateHandlerRegistered;
    private bool _navigationStarted;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return !_disposed;
            }
        }
    }

    public WebView2BrowserHost(IWebView2Control control)
        : this(control, new BrowserTimeoutScheduler())
    {
    }

    internal WebView2BrowserHost(
        IWebView2Control control,
        IBrowserTimeoutScheduler timeoutScheduler)
        : this(control, timeoutScheduler, DefaultCancelledNavigationStopTimeout)
    {
    }

    internal WebView2BrowserHost(
        IWebView2Control control,
        IBrowserTimeoutScheduler timeoutScheduler,
        TimeSpan cancelledNavigationStopTimeout)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _timeoutScheduler = timeoutScheduler ?? throw new ArgumentNullException(nameof(timeoutScheduler));
        if (cancelledNavigationStopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancelledNavigationStopTimeout));
        _cancelledNavigationStopTimeout = cancelledNavigationStopTimeout;
        _certificateHandlerDelegate = HandleClientCertificateRequestAsync;
        _control.NavigationStarting += OnNavigationStarting;
        _control.NavigationCompleted += OnNavigationCompleted;
    }

    public async Task ConfigureClientCertificateAuthenticationAsync(
        IBrowserClientCertificateRequestHandler handler,
        IBrowserClientCertificateAuthenticationReporter reporter,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(reporter);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_authentication is not null)
                throw new InvalidOperationException("Client-certificate authentication is already configured.");
            if (_navigationStarted)
                throw new InvalidOperationException("Client-certificate authentication must be configured before navigation starts.");

            _authentication = new(handler, reporter, timeout);
            if (_initialized) await RegisterCertificateHandlerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await InitializeAndRegisterAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default) =>
        NavigateAsync(target, BrowserOperationIdentity.Create(), cancellationToken);

    public async Task NavigateAsync(
        Uri target,
        long browserOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (browserOperationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(browserOperationId));
        }
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);

        NavigationOperation? operation = null;
        var physicalGateTransferred = false;
        var physicalGateAcquired = false;
        try
        {
            await _physicalNavigationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            physicalGateAcquired = true;
            await _lifecycleGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await InitializeAndRegisterAsync(linkedCancellation.Token).ConfigureAwait(false);
                _navigationStarted = true;
                operation = new NavigationOperation(browserOperationId);
                lock (_sync)
                {
                    if (_activeOperation is not null)
                        throw new InvalidOperationException("A physical browser navigation is still active.");
                    _activeOperation = operation;
                }

                physicalGateTransferred = true;
                var authentication = _authentication;
                if (authentication is not null)
                {
                    try
                    {
                        await authentication.Reporter
                            .ReportNavigationStartedAsync(operation.OperationId, linkedCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        ReleasePhysicalNavigation(operation);
                        operation = null;
                        throw;
                    }
                }

                Task<long> startTask;
                try
                {
                    startTask = _control.StartNavigationAsync(target, _lifetimeCancellation.Token);
                }
                catch
                {
                    await TerminateAuthenticationAsync(operation, isSuccess: false).ConfigureAwait(false);
                    ReleasePhysicalNavigation(operation);
                    throw;
                }

                operation.StartTask = startTask;
                operation.BindingTask = BindNavigationIdentityAsync(operation, startTask);
                Observe(operation.BindingTask);
            }
            finally
            {
                _lifecycleGate.Release();
            }

            await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            if (operation is not null)
            {
                await TerminateAuthenticationAsync(operation, isSuccess: false).ConfigureAwait(false);
            }

            throw new ObjectDisposedException(nameof(WebView2BrowserHost));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && operation is not null)
        {
            Observe(operation.Completion.Task);
            await TerminateAuthenticationAsync(operation, isSuccess: false).ConfigureAwait(false);
            await StopCancelledNavigationAsync(operation).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (physicalGateAcquired && !physicalGateTransferred)
                _physicalNavigationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        NavigationOperation? operation;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                operation = _activeOperation;
            }

            _lifetimeCancellation.Cancel();
            _control.NavigationStarting -= OnNavigationStarting;
            _control.NavigationCompleted -= OnNavigationCompleted;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        Exception? disposalFailure = null;
        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        if (operation is not null)
        {
            operation.Completion.TrySetException(
                new ObjectDisposedException(nameof(WebView2BrowserHost)));
            await TerminateAuthenticationAsync(operation, isSuccess: false).ConfigureAwait(false);
            ReleasePhysicalNavigation(operation);
            if (operation.BindingTask is not null)
            {
                await operation.BindingTask.ConfigureAwait(false);
            }
        }

        if (disposalFailure is not null)
        {
            throw disposalFailure;
        }
    }

    private async Task InitializeAndRegisterAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await _control.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        await RegisterCertificateHandlerAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RegisterCertificateHandlerAsync(CancellationToken cancellationToken)
    {
        if (_authentication is null || _certificateHandlerRegistered) return;
        await _control.RegisterClientCertificateRequestHandlerAsync(
            _certificateHandlerDelegate, cancellationToken).ConfigureAwait(false);
        _certificateHandlerRegistered = true;
    }

    private async Task BindNavigationIdentityAsync(NavigationOperation operation, Task<long> startTask)
    {
        try
        {
            var navigationId = await startTask.ConfigureAwait(false);
            if (navigationId <= 0) throw new InvalidOperationException("The browser returned an invalid navigation identity.");
            if (operation.TryBindNavigationId(navigationId, out var earlyCompletion))
            {
                if (earlyCompletion is not null)
                    await CompletePhysicalNavigationAsync(operation, earlyCompletion).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("The browser navigation identity could not be bound.");
        }
        catch (Exception exception)
        {
            if (_disposed && exception is OperationCanceledException)
            {
                exception = new ObjectDisposedException(nameof(WebView2BrowserHost));
            }
            operation.Completion.TrySetException(exception);
            await TerminateAuthenticationAsync(operation, isSuccess: false).ConfigureAwait(false);
            ReleasePhysicalNavigation(operation);
        }
    }

    private async Task StopCancelledNavigationAsync(NavigationOperation operation)
    {
        try
        {
            await _control.StopNavigationAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (operation.NavigationId is null &&
            operation.StartTask?.IsCompleted == true &&
            operation.BindingTask is not null)
        {
            await operation.BindingTask.ConfigureAwait(false);
        }

        if (operation.NavigationId is null)
        {
            // Without NavigationStarting there is no native identity with which to
            // quarantine late callbacks. Disposing the control is the only safe
            // fail-closed outcome; releasing this gate for reuse would let an old
            // certificate request be misattributed to a later navigation.
            await DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await operation.Completion.Task.WaitAsync(_cancelledNavigationStopTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A stopped navigation normally completes as a browser navigation error.
            // The terminal callback has already released the physical operation.
        }
    }

    private void OnNavigationCompleted(object? sender, WebNavigationCompletedEventArgs eventArgs)
    {
        NavigationOperation? operation;
        lock (_sync) operation = _activeOperation;
        if (operation is null) return;

        if (operation.TryAcceptCompletion(eventArgs))
            Observe(CompletePhysicalNavigationAsync(operation, eventArgs));
    }

    private void OnNavigationStarting(
        object? sender,
        WebNavigationStartingEventArgs eventArgs)
    {
        AuthenticationConfiguration? authentication;
        lock (_sync)
        {
            authentication = _authentication;
        }

        if (authentication is null)
        {
            eventArgs.BindAuthentication(Task.CompletedTask);
            return;
        }

        if (!_physicalNavigationGate.Wait(0))
        {
            eventArgs.BindAuthentication(Task.FromException(
                new InvalidOperationException(
                    "A browser navigation is already active.")));
            return;
        }

        NavigationOperation operation;
        lock (_sync)
        {
            if (_disposed || _activeOperation is not null)
            {
                _physicalNavigationGate.Release();
                eventArgs.BindAuthentication(Task.FromException(
                    new InvalidOperationException(
                        "A browser navigation is already active.")));
                return;
            }

            _navigationStarted = true;
            operation = new NavigationOperation(BrowserOperationIdentity.Create());
            operation.TryBindNavigationId(eventArgs.NavigationId, out _);
            operation.StartTask = Task.FromResult(eventArgs.NavigationId);
            _activeOperation = operation;
        }

        var bindingTask = ReportExternalNavigationStartedAsync(operation, authentication);
        operation.BindingTask = bindingTask;
        eventArgs.BindAuthentication(bindingTask);
        Observe(bindingTask);
    }

    private async Task ReportExternalNavigationStartedAsync(
        NavigationOperation operation,
        AuthenticationConfiguration authentication)
    {
        try
        {
            await authentication.Reporter
                .ReportNavigationStartedAsync(
                    operation.OperationId,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            operation.MarkAuthenticationBindingFailed();
            throw;
        }
    }

    private async Task CompletePhysicalNavigationAsync(
        NavigationOperation operation,
        WebNavigationCompletedEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.IsSuccess)
                operation.Completion.TrySetResult();
            else
                operation.Completion.TrySetException(new BrowserNavigationException(eventArgs.ErrorStatus ?? "Unknown"));

            await TerminateAuthenticationAsync(operation, eventArgs.IsSuccess).ConfigureAwait(false);
        }
        finally
        {
            ReleasePhysicalNavigation(operation);
        }
    }

    private void ReleasePhysicalNavigation(NavigationOperation operation)
    {
        var release = false;
        lock (_sync)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
                release = true;
            }
        }

        if (release) _physicalNavigationGate.Release();
    }

    private async ValueTask<WebClientCertificateSelection> HandleClientCertificateRequestAsync(
        WebClientCertificateRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticationConfiguration? authentication;
        NavigationOperation? operation;
        lock (_sync)
        {
            authentication = _authentication;
            operation = _activeOperation;
        }

        if (authentication is null || operation is null || operation.IsAuthenticationTerminal ||
            cancellationToken.IsCancellationRequested)
            return WebClientCertificateSelection.Deny();

        ClientCertificateRequestMatchResult result;
        try
        {
            result = await authentication.Handler
                .HandleAsync(request.ToSnapshot(operation.OperationId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return WebClientCertificateSelection.Deny();
        }

        var requiresManualSelection =
            result.Status is ClientCertificateRequestMatchStatus.CertificateMissing
                or ClientCertificateRequestMatchStatus.CertificateConflict;
        if ((!requiresManualSelection &&
             (result.Status != ClientCertificateRequestMatchStatus.Selected ||
              result.SelectedCertificate is null)) ||
            result.Attempt is null)
            return WebClientCertificateSelection.Deny();

        WebClientCertificate? selected = null;
        if (!requiresManualSelection)
        {
            selected = request.AvailableCertificates.SingleOrDefault(candidate =>
                ReferenceEquals(candidate.Candidate, result.SelectedCertificate));
            if (selected is null)
            {
                if (operation.TryClaimAuthenticationTerminal(out _))
                    await ReportSelectionFailureAsync(authentication.Reporter, result.Attempt).ConfigureAwait(false);
                return WebClientCertificateSelection.Deny();
            }
        }

        var pending = new PendingAuthenticationAttempt(result.Attempt, authentication.Reporter);
        var timeoutToken = pending.TimeoutToken;
        if (!operation.TrySetPending(pending))
        {
            pending.Dispose();
            await ReportSelectionFailureAsync(authentication.Reporter, result.Attempt)
                .ConfigureAwait(false);
            return WebClientCertificateSelection.Deny();
        }

        Observe(ReportTimeoutAsync(
            operation,
            pending,
            timeoutToken,
            authentication.Timeout,
            _timeoutScheduler));

        ClientCertificateSelectionAuthorization? authorization;
        try
        {
            authorization = await authentication.Reporter
                .ReportCertificateSelectedAsync(result.Attempt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (operation.TryClaimExpectedPendingTerminal(pending, out var owned))
            {
                owned!.CancelAndDispose();
                await ReportSelectionFailureAsync(authentication.Reporter, result.Attempt).ConfigureAwait(false);
            }
            return WebClientCertificateSelection.Deny();
        }

        if (authorization is null)
        {
            if (operation.TryClaimExpectedPendingTerminal(pending, out var owned))
                owned!.CancelAndDispose();
            return WebClientCertificateSelection.Deny();
        }

        if (!operation.ContainsPending(pending))
            return WebClientCertificateSelection.Deny();

        return requiresManualSelection
            ? WebClientCertificateSelection.PassThrough(authorization)
            : WebClientCertificateSelection.Select(selected!, authorization);
    }

    private async Task TerminateAuthenticationAsync(NavigationOperation operation, bool isSuccess)
    {
        if (!operation.TryClaimAuthenticationTerminal(out var pending)) return;

        if (pending is null)
        {
            var authentication = _authentication;
            if (authentication is null) return;
            try
            {
                await authentication.Reporter
                    .ReportNavigationCompletedWithoutCertificateAsync(operation.OperationId, isSuccess)
                    .ConfigureAwait(false);
            }
            catch { }
            return;
        }

        pending.CancelAndDispose();
        try
        {
            if (isSuccess)
                await pending.Reporter.ReportTlsCompletedAsync(pending.Attempt).ConfigureAwait(false);
            else
                await pending.Reporter.ReportTlsFailureAsync(pending.Attempt).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task ReportTimeoutAsync(
        NavigationOperation operation,
        PendingAuthenticationAttempt pending,
        CancellationToken timeoutToken,
        TimeSpan timeout,
        IBrowserTimeoutScheduler timeoutScheduler)
    {
        try
        {
            await timeoutScheduler.WaitAsync(timeout, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        if (!operation.TryClaimExpectedPendingTerminal(pending, out var owned)) return;
        owned!.Dispose();
        try
        {
            await owned.Reporter.ReportTimeoutAsync(owned.Attempt).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task ReportSelectionFailureAsync(
        IBrowserClientCertificateAuthenticationReporter reporter,
        AuthenticationAttempt attempt)
    {
        try
        {
            await reporter.ReportTlsFailureAsync(attempt, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record AuthenticationConfiguration(
        IBrowserClientCertificateRequestHandler Handler,
        IBrowserClientCertificateAuthenticationReporter Reporter,
        TimeSpan Timeout);

    private sealed class NavigationOperation(long operationId)
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, WebNavigationCompletedEventArgs> _earlyCompletions = [];
        private PendingAuthenticationAttempt? _pending;
        private long? _navigationId;
        private bool _navigationTerminal;
        private bool _authenticationTerminal;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long>? StartTask { get; set; }

        public Task? BindingTask { get; set; }

        public long OperationId { get; } = operationId;

        public long? NavigationId { get { lock (_sync) return _navigationId; } }
        public bool IsAuthenticationTerminal { get { lock (_sync) return _authenticationTerminal; } }

        public void MarkAuthenticationBindingFailed()
        {
            lock (_sync)
            {
                _authenticationTerminal = true;
            }
        }

        public bool TryBindNavigationId(long navigationId, out WebNavigationCompletedEventArgs? earlyCompletion)
        {
            lock (_sync)
            {
                earlyCompletion = null;
                if (_navigationId is not null) return false;
                _navigationId = navigationId;
                if (_earlyCompletions.Remove(navigationId, out earlyCompletion))
                    _navigationTerminal = true;
                _earlyCompletions.Clear();
                return true;
            }
        }

        public bool TryAcceptCompletion(WebNavigationCompletedEventArgs completion)
        {
            lock (_sync)
            {
                if (_navigationTerminal) return false;
                if (_navigationId is null)
                {
                    _earlyCompletions[completion.NavigationId] = completion;
                    return false;
                }
                if (_navigationId.Value != completion.NavigationId) return false;
                _navigationTerminal = true;
                return true;
            }
        }

        public bool TrySetPending(PendingAuthenticationAttempt pending)
        {
            lock (_sync)
            {
                if (_authenticationTerminal || _pending is not null) return false;
                _pending = pending;
                return true;
            }
        }

        public bool ContainsPending(PendingAuthenticationAttempt pending)
        {
            lock (_sync) return !_authenticationTerminal && ReferenceEquals(_pending, pending);
        }

        public bool TryClaimExpectedPendingTerminal(
            PendingAuthenticationAttempt expected,
            out PendingAuthenticationAttempt? owned)
        {
            lock (_sync)
            {
                owned = null;
                if (_authenticationTerminal || !ReferenceEquals(_pending, expected)) return false;
                _authenticationTerminal = true;
                owned = _pending;
                _pending = null;
                return true;
            }
        }

        public bool TryClaimAuthenticationTerminal(out PendingAuthenticationAttempt? owned)
        {
            lock (_sync)
            {
                owned = null;
                if (_authenticationTerminal) return false;
                _authenticationTerminal = true;
                owned = _pending;
                _pending = null;
                return true;
            }
        }
    }

    private sealed class PendingAuthenticationAttempt(
        AuthenticationAttempt attempt,
        IBrowserClientCertificateAuthenticationReporter reporter) : IDisposable
    {
        private readonly CancellationTokenSource _timeout = new();
        private int _disposed;
        public AuthenticationAttempt Attempt { get; } = attempt;
        public IBrowserClientCertificateAuthenticationReporter Reporter { get; } = reporter;
        public CancellationToken TimeoutToken => _timeout.Token;

        public void CancelAndDispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timeout.Cancel();
            _timeout.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timeout.Dispose();
        }
    }
}

public sealed class BrowserNavigationException(string errorStatus)
    : Exception($"页面导航失败：{errorStatus}")
{
}
