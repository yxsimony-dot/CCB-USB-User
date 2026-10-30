using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.App.Browser;

public sealed class WpfWebView2Control : IWebView2Control, IAsyncDisposable
{
    private readonly WebView2 _control;
    private readonly Func<string, Task<CoreWebView2Environment>> _createEnvironment;
    private readonly UiThreadResourceDisposer _disposer;
    private readonly CcbUKeyBrowserExtensionInstaller _extensionInstaller = new();
    private readonly WebView2NavigationSessionTracker _navigationSessions = new();
    private readonly WebView2ControlLifetime _lifetime = new();
    private WebClientCertificateRequestHandler? _clientCertificateHandler;
    private bool _clientCertificateHandlerRegistered;
    private TaskCompletionSource<long>? _pendingNavigationStart;
    private bool _disposed;
    private WpfKnownPopupHandler? _popupHandler;
    private WpfDownloadDirectory? _downloadDirectory;
    private readonly DownloadDirectorySettings? _downloadSettings;
    private readonly Action<string> _downloadError;
    private readonly BrowserSessionDirectory _sessionDirectory;
    private readonly BrowserSessionDirectoryOwnership _sessionDirectoryOwnership;
    private readonly CancellationToken _sessionToken;
    private readonly Action<string>? _initializationPhase;
    private readonly TaskCompletionSource<uint> _browserProcessExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? _environment;

    public WpfWebView2Control(WebView2 control, DownloadDirectorySettings? downloadSettings = null,
        Action<string>? downloadError = null, BrowserSessionDirectory? sessionDirectory = null,
        CancellationToken sessionToken = default)
        : this(
            control,
            downloadSettings,
            downloadError,
            sessionDirectory,
            sessionToken,
            CreateEnvironmentAsync,
            initializationPhase: null)
    {
    }

    internal WpfWebView2Control(
        WebView2 control,
        DownloadDirectorySettings? downloadSettings,
        Action<string>? downloadError,
        BrowserSessionDirectory? sessionDirectory,
        CancellationToken sessionToken,
        Action<string> initializationPhase)
        : this(
            control,
            downloadSettings,
            downloadError,
            sessionDirectory,
            sessionToken,
            CreateEnvironmentAsync,
            initializationPhase)
    {
    }

    internal WpfWebView2Control(
        WebView2 control,
        DownloadDirectorySettings? downloadSettings,
        Action<string>? downloadError,
        BrowserSessionDirectory? sessionDirectory,
        CancellationToken sessionToken,
        Func<string, Task<CoreWebView2Environment>> createEnvironment,
        Action<string>? initializationPhase = null)
    {
        _sessionToken = sessionToken;
        _initializationPhase = initializationPhase;
        _sessionDirectory = sessionDirectory ?? new BrowserSessionDirectory();
        _sessionDirectoryOwnership = new(_sessionDirectory, RetireSessionDirectory);
        _createEnvironment = createEnvironment ?? throw new ArgumentNullException(nameof(createEnvironment));
        _downloadSettings = downloadSettings;
        _downloadError = downloadError ?? (_ => { });
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _control.NavigationStarting += OnNavigationStarting;
        _control.NavigationCompleted += OnNavigationCompleted;
        _disposer = new(
            _control.Dispatcher.CheckAccess,
            action => _control.Dispatcher.InvokeAsync(action).Task,
            DisposeOnUiThread);
    }

    internal BrowserSessionDirectoryOwnershipState SessionDirectoryState =>
        _sessionDirectoryOwnership.State;

    internal uint? BrowserProcessId =>
        _control.CoreWebView2?.BrowserProcessId;

    internal Task<uint> BrowserProcessExited => _browserProcessExited.Task;

    public event EventHandler<WebNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<WebNavigationCompletedEventArgs>? NavigationCompleted;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed();
        if (_control.Dispatcher.CheckAccess())
        {
            _lifetime.ThrowIfDisposed();
            await EnsureInitializedOnUiThreadAsync(cancellationToken);
            return;
        }

