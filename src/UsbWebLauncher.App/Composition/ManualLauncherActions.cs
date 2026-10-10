using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Composition;

public sealed class ManualLauncherActions : ILocalPinCommands, INativePromptBrokerCommands, IAsyncDisposable
{
    private readonly LauncherCoordinator _launcher;
    private readonly ILocalPinCommands _preparation;
    private readonly INativePromptBrokerCommands _broker;
    private readonly INativePromptBrokerPrerequisites _prerequisites;
    private readonly IManualAuthenticationControl? _authentication;
    private readonly Func<bool> _isAuthenticationBusy;
    private readonly CertificatePageLoginCoordinator? _pageLogin;
    private readonly Func<AuthenticationStage>? _authenticationStage;
    private readonly IUiDispatcher? _uiDispatcher;
    private readonly object _pageLoginSync = new();
    private bool _pageAttemptArmed;
    private bool _pageDeviceSeen;
    private bool _pageAuthenticationSeen;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private volatile bool _started;
    private volatile bool _awaitingStart = true;
    private volatile bool _busy;
    private int _disposed;

    public ManualLauncherActions(LauncherCoordinator launcher, ILocalPinCommands preparation,
        INativePromptBrokerPrerequisites? prerequisites = null, Func<bool>? isAuthenticationBusy = null,
        IManualAuthenticationControl? authentication = null,
        CertificatePageLoginCoordinator? pageLogin = null,
        Func<AuthenticationStage>? authenticationStage = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _launcher = launcher;
        _preparation = preparation;
        _broker = preparation as INativePromptBrokerCommands ?? NullNativePromptBrokerCommands.Instance;
        _prerequisites = prerequisites ?? NullNativePromptBrokerPrerequisites.Instance;
        _isAuthenticationBusy = isAuthenticationBusy ?? (() => false);
        _authentication = authentication;
        _pageLogin = pageLogin;
        _authenticationStage = authenticationStage;
        _uiDispatcher = uiDispatcher;
        _broker.StateChanged += OnStateChanged;
        if (_authentication is not null) _authentication.StateChanged += OnStateChanged;
        if (_pageLogin is not null)
        {
            _pageLogin.StateChanged += OnPageLoginStateChanged;
            _launcher.StateChanged += OnLauncherStateChanged;
        }
    }

    public event EventHandler? StateChanged;
    public bool HasStarted => _started;
    public bool RequiresExplicitStart => _awaitingStart || _authentication?.RequiresExplicitStart == true;
    public bool IsBusy => _busy;
    private string? _message;
    public string? Message { get => _message ?? _pageLogin?.Message; private set => _message = value; }
    public NativePromptBrokerPrerequisiteState CurrentState => _broker.CurrentState;

    public void RefreshPageLogin()
    {
        lock (_pageLoginSync)
        {
            if (_pageLogin is null || !_pageAttemptArmed || Volatile.Read(ref _disposed) != 0) return;
            var state = _launcher.CurrentState;
            var authentication = _authenticationStage?.Invoke() ?? AuthenticationStage.Ready;
            CertificateLoginDiagnostics.Write("trigger", new
            {
                Armed = _pageAttemptArmed,
                Launcher = state.Stage.ToString(),
                Usb = state.IsUsbPresent,
                Authentication = authentication.ToString(),
                RequiresExplicitStart
            });
            if (state.IsUsbPresent) _pageDeviceSeen = true;
            if (authentication is AuthenticationStage.ValidatingCompatibility or AuthenticationStage.UnlockingToken or
                AuthenticationStage.AwaitingCertificate or AuthenticationStage.Authenticating or AuthenticationStage.Ready)
                _pageAuthenticationSeen = true;
            if (RequiresExplicitStart || state.Stage == LauncherStage.Error ||
                (_pageDeviceSeen && !state.IsUsbPresent) ||
                authentication is AuthenticationStage.Failed or AuthenticationStage.Incompatible or AuthenticationStage.RequiresPinSetup ||
                ((_pageDeviceSeen || _pageAuthenticationSeen) && authentication == AuthenticationStage.WaitingForDevice))
            {
                CancelPageLogin();
                _awaitingStart = true;
                Publish();
                return;
            }
            if (state.Stage == LauncherStage.Ready && state.IsUsbPresent && authentication == AuthenticationStage.Ready)
                _ = _pageLogin.ObserveAsync();
        }
    }