        var initialization = _control.Dispatcher
            .InvokeAsync(() =>
            {
                _lifetime.ThrowIfDisposed();
                return EnsureInitializedOnUiThreadAsync(cancellationToken);
            })
            .Task
            .Unwrap();
        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedOnUiThreadAsync(CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed();
        if (_control.CoreWebView2 is null)
        {
            var sessionPath = _sessionDirectoryOwnership.EnsureCreated();
            ReportInitializationPhase("session-directory-created");
            ObserveDirectoryCleanup(
                Task.Run(() => BrowserSessionDirectory.CleanupRetired(
                    System.IO.Path.GetDirectoryName(sessionPath)!)),
                "browser-retired-directory-scan");
            CoreWebView2Environment environment;
            try
            {
                ReportInitializationPhase("environment-creation-started");
                environment = await _createEnvironment(sessionPath) ??
                    throw new InvalidOperationException("WebView2 environment creation returned no environment.");
                ReportInitializationPhase("environment-creation-completed");
            }
            catch
            {
                ReportInitializationPhase("environment-creation-failed");
                _sessionDirectoryOwnership.EnvironmentCreationFailed();
                throw;
            }
            // Keep ownership even when cancellation wins during environment creation:
            // cleanup may only happen after its browser process exit is observed.
            _environment = environment;
            environment.BrowserProcessExited += OnBrowserProcessExited;
            _sessionDirectoryOwnership.EnvironmentCreated();
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.ThrowIfDisposed();
            if (!string.Equals(System.IO.Path.GetFullPath(environment.UserDataFolder).TrimEnd('\\', '/'),
                    System.IO.Path.GetFullPath(sessionPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("浏览器数据目录被外部配置覆盖，无法建立独立业务会话。");
            try
            {
                ReportInitializationPhase("controller-initialization-started");
                await _control.EnsureCoreWebView2Async(environment)
                    .WaitAsync(cancellationToken);
                ReportInitializationPhase("controller-initialization-completed");
            }
            catch
            {
                ReportInitializationPhase("controller-initialization-failed");
                throw;
            }
        }

        _lifetime.ThrowIfDisposed();
        var coreWebView2 = _control.CoreWebView2 ??
            throw new InvalidOperationException("WebView2 initialization did not produce a browser profile.");
        if (_downloadSettings is not null)
            _downloadDirectory ??= new WpfDownloadDirectory(coreWebView2, _downloadSettings, _downloadError);
        var package = CcbUKeyBrowserExtensionPackage.ResolveFromBaseDirectory(
            AppContext.BaseDirectory);
        try
        {
            ReportInitializationPhase("extension-installation-started");
            await _extensionInstaller.EnsureInstalledAsync(
                new WebView2BrowserExtensionProfile(coreWebView2.Profile),
                package,
                cancellationToken);
            ReportInitializationPhase("extension-installation-completed");
        }
        catch
        {
            ReportInitializationPhase("extension-installation-failed");
            throw;
        }
        _lifetime.ThrowIfDisposed();
        try
        {
            _popupHandler ??= new WpfKnownPopupHandler(coreWebView2);
            await _popupHandler.EnsureInitializedAsync();
        }
        catch
        {
            _popupHandler?.Dispose();
            _popupHandler = null;
            CertificateLoginDiagnostics.Write("popup-handler-unavailable");
        }
        _lifetime.ThrowIfDisposed();
        ReportInitializationPhase("initialization-completed");
    }

    public async Task RegisterClientCertificateRequestHandlerAsync(
        WebClientCertificateRequestHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _lifetime.ThrowIfDisposed();

        if (_control.Dispatcher.CheckAccess())
        {
            RegisterClientCertificateHandlerOnUiThread(handler);
            return;
        }

        await _control.Dispatcher.InvokeAsync(
            () =>
            {
                _lifetime.ThrowIfDisposed();
                RegisterClientCertificateHandlerOnUiThread(handler);
            }).Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> StartNavigationAsync(Uri target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        _lifetime.ThrowIfDisposed();
        if (!_control.Dispatcher.CheckAccess())
        {
            var start = _control.Dispatcher
                .InvokeAsync(() => StartNavigationOnUiThread(target))
                .Task
                .Unwrap();
            return await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await StartNavigationOnUiThread(target).WaitAsync(cancellationToken);
    }

    public async Task StopNavigationAsync(CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed();
        if (_control.Dispatcher.CheckAccess())
        {
            StopNavigationOnUiThread();
            return;
        }

        await _control.Dispatcher.InvokeAsync(StopNavigationOnUiThread)
            .Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<long> StartNavigationOnUiThread(Uri target)
    {
        _lifetime.ThrowIfDisposed();
        if (_pendingNavigationStart is not null)
        {
            throw new InvalidOperationException("A browser navigation is already awaiting NavigationStarting.");
        }

        if (_control.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 尚未初始化。");
        }

        _navigationSessions.BeginNavigation();
        var pending = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingNavigationStart = pending;
        try
        {
            _control.CoreWebView2.Navigate(target.AbsoluteUri);
            return pending.Task;
        }
        catch
        {
            _pendingNavigationStart = null;
            _navigationSessions.AbortNavigation();
            throw;
        }
    }

    private void StopNavigationOnUiThread()
    {
        _lifetime.ThrowIfDisposed();
        _control.CoreWebView2?.Stop();
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Dispose();
        return _disposer.DisposeAsync();
    }

    private void DisposeOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var failures = new List<Exception>();
        void Release(string stage, Action action)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            CertificateLoginDiagnostics.Write("browser-release-start", new { stage });
            try
            {
                action();
                CertificateLoginDiagnostics.Write("browser-release-complete", new { stage, elapsedMs = elapsed.ElapsedMilliseconds });
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                CertificateLoginDiagnostics.Write("browser-release-error", new { stage, elapsedMs = elapsed.ElapsedMilliseconds });
            }
        }
        Release("stop-navigation", () => _control.CoreWebView2?.Stop());
        Release("download-handler", () => _downloadDirectory?.Dispose());
        Release("popup-handler", () => _popupHandler?.Dispose());
        var pendingNavigationStart = _pendingNavigationStart;
        _pendingNavigationStart = null;
        pendingNavigationStart?.TrySetException(
            new ObjectDisposedException(nameof(WpfWebView2Control)));
        Release("navigation-session", _navigationSessions.Dispose);
        Release("navigation-start-event", () => _control.NavigationStarting -= OnNavigationStarting);
        Release("navigation-complete-event", () => _control.NavigationCompleted -= OnNavigationCompleted);
        Release("certificate-event", () =>
        {
            if (_clientCertificateHandlerRegistered && _control.CoreWebView2 is not null)
                _control.CoreWebView2.ClientCertificateRequested -= OnClientCertificateRequested;
        });

        _clientCertificateHandlerRegistered = false;
        _clientCertificateHandler = null;
        Release("directory-disposal-request", _sessionDirectoryOwnership.DisposeRequested);
        Release("native-controller", _control.Dispose);
        if (failures.Count != 0) throw new AggregateException("Browser session cleanup failed.", failures);
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs eventArgs)
    {
        _browserProcessExited.TrySetResult(eventArgs.BrowserProcessId);
        _sessionDirectoryOwnership.BrowserProcessExited();
    }

    private void RetireSessionDirectory()
    {
        if (_environment is not null)
        {
            _environment.BrowserProcessExited -= OnBrowserProcessExited;
            _environment = null;
        }
        ObserveDirectoryCleanup(
            Task.Run(() => _sessionDirectory.RetireAndTryCleanup()),
            "browser-session-directory-retire");
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(string sessionPath) =>
        CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: sessionPath,
            CcbUKeyWebView2Environment.CreateOptions());

    private static void ObserveDirectoryCleanup(Task task, string operation) =>
        BackgroundTaskObserver.Observe(
            task,
            operation,
            fault => CertificateLoginDiagnostics.Write(
                "background-task-error",
                new { operation = fault.Operation, exceptionType = fault.ExceptionType }));

    private void ReportInitializationPhase(string phase)
    {
        try
        {
            _initializationPhase?.Invoke(phase);
        }
        catch (Exception exception)
        {
            CertificateLoginDiagnostics.Write(
                "lifecycle-observation-phase-error",
                new { phase, exceptionType = exception.GetType().Name });
        }
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        var navigationId = checked((long)eventArgs.NavigationId);
        var pending = _pendingNavigationStart;
        _pendingNavigationStart = null;
        if (_navigationSessions.TryBindNavigationId(navigationId))
        {
            pending?.TrySetResult(navigationId);
            return;
        }

        pending?.TrySetException(new InvalidOperationException(
            "NavigationStarting did not belong to the pending physical navigation."));

        var targetUri = Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri)
            ? uri
            : null;
        var navigationStarting = new WebNavigationStartingEventArgs(
            navigationId,
            eventArgs.IsUserInitiated,
            targetUri);
        if (!_navigationSessions.TryBeginUntrackedNavigation(
                navigationId,
                navigationStarting.AuthenticationBindingTask))
        {
            navigationStarting.CompleteWithoutAuthenticationBinding();
            return;
        }

        try
        {
            NavigationStarting?.Invoke(this, navigationStarting);
        }
        catch
        {
            navigationStarting.CompleteWithoutAuthenticationBinding();
        }
        finally
        {
            navigationStarting.CompleteWithoutAuthenticationBinding();
        }
    }

    private void RegisterClientCertificateHandlerOnUiThread(WebClientCertificateRequestHandler handler)
    {
        _lifetime.ThrowIfDisposed();
        if (_control.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 尚未初始化。");
        }

        if (_clientCertificateHandlerRegistered)
        {
            if (!ReferenceEquals(_clientCertificateHandler, handler))
            {
                throw new InvalidOperationException("Client-certificate handler is already registered.");
            }

            return;
        }

        _clientCertificateHandler = handler;
        _control.CoreWebView2.ClientCertificateRequested += OnClientCertificateRequested;
        _clientCertificateHandlerRegistered = true;
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        var navigationId = checked((long)eventArgs.NavigationId);
        var isSuccess = eventArgs.IsSuccess;
        var errorStatus = eventArgs.WebErrorStatus.ToString();
        try
        {
            // WebView2 does not attach a NavigationId to ClientCertificateRequested.
            // Its controller events are serialized on this UI thread, so a request
            // belonging to this navigation must enter before its NavigationCompleted.
            // Holding this terminal until all entered deferrals finish makes the
            // single-active-session attribution provable. Requests arriving after
            // terminal begins or between sessions are denied below.
            if (await _navigationSessions.CompleteNavigationAsync(navigationId))
            {
                NavigationCompleted?.Invoke(
                    this,
                    new(navigationId, isSuccess, errorStatus));
            }
        }
        catch
        {
        }
    }

    private async void OnClientCertificateRequested(
        object? sender,
        CoreWebView2ClientCertificateRequestedEventArgs eventArgs)
    {
        CoreWebView2Deferral? deferral = null;
        IDisposable? certificateRequestLease = null;
        var deferralCompleted = false;
        try
        {
            deferral = eventArgs.GetDeferral();
            var belongsToActiveSession = _navigationSessions.TryEnterCertificateRequest(
                out certificateRequestLease,
                out var authenticationBindingTask);
            var handler = belongsToActiveSession
                ? _clientCertificateHandler ?? ((_, _) =>
                    ValueTask.FromResult(WebClientCertificateSelection.Deny()))
                : ((_, _) =>
                ValueTask.FromResult(WebClientCertificateSelection.Deny()));

            if (belongsToActiveSession)
            {
                await authenticationBindingTask.WaitAsync(_sessionToken);
            }

            await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
                () =>
                {
                    deferralCompleted = true;
                    return deferral.Complete;
                },
                () => MapClientCertificateRequest(eventArgs),
                handler,
                selection => eventArgs.SelectedCertificate = selection.SelectedCertificate?.NativeCertificate
                    as CoreWebView2ClientCertificate,
                () => eventArgs.SelectedCertificate = null,
                () => eventArgs.Handled = true,
                _sessionToken);
        }
        catch
        {
            // Session revocation or a released native controller can interrupt the
            // binding wait. Never let the async event callback escape; finally denies
            // selection and completes every acquired deferral exactly once.
        }
        finally
        {
            certificateRequestLease?.Dispose();
            if (!deferralCompleted)
            {
                try
                {
                    eventArgs.SelectedCertificate = null;
                    eventArgs.Handled = true;
                }
                catch
                {
                }

                try
                {
                    deferral?.Complete();
                }
                catch
                {
                }
            }
        }
    }

    private static WebClientCertificateRequest MapClientCertificateRequest(
        CoreWebView2ClientCertificateRequestedEventArgs eventArgs)
    {
        var port = eventArgs.Port is > 0 and <= 65535 ? eventArgs.Port : 443;
        var requestUri = new UriBuilder(Uri.UriSchemeHttps, eventArgs.Host, port).Uri;
        var certificates = eventArgs.MutuallyTrustedCertificates.Select(certificate =>
        {
            using var x509 = certificate.ToX509Certificate2();
            var identity = WebClientCertificateIdentity.FromCertificate(x509);
            return new WebClientCertificate(
                identity.Sha1Fingerprint,
                identity.Subject,
                identity.Issuer,
                certificate);
        });
        return new WebClientCertificateRequest(requestUri, eventArgs.IsProxy, certificates);
    }
}