    public Task StartBusinessAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        if (_started && !RequiresExplicitStart && !_launcher.CurrentState.CanRetry) return;
        if (_prerequisites.CurrentState.IsRequired && !await _prerequisites.RefreshHealthAsync(token))
        {
            await PauseAsync(token);
            Message = "请先点击“安装环境”，完成后再点击“启动业务”。";
            return;
        }

        if (_authentication is not null) await _authentication.AllowBusinessAsync(token);
        _awaitingStart = false;
        Message = null;
        lock (_pageLoginSync)
        {
            _pageDeviceSeen = false;
            _pageAuthenticationSeen = false;
            _pageAttemptArmed = _pageLogin is not null;
            CertificateLoginDiagnostics.Write("attempt-begin", new { Armed = _pageAttemptArmed });
            _pageLogin?.BeginAttempt();
        }
        if (!_started)
        {
            _started = true;
            Publish();
            await _launcher.StartAsync(token);
        }
        else if (_launcher.CurrentState.CanRetry)
        {
            await _launcher.RetryAsync(token);
        }
        else
        {
            await _launcher.ResumeNavigationAfterAuthenticationSetupAsync(token);
        }
        RefreshPageLogin();
    }, cancellationToken);

    public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(_preparation.ValidateAndSaveAsync, cancellationToken);
    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(_preparation.ResetAsync, cancellationToken);
    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(_preparation.DeleteAsync, cancellationToken);
    public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(_broker.InstallNativePromptBrokerAsync, cancellationToken);
    public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
        PrepareAsync(_broker.UninstallNativePromptBrokerAsync, cancellationToken);

    private Task PrepareAsync(Func<CancellationToken, Task> prepare, CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            await PauseAsync(token);
            // A device callback may have begun authentication while PauseAsync
            // was waiting for the authentication gate. Never maintain it in flight.
            if (_isAuthenticationBusy()) return;
            await prepare(token);
            Message = _started ? "准备操作已结束，请点击“启动业务”继续。" : null;
        }, cancellationToken);

    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        _awaitingStart = true;
        lock (_pageLoginSync) CancelPageLogin();
        if (_authentication is not null) await _authentication.PauseAsync(cancellationToken);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || _isAuthenticationBusy()) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            if (!await _operation.WaitAsync(0, linked.Token)) return;
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || _isAuthenticationBusy()) return;
                _busy = true;
                Message = null;
                Publish();
                await action(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                _awaitingStart = true;
                lock (_pageLoginSync) CancelPageLogin();
            }
            catch
            {
                await PauseAsync(CancellationToken.None);
                Message = "操作未完成，请检查状态后重试。";
            }
            finally
            {
                _busy = false;
                _operation.Release();
                Publish();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        RefreshPageLogin();
        Publish();
    }
    private void OnPageLoginStateChanged(object? sender, EventArgs e) => Publish();
    private void OnLauncherStateChanged(object? sender, LauncherStateChangedEventArgs e) => RefreshPageLogin();
    private void CancelPageLogin()
    {
        CertificateLoginDiagnostics.Write("attempt-cancel", new { Armed = _pageAttemptArmed });
        _pageAttemptArmed = false;
        _pageDeviceSeen = false;
        _pageAuthenticationSeen = false;
        _pageLogin?.Cancel();
    }
    private void Publish()
    {
        // Authentication and page completion can arrive on worker threads. WPF
        // command subscribers must run on the UI dispatcher, just like status text.
        if (_uiDispatcher is not null) _uiDispatcher.Post(PublishOnUiThread);
        else PublishOnUiThread();
    }

    private void PublishOnUiThread()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var subscribers = StateChanged;
        if (subscribers is null) return;
        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception exception)
            {
                // A presentation failure must not hide status from later subscribers
                // or turn a completed page operation into another login attempt.
                CertificateLoginDiagnostics.Write("presentation-notification-failed", new
                {
                    ExceptionType = exception.GetType().Name
                });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        lock (_pageLoginSync) CancelPageLogin();
        await _operation.WaitAsync();
        _operation.Release();
        _broker.StateChanged -= OnStateChanged;
        if (_authentication is not null) _authentication.StateChanged -= OnStateChanged;
        if (_pageLogin is not null)
        {
            _launcher.StateChanged -= OnLauncherStateChanged;
            _pageLogin.StateChanged -= OnPageLoginStateChanged;
            await _pageLogin.DisposeAsync();
        }
        _lifetime.Dispose();
        // Late UI commands are rejected by _disposed; do not dispose a semaphore
        // that a command already entering RunAsync may still be awaiting.
    }
}
