using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Core.Lifecycle;
using UsbWebLauncher.Windows.Devices;
using UsbWebLauncher.Windows.Authentication.NativePrompt;
using UsbWebLauncher.App.Updates;

namespace UsbWebLauncher.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private MainWindow? _window;
    private LauncherCoordinator? _coordinator;
    private LauncherApplicationRuntime? _runtime;
    private IClientCertificateAuthenticationRuntime? _authenticationRuntime;
    private WpfWebView2Control? _browserControl;
    private WebView2BrowserHost? _browserHost;
    private LauncherUiPresenter? _presenter;
    private ManualLauncherActions? _actions;
    private WpfDocumentsExporter? _documentsExporter;
    private WpfCurrentCompanyDisplay? _currentCompanyDisplay;
    private ApplicationLaunchOptions _launchOptions = new(false);
    private BankPinCredentialBinding? _pinCredentialBinding;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _initializing;
    private bool _retryInProgress;
    private BusinessSessionScope? _session;
    private BankRuntimeOwner? _runtimeOwner;
    private BankRuntimeCallbackGuard? _runtimeCallbackGuard;
    private long _runtimeRevision;
    private EventHandler<LauncherStateChangedEventArgs>? _coordinatorStateChangedHandler;
    private EventHandler<AuthenticationStateChangedEventArgs>? _authenticationStateChangedHandler;
    private EventHandler? _actionsStateChangedHandler;
    private EventHandler? _exportStateChangedHandler;
    private readonly ApplicationRuntimeCleanup _runtimeCleanup = new();
    private Task? _stopTask;
    private Task? _startTask;
    private IUsbKeyPresenceObserver? _keyPresenceObserver;
    private long _keyPresenceRevision;
    private readonly ApplicationExitWatchdog _exitWatchdog = new();
    private UpdatePresentationController? _updates;
    private LifecycleObservationReport? _lifecycleObservationReport;
    private string? _lifecycleObservationReportPath;
    private int _lifecycleObservationCycle;
    private PsbcClientCertificateDiagnostics? _psbcCertificateDiagnostics;
    private PsbcNativeWindowDiagnostics? _psbcNativeWindowDiagnostics;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _launchOptions = ApplicationLaunchOptions.Parse(e.Args);
            if (_launchOptions.IsHeadless)
            {
                Shutdown(await InstallerCommandRunner.Create().RunAsync(_launchOptions.InstallerCommand));
                return;
            }
            if (_launchOptions.ShowDependencyPreparation)
            {
                var preparation = new DependencyPreparationWindow();
                MainWindow = preparation;
                preparation.Closed += (_, _) => Shutdown();
                preparation.Show();
                return;
            }

            CertificateLoginDiagnostics.BeginRun();
            if (_launchOptions.PsbcCertificateDiagnosticsPath is { } diagnosticsPath)
            {
                _psbcCertificateDiagnostics =
                    new PsbcClientCertificateDiagnostics(diagnosticsPath);
                _psbcCertificateDiagnostics.BeginRun();
            }
            if (_launchOptions.PsbcNativeWindowDiagnosticsPath is { } windowDiagnosticsPath)
            {
                _psbcNativeWindowDiagnostics =
                    new PsbcNativeWindowDiagnostics(windowDiagnosticsPath);
                _psbcNativeWindowDiagnostics.BeginRun();
            }

            _window = new MainWindow();
            MainWindow = _window;
            _window.Closing += OnMainWindowClosing;
            _window.RetryRequested += OnRetryRequested;
            _window.ViewModel.BankFlowChangeRequested += OnBankFlowChangeRequested;
            _updates = new UpdatePresentationController(GitHubApplicationUpdateService.CreateDefault(ProductVersion.Display),
                _window.ViewModel, ProductVersion.Display, UpdatePresentationController.LaunchInteractiveInstaller,
                () => _window.Close());
            _window.ConfigureUpdates(_updates);
            _presenter = new LauncherUiPresenter(
                _window.ViewModel,
                new WpfUiDispatcher(Dispatcher),
                _window.ActivateForBrowser);

            _window.Show();
            // Independent of initialization and USB Key observation; never download on startup.
            if (!_launchOptions.IsLifecycleObservation)
            {
                ObserveBackground(
                    _updates.CheckOnStartupAsync(),
                    "startup-update-check");
            }
            await RunSerializedAsync(InitializeLauncherCoreAsync);

            if (_launchOptions.LifecycleObservationReportPath is { } observationReportPath &&
                !_shutdownStarted &&
                _window is not null)
            {
                await RunLifecycleObservationAsync(observationReportPath);
                if (!_shutdownStarted)
                {
                    _window.Title =
                        ApplicationLaunchOptions.LifecycleObservationReadyWindowTitle;
                }
            }
            else if (_launchOptions.UseEmptyDeviceMonitor &&
                !_shutdownStarted &&
                _coordinator is not null &&
                _window is not null)
            {
                _window.Title = ApplicationLaunchOptions.SmokeReadyWindowTitle;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CertificateLoginDiagnostics.Write(
                "startup-handler-error",
                new { exceptionType = exception.GetType().Name });
            ShowStartupError(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Do not synchronously invoke third-party cancellation callbacks on exit.
        ObserveBackground(_lifetime.CancelAsync(), "application-exit-cancellation");
        base.OnExit(e);
        _exitWatchdog.Dispose();
    }

    private async Task RunSerializedAsync(Func<Task> operation)
    {
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token);
            try
            {
                await operation();
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task InitializeLauncherCoreAsync()
    {
        if (_window is null || _presenter is null || _shutdownStarted)
        {
            return;
        }

        _initializing = true;
        BankRuntimeCallbackGuard? initializationGuard = null;
        _window.ViewModel.UpdateBusinessState(false, false, true, true);
        try
        {
            try
            {
                await DisposeRuntimeAsync();
            }
            finally
            {
                _runtimeCleanup.Reset();
            }
            if (_shutdownStarted) return;
            var bankFlow = _window.ViewModel.SelectedBankFlow;
            if (!bankFlow.IsConfigured)
            {
                return;
            }
            var session = new BusinessSessionScope(_lifetime.Token);
            var runtimeOwner = new BankRuntimeOwner(
                session,
                Interlocked.Increment(ref _runtimeRevision),
                bankFlow.Kind);
            var callbackGuard = new BankRuntimeCallbackGuard(
                runtimeOwner,
                () => _runtimeOwner,
                () => Volatile.Read(ref _runtimeRevision),
                () => _window?.ViewModel.SelectedBankFlow.Kind);
            var sessionToken = session.Token;
            _session = session;
            _runtimeOwner = runtimeOwner;
            _runtimeCallbackGuard = callbackGuard;
            initializationGuard = callbackGuard;
            if (bankFlow.Kind == BankFlowKind.PostalSavingsBank &&
                _psbcNativeWindowDiagnostics is { } nativeWindowDiagnostics)
            {
                ObserveBackground(
                    nativeWindowDiagnostics.ObserveAsync(sessionToken),
                    "psbc-native-window-diagnostics");
            }
            _presenter = new LauncherUiPresenter(_window.ViewModel, new WpfUiDispatcher(Dispatcher),
                _window.ActivateForBrowser, () => callbackGuard.IsCurrent);
            var browser = _window.CreateBrowserControl();

            var profilePath = Path.Combine(AppContext.BaseDirectory, bankFlow.ProfileFileName!);
            var profile = LauncherProfileJson.Parse(await File.ReadAllTextAsync(
                profilePath,
                sessionToken));
            sessionToken.ThrowIfCancellationRequested();
            var usbKeyFlowFactory = BankUsbKeyFlowFactory.CreateDefault(
                policy => _launchOptions.UseEmptyDeviceMonitor
                    ? new EmptyUsbDeviceMonitor()
                    : new WindowsUsbDeviceMonitor(policy),
                () => new PsbcUsbKeyProbeAggregator(
                    new WindowsPsbcUsbKeyEnvironmentQuery()));
            var usbKeyFlow = usbKeyFlowFactory.Create(bankFlow.Kind, profile);
            if (_keyPresenceObserver is null)
            {
                var presenceRevision = Interlocked.Increment(
                    ref _keyPresenceRevision);
                var observer = usbKeyFlow.CreatePresenceObserver(
                    status =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_shutdownStarted ||
                                presenceRevision != Volatile.Read(
                                    ref _keyPresenceRevision) ||
                                _window?.ViewModel.SelectedBankFlow.Kind != bankFlow.Kind)
                            {
                                return;
                            }
                            _window?.ViewModel.UpdateUsbKeyDetectionStatus(status);
                        }));
                    });
                _keyPresenceObserver = observer;
                // Presence is only a hint. Never start the session coordinator or
                // wait for hardware metadata on the UI/stop-initialization path.
                ObserveBackground(
                    Task.Run(() => observer.StartAsync(_lifetime.Token)),
                    "presence-observer-start");
            }

            var observationSessionDirectory = _launchOptions.LifecycleObservationReportPath is { } reportPath
                ? BankBrowserSessionDirectory.Create(
                    bankFlow.BrowserCapabilities,
                    LifecycleObservation.GetBrowserSessionRoot(reportPath))
                : null;
            _browserControl = new WpfWebView2Control(
                browser,
                _window.DownloadSettings,
                _window.ShowDownloadDirectoryError,
                observationSessionDirectory,
                sessionToken,
                _launchOptions.IsLifecycleObservation
                    ? RecordLifecycleObservationPhase
                    : null,
                bankFlow.BrowserCapabilities.EnablePsbcClientCertificateHandling
                    ? _psbcCertificateDiagnostics
                    : null,
                bankFlow.BrowserCapabilities);
            _browserHost = new WebView2BrowserHost(_browserControl);
            var composer = new LauncherApplicationComposer(
                new WindowsClientCertificateAuthenticationRuntimeFactory());
            var deviceMonitor = usbKeyFlow.CreateSessionMonitor(
                () => OnSessionDeviceRemoved(callbackGuard),
                callbackGuard);
            var composedRuntime = await composer.ComposeAsync(
                profile,
                deviceMonitor,
                _browserHost,
                bankFlow,
                sessionToken,
                usbKeyFlow.CoordinatorMatcher);
            // Shutdown can finish its ownership snapshot while composition is
            // awaiting an external dependency. Never publish a late runtime into
            // the already-cleared fields; release that local owner instead.
            if (_shutdownStarted || !callbackGuard.IsCurrent)
            {
                await composedRuntime.DisposeAsync();
                return;
            }
            _runtime = composedRuntime;
            _coordinator = _runtime.Launcher;
            var coordinator = _coordinator;
            _coordinatorStateChangedHandler = (sender, eventArgs) =>
                OnCoordinatorStateChanged(
                    callbackGuard,
                    coordinator,
                    sender,
                    eventArgs);
            coordinator.StateChanged += _coordinatorStateChangedHandler;

            _authenticationRuntime = _runtime.Authentication;
            if (_authenticationRuntime is not null)
            {
                var authenticationRuntime = _authenticationRuntime;
                var pinAuthenticationOptions =
                    profile.ClientCertificateAuthentication ??
                    throw new InvalidOperationException(
                        "认证运行时缺少客户端证书配置。");
                _pinCredentialBinding = BankPinCredentialBinding.Create(
                    authenticationRuntime.RuntimeKind,
                    pinAuthenticationOptions);
                _authenticationStateChangedHandler = (sender, eventArgs) =>
                    OnAuthenticationStateChanged(
                        callbackGuard,
                        authenticationRuntime,
                        sender,
                        eventArgs);
                authenticationRuntime.StateChanged +=
                    _authenticationStateChangedHandler;
                var localPinCommands = new LocalPinCommands(
                    () => new PinSetupDialog(_window, bankFlow.DisplayName),
                    authenticationRuntime.PinSetupService,
                    authenticationRuntime.BrokerPrerequisites);
                var pageLogin = authenticationRuntime.PinStore is { } pinStore &&
                    profile.ClientCertificateAuthentication is { } authenticationOptions
                    ? _runtime.BankComponents.CreateCcbPageCoordinator(
                        () => new CertificatePageLoginCoordinator(
                            pinStore,
                            authenticationOptions.CredentialTargetName,
                            new WpfCertificateLoginPage(
                                browser,
                                new Uri(profile.TargetUrl),
                                authenticationOptions,
                                sessionToken),
                            sessionToken: sessionToken))
                    : null;
                _actions = new ManualLauncherActions(_coordinator, localPinCommands,
                    authenticationRuntime.BrokerPrerequisites,
                    () => IsAuthenticationBusy(callbackGuard, authenticationRuntime),
                    authenticationRuntime.ManualControl, pageLogin,
                    () => callbackGuard.IsCurrent
                        ? authenticationRuntime.CurrentState.Stage
                        : AuthenticationStage.WaitingForDevice,
                    new WpfUiDispatcher(Dispatcher));
                _window.ViewModel.ConfigureLocalPinCommands(_actions);
                _ = await authenticationRuntime.BrokerPrerequisites.RefreshHealthAsync(
                    sessionToken);
                sessionToken.ThrowIfCancellationRequested();
                _presenter.PresentAuthentication(authenticationRuntime.CurrentState);
                await RefreshCurrentPinAsync(
                    callbackGuard,
                    authenticationRuntime,
                    _pinCredentialBinding);
                sessionToken.ThrowIfCancellationRequested();
            }

            _actions ??= new ManualLauncherActions(_coordinator, NullLocalPinCommands.Instance);
            var actions = _actions;
            _actionsStateChangedHandler = (sender, eventArgs) =>
                OnActionsStateChanged(
                    callbackGuard,
                    actions,
                    sender,
                    eventArgs);
            actions.StateChanged += _actionsStateChangedHandler;
            _window.ViewModel.ConfigureBusinessCommand(
                token => StartBusinessSessionAsync(
                    callbackGuard,
                    actions,
                    token));
            _window.ViewModel.ConfigureStopBusinessCommand(
                _ => StopBusinessSessionAsync(callbackGuard));
            _documentsExporter = new WpfDocumentsExporter(browser, _window.DownloadSettings);
            _currentCompanyDisplay = new WpfCurrentCompanyDisplay(browser,
                text => Dispatcher.BeginInvoke(new Action(() =>
                    callbackGuard.TryPublish(
                        () => _window.ViewModel.UpdateCurrentCompany(text)))));
            var exporter = _documentsExporter;
            _exportStateChangedHandler = (sender, eventArgs) =>
                OnExportStateChanged(
                    callbackGuard,
                    exporter,
                    sender,
                    eventArgs);
            exporter.StateChanged += _exportStateChangedHandler;
            _window.ViewModel.ConfigureDocumentsExportCommand(async (month, token) =>
            {
                if (!callbackGuard.IsCurrent) return;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
                await exporter.ExportAsync(month, linked.Token);
            });

            _presenter.Present(_coordinator.CurrentState);
            PresentBusinessState(callbackGuard);
        }
        catch (OperationCanceledException) when (_shutdownStarted || _lifetime.IsCancellationRequested || _session?.IsCurrent != true)
        {
            // Cancellation is a normal end of startup/reinitialization, including
            // when it was awaited by OnStartup's async-void event handler.
            return;
        }
        catch (Exception exception)
        {
            await DisposeRuntimeAsync();
            ShowStartupError(exception);
        }
        finally
        {
            _initializing = false;
            PresentBusinessState(initializationGuard);
        }
    }

    private async void OnBankFlowChangeRequested(object? sender, BankFlowChangedEventArgs eventArgs)
    {
        var window = _window;
        if (window is null || _shutdownStarted)
        {
            return;
        }

        var viewModel = window.ViewModel;
        viewModel.BeginBankFlowSwitch();
        var selectedFlow = eventArgs.Current;
        var runtimeOwner = _runtimeOwner;
        Interlocked.Increment(ref _runtimeRevision);
        var revocation = runtimeOwner?.Session.RevokeAsync() ??
            _session?.RevokeAsync() ??
            Task.CompletedTask;
        Interlocked.Increment(ref _keyPresenceRevision);

        try
        {
            await RunSerializedAsync(async () =>
            {
                await revocation;
                await DisposeKeyPresenceObserverAsync();
                if (_shutdownStarted || viewModel.SelectedBankFlow != selectedFlow)
                {
                    return;
                }

                await InitializeLauncherCoreAsync();
            });
        }
        catch (OperationCanceledException) when (_shutdownStarted || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CertificateLoginDiagnostics.Write(
                "bank-flow-switch-error",
                new
                {
                    bank = selectedFlow.Kind.ToString(),
                    exceptionType = exception.GetType().Name
                });
            ShowStartupError(exception);
        }
        finally
        {
            if (!_shutdownStarted && viewModel.SelectedBankFlow == selectedFlow)
            {
                viewModel.CompleteBankFlowSwitch(
                    selectedFlow.IsConfigured ? null : selectedFlow.UnavailableStatusText);
            }
        }
    }

    private async Task DisposeKeyPresenceObserverAsync()
    {
        Interlocked.Increment(ref _keyPresenceRevision);
        var observer = _keyPresenceObserver;
        _keyPresenceObserver = null;
        if (observer is not null)
        {
            await Task.Run(async () => await observer.DisposeAsync());
        }
    }

    private async Task RunLifecycleObservationAsync(string reportPath)
    {
        var tolerances = new LifecycleObservationTolerances();
        var browserSessionRoot = BankBrowserSessionDirectory.ResolveRoot(
            _window?.ViewModel.SelectedBankFlow.BrowserCapabilities ??
                BankBrowserCapabilities.ChinaConstructionBank,
            LifecycleObservation.GetBrowserSessionRoot(reportPath));
        var report = new LifecycleObservationReport
        {
            Tolerances = tolerances,
            InitialSessionDirectories =
                LifecycleObservation.CaptureSessionDirectories(browserSessionRoot)
        };
        _lifecycleObservationReport = report;
        _lifecycleObservationReportPath = reportPath;
        await LifecycleObservation.WriteReportAsync(reportPath, report);

        try
        {
            for (var cycleNumber = 1; cycleNumber <= tolerances.CycleCount; cycleNumber++)
            {
                _lifecycleObservationCycle = cycleNumber;
                var host = _browserHost ??
                    throw new InvalidOperationException("The browser host was not initialized.");
                var control = _browserControl ??
                    throw new InvalidOperationException("The browser control was not initialized.");
                var window = _window ??
                    throw new InvalidOperationException("The main window was not initialized.");
                var session = _session;
                if (session?.IsCurrent != true)
                    throw new InvalidOperationException("The business session owner was not current.");

                window.PrepareBrowserForLifecycleObservation();
                RecordLifecycleObservationPhase("browser-container-prepared");
                await host.InitializeAsync(session.Token)
                    .WaitAsync(
                        TimeSpan.FromSeconds(tolerances.BrowserInitializationTimeoutSeconds),
                        session.Token);
                var browserProcessId = control.BrowserProcessId ??
                    throw new InvalidOperationException("WebView2 did not expose its browser process.");
                var browserProcessExit = control.BrowserProcessExited;
                var cycle = new LifecycleObservationCycle
                {
                    Cycle = cycleNumber,
                    BrowserProcessId = browserProcessId,
                    Started = LifecycleObservation.CaptureSnapshot(browserSessionRoot)
                };
                report.Cycles.Add(cycle);
                await LifecycleObservation.WriteReportAsync(reportPath, report);

                try
                {
                    await DisposeRuntimeAsync().WaitAsync(
                        TimeSpan.FromSeconds(tolerances.BrowserExitTimeoutSeconds),
                        _lifetime.Token);
                }
                catch (Exception exception)
                {
                    report.Failures.Add(
                        $"Cycle {cycleNumber} runtime cleanup failed: {exception.GetType().Name}.");
                }

                try
                {
                    cycle.ExitedBrowserProcessId = await browserProcessExit.WaitAsync(
                        TimeSpan.FromSeconds(tolerances.BrowserExitTimeoutSeconds),
                        _lifetime.Token);
                    cycle.BrowserProcessExitedWithinTimeout = true;
                }
                catch (TimeoutException)
                {
                    cycle.BrowserProcessExitedWithinTimeout = false;
                }

                await LifecycleObservation.WaitForDirectoryCleanupAsync(
                    browserSessionRoot,
                    TimeSpan.FromSeconds(tolerances.DirectoryCleanupTimeoutSeconds),
                    _lifetime.Token);
                cycle.Stopped = LifecycleObservation.CaptureSnapshot(browserSessionRoot);
                await LifecycleObservation.WriteReportAsync(reportPath, report);

                if (cycleNumber < tolerances.CycleCount)
                {
                    await RunSerializedAsync(InitializeLauncherCoreAsync)
                        .WaitAsync(
                            TimeSpan.FromSeconds(tolerances.BrowserInitializationTimeoutSeconds),
                            _lifetime.Token);
                }
            }
        }
        catch (Exception exception)
        {
            var lastPhase = report.Phases.LastOrDefault()?.Name ?? "not-started";
            report.Failures.Add(
                $"Lifecycle observation failed at {lastPhase}: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                await DisposeRuntimeAsync().WaitAsync(
                    TimeSpan.FromSeconds(tolerances.BrowserExitTimeoutSeconds));
            }
            catch (Exception exception)
            {
                report.Failures.Add(
                    $"Final runtime cleanup failed: {exception.GetType().Name}.");
            }

            try
            {
                await LifecycleObservation.WaitForDirectoryCleanupAsync(
                    browserSessionRoot,
                    TimeSpan.FromSeconds(tolerances.DirectoryCleanupTimeoutSeconds),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                report.Failures.Add(
                    $"Final browser directory cleanup failed: {exception.GetType().Name}.");
            }

            report.Failures.AddRange(LifecycleObservation.Validate(report));
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            report.Status = report.Failures.Count == 0 ? "passed" : "failed";
            await LifecycleObservation.WriteReportAsync(reportPath, report);
        }
    }

    private void RecordLifecycleObservationPhase(string phase)
    {
        var report = _lifecycleObservationReport;
        var reportPath = _lifecycleObservationReportPath;
        if (report is null || reportPath is null) return;

        report.Phases.Add(new LifecycleObservationPhase(
            _lifecycleObservationCycle,
            phase,
            DateTimeOffset.UtcNow));
        LifecycleObservation.WriteReport(reportPath, report);
    }

    private void OnSessionDeviceRemoved(BankRuntimeCallbackGuard callbackGuard)
    {
        if (!callbackGuard.IsCurrent) return;
        var session = callbackGuard.Owner.Session;
        // Revoke on the notification thread before the old coordinator can see a
        // replacement Key. Never await cleanup here: it owns this device monitor.
        ObserveBackground(session.RevokeAsync(), "session-device-removal-revocation");
        CertificateLoginDiagnostics.Write("session-key-removed", new { session.Id });
        ObserveBackground(
            Dispatcher.InvokeAsync(async () =>
            {
                if (_shutdownStarted || !callbackGuard.OwnsCurrentRuntime) return;
                await StopBusinessSessionAsync(callbackGuard);
            }).Task.Unwrap(),
            "session-device-removal-stop");
    }

    private void OnCoordinatorStateChanged(
        BankRuntimeCallbackGuard callbackGuard,
        LauncherCoordinator coordinator,
        object? sender,
        LauncherStateChangedEventArgs eventArgs)
    {
        if (!callbackGuard.IsCurrent ||
            !ReferenceEquals(sender, coordinator))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            callbackGuard.TryPublish(() =>
            {
                if (!ReferenceEquals(coordinator, _coordinator)) return;
                _presenter?.Present(eventArgs.State);
                _currentCompanyDisplay?.SetDevicePresent(eventArgs.State.IsUsbPresent);
            });
        });
    }

    private void OnAuthenticationStateChanged(
        BankRuntimeCallbackGuard callbackGuard,
        IClientCertificateAuthenticationRuntime authenticationRuntime,
        object? sender,
        AuthenticationStateChangedEventArgs eventArgs)
    {
        if (!callbackGuard.IsCurrent ||
            !ReferenceEquals(sender, authenticationRuntime))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            callbackGuard.TryPublish(() =>
            {
                if (!ReferenceEquals(
                        authenticationRuntime,
                        _authenticationRuntime))
                {
                    return;
                }

                _presenter?.PresentAuthentication(eventArgs.State);
                _actions?.RefreshPageLogin();
                _ = RefreshCurrentPinAsync(
                    callbackGuard,
                    authenticationRuntime,
                    _pinCredentialBinding);
            });
        });
    }

    private static bool IsAuthenticationBusy(
        BankRuntimeCallbackGuard callbackGuard,
        IClientCertificateAuthenticationRuntime authenticationRuntime) =>
        callbackGuard.IsCurrent &&
        authenticationRuntime.CurrentState.Stage is
            AuthenticationStage.ValidatingCompatibility or
            AuthenticationStage.UnlockingToken or
            AuthenticationStage.AwaitingCertificate or
            AuthenticationStage.Authenticating;

    private void OnActionsStateChanged(
        BankRuntimeCallbackGuard callbackGuard,
        ManualLauncherActions actions,
        object? sender,
        EventArgs eventArgs)
    {
        if (!callbackGuard.IsCurrent ||
            !ReferenceEquals(sender, actions))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            callbackGuard.TryPublish(() =>
            {
                if (!ReferenceEquals(actions, _actions)) return;
                PresentBusinessState(callbackGuard);
                if (_authenticationRuntime is { } authenticationRuntime)
                {
                    _ = RefreshCurrentPinAsync(
                        callbackGuard,
                        authenticationRuntime,
                        _pinCredentialBinding);
                }
            });
        });
    }

    private async Task RefreshCurrentPinAsync(
        BankRuntimeCallbackGuard callbackGuard,
        IClientCertificateAuthenticationRuntime authenticationRuntime,
        BankPinCredentialBinding? credentialBinding)
    {
        var window = _window;
        var pinStore = authenticationRuntime.PinStore;
        if (window is null ||
            pinStore is null ||
            credentialBinding is null ||
            credentialBinding.RuntimeKind != authenticationRuntime.RuntimeKind ||
            !callbackGuard.IsCurrent ||
            !ReferenceEquals(authenticationRuntime, _authenticationRuntime))
        {
            return;
        }

        try
        {
            var status = await credentialBinding.ReadStatusAsync(
                    pinStore,
                    callbackGuard.Owner.Session.Token)
                .ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
                callbackGuard.TryPublish(() =>
                {
                    if (ReferenceEquals(
                            authenticationRuntime,
                            _authenticationRuntime))
                    {
                        window.ViewModel.UpdateCurrentPinStatus(status);
                    }
                }));
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested ||
            !callbackGuard.IsCurrent)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
                callbackGuard.TryPublish(() =>
                {
                    if (ReferenceEquals(
                            authenticationRuntime,
                            _authenticationRuntime))
                    {
                        window.ViewModel.UpdateCurrentPinStatus(
                            PinCredentialStatus.ReadFailed);
                    }
                }));
        }
    }

    private void OnExportStateChanged(
        BankRuntimeCallbackGuard callbackGuard,
        WpfDocumentsExporter exporter,
        object? sender,
        EventArgs eventArgs)
    {
        if (!callbackGuard.IsCurrent ||
            !ReferenceEquals(sender, exporter))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            callbackGuard.TryPublish(() =>
            {
                if (!ReferenceEquals(exporter, _documentsExporter)) return;
                _window?.ViewModel.UpdateExportState(
                    exporter.IsBusy,
                    exporter.Message);
            });
        });
    }

    private void PresentBusinessState(
        BankRuntimeCallbackGuard? callbackGuard = null)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (callbackGuard is not null && !callbackGuard.IsCurrent) return;
            _window?.ViewModel.UpdateBusinessState(
                initialized: _actions is not null && !_initializing && !_shutdownStarted,
                started: _actions?.HasStarted == true,
                awaitingExplicitStart: _actions?.RequiresExplicitStart != false,
                operationBusy: _initializing || _shutdownStarted || _actions?.IsBusy == true,
                message: _actions?.Message);
        }));
    }

    private async Task StartBusinessSessionAsync(
        BankRuntimeCallbackGuard callbackGuard,
        ManualLauncherActions actions,
        CancellationToken token)
    {
        if (_shutdownStarted || _window?.ViewModel.UpdateInstallInProgress == true ||
            !callbackGuard.IsCurrent ||
            !ReferenceEquals(actions, _actions))
        {
            return;
        }

        var session = callbackGuard.Owner.Session;
        _stopTask = null;
        _window!.ViewModel.UpdateSessionState(BusinessSessionState.Active);
        CertificateLoginDiagnostics.Write("session-start", new { session.Id });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        try
        {
            _startTask = actions.StartBusinessAsync(linked.Token);
            await _startTask;
            if (callbackGuard.IsCurrent &&
                ReferenceEquals(actions, _actions) &&
                !actions.HasStarted)
            {
                _window.ViewModel.UpdateSessionState(BusinessSessionState.Idle);
            }
        }
        catch (OperationCanceledException) when (!callbackGuard.IsCurrent) { }
    }

    private Task StopBusinessSessionAsync(
        BankRuntimeCallbackGuard callbackGuard)
    {
        if (!callbackGuard.OwnsCurrentRuntime)
        {
            return Task.CompletedTask;
        }

        return _stopTask ??= StopBusinessSessionCoreAsync(callbackGuard);
    }

    private async Task StopBusinessSessionCoreAsync(
        BankRuntimeCallbackGuard callbackGuard)
    {
        if (_shutdownStarted || !callbackGuard.OwnsCurrentRuntime) return;
        // Arm before UI notifications, cancellation callbacks, or native cleanup.
        // Its finally disposal affects only this stop, never the close watchdog.
        using var budget = new BusinessSessionStopDeadline();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var session = callbackGuard.Owner.Session;
        Interlocked.Increment(ref _runtimeRevision);
        _window!.ViewModel.UpdateSessionState(BusinessSessionState.Stopping);
        CertificateLoginDiagnostics.Write("session-stop", new { session.Id });
        // CancelAsync invalidates the token immediately without running callbacks on this UI stack.
        var cancellation = session.RevokeAsync();
        try
        {
            await Task.Yield();
            await Task.WhenAll(cancellation, DisposeRuntimeAsync()).WaitAsync(budget.Token);
            if (_shutdownStarted) return;
            _window.ViewModel.ResetSessionPresentation();
            // Rebuild only idle dependencies; no navigation or device enumeration occurs here.
            await RunSerializedAsync(async () =>
            {
                budget.Token.ThrowIfCancellationRequested();
                if (_shutdownStarted || _window.ViewModel.SessionState == BusinessSessionState.StopFailed) return;
                await InitializeLauncherCoreAsync();
            }).WaitAsync(budget.Token);
            if (_shutdownStarted) return;
            if (_actions is null) throw new InvalidOperationException("Idle session initialization failed.");
            _window.ViewModel.UpdateSessionState(BusinessSessionState.Idle);
            CertificateLoginDiagnostics.Write("session-stopped", new
            {
                session.Id,
                elapsedMs = elapsed.ElapsedMilliseconds
            });
        }
        catch
        {
            ObserveBackground(
                session.RevokeAsync(),
                "session-stop-failure-revocation");
            if (!_shutdownStarted) _window.ViewModel.UpdateSessionState(BusinessSessionState.StopFailed);
            CertificateLoginDiagnostics.Write("session-stop-failed", new
            {
                session.Id,
                elapsedMs = elapsed.ElapsedMilliseconds,
                timedOut = budget.Token.IsCancellationRequested
            });
        }
    }

    private async void OnRetryRequested(object? sender, EventArgs eventArgs)
    {
        if (_initializing || _retryInProgress || _shutdownStarted ||
            _window?.ViewModel.UpdateInstallInProgress == true ||
            _window?.ViewModel.SessionState is BusinessSessionState.Stopping or BusinessSessionState.StopFailed) return;
        var retryInitialization = _actions is null;
        var retryGuard = _runtimeCallbackGuard;
        var retryActions = _actions;
        if (!retryInitialization &&
            (retryGuard?.IsCurrent != true || retryActions is null))
        {
            return;
        }

        _retryInProgress = true;
        try
        {
            await RunSerializedAsync(async () =>
            {
                if (_shutdownStarted || _window?.ViewModel.UpdateInstallInProgress == true) return;
                if (retryInitialization)
                    await InitializeLauncherCoreAsync();
                else if (retryGuard?.IsCurrent == true &&
                    retryActions is not null &&
                    ReferenceEquals(retryActions, _actions))
                    await StartBusinessSessionAsync(
                        retryGuard,
                        retryActions,
                        retryGuard.Owner.Session.Token);
            });
        }
        catch (OperationCanceledException) when (
            retryGuard?.IsCurrent != true ||
            _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CertificateLoginDiagnostics.Write(
                "retry-handler-error",
                new { exceptionType = exception.GetType().Name });
            ShowStartupError(exception);
        }
        finally { _retryInProgress = false; }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_shutdownComplete)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _exitWatchdog.Arm();
        _window?.ViewModel.UpdateSessionState(BusinessSessionState.Closing);
        Interlocked.Increment(ref _runtimeRevision);
        var session = _runtimeOwner?.Session ?? _session;
        if (session is not null)
        {
            ObserveBackground(
                session.RevokeAsync(),
                "application-close-session-revocation");
        }
        ObserveBackground(
            _lifetime.CancelAsync(),
            "application-close-lifetime-cancellation");
        CertificateLoginDiagnostics.Write("application-closing");

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () => await CompleteShutdownAsync()));
    }

    private async Task CompleteShutdownAsync()
    {
        var sequence = new ApplicationShutdownSequence(
            CleanupRuntimeForShutdownAsync,
            CloseMainWindowAfterCleanup,
            exitCode => Shutdown(exitCode), _exitWatchdog.RemainingTime);
        await sequence.RunAsync();
    }

    private async Task CleanupRuntimeForShutdownAsync()
    {
        if (_window is not null)
        {
            _window.RetryRequested -= OnRetryRequested;
            _window.ViewModel.BankFlowChangeRequested -= OnBankFlowChangeRequested;
        }
        var presence = _keyPresenceObserver;
        _keyPresenceObserver = null;
        Interlocked.Increment(ref _keyPresenceRevision);
        var updates = _updates;
        _updates = null;
        var runtimeCleanup = CaptureTask(DisposeRuntimeAsync);
        var releasePresence = CaptureTask(() => presence is null
            ? Task.CompletedTask
            : Task.Run(async () => await presence.DisposeAsync()));
        var releaseUpdates = CaptureTask(() =>
            updates?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await CleanupStageRunner.RunAsync(
            [
                AwaitStage("runtime", runtimeCleanup),
                AwaitStage("presence-observer", releasePresence),
                AwaitStage("update-presentation", releaseUpdates)
            ],
            ObserveCleanupStage);
    }

    private void CloseMainWindowAfterCleanup()
    {
        _shutdownComplete = true;
        if (_window is not null)
        {
            _window.Closing -= OnMainWindowClosing;
            _window.Close();
        }
    }

    private void ShowStartupError(Exception exception)
    {
        _window?.ViewModel.UpdateBusinessState(false, false, true, false);
        _window?.ViewModel.UpdateState(new LauncherState(
            LauncherStage.Error,
            false,
            "启动失败，可以重试。",
            exception.Message));
    }

    private Task DisposeRuntimeAsync()
    {
        return _runtimeCleanup.RunAsync(
            CaptureRuntimeCleanup,
            ObserveCleanupStage);
    }

    private ApplicationRuntimeCleanupSnapshot CaptureRuntimeCleanup()
    {
        var session = _session;
        var runtimeOwner = _runtimeOwner;
        var runtime = _runtime;
        var browser = _browserControl;
        var host = _browserHost;
        var actions = _actions;
        var exporter = _documentsExporter;
        var start = _startTask;
        var display = _currentCompanyDisplay;
        var coordinator = _coordinator;
        var authenticationRuntime = _authenticationRuntime;
        var coordinatorStateChangedHandler = _coordinatorStateChangedHandler;
        var authenticationStateChangedHandler = _authenticationStateChangedHandler;
        var actionsStateChangedHandler = _actionsStateChangedHandler;
        var exportStateChangedHandler = _exportStateChangedHandler;
        if (runtimeOwner is not null || session is not null)
        {
            Interlocked.Increment(ref _runtimeRevision);
        }

        _runtimeOwner = null;
        _runtimeCallbackGuard = null;
        _currentCompanyDisplay = null;
        if (exporter is not null && exportStateChangedHandler is not null)
            exporter.StateChanged -= exportStateChangedHandler;
        if (actions is not null && actionsStateChangedHandler is not null)
            actions.StateChanged -= actionsStateChangedHandler;
        if (coordinator is not null && coordinatorStateChangedHandler is not null)
            coordinator.StateChanged -= coordinatorStateChangedHandler;
        if (authenticationRuntime is not null &&
            authenticationStateChangedHandler is not null)
        {
            authenticationRuntime.StateChanged -=
                authenticationStateChangedHandler;
        }

        _coordinatorStateChangedHandler = null;
        _authenticationStateChangedHandler = null;
        _actionsStateChangedHandler = null;
        _exportStateChangedHandler = null;
        _documentsExporter = null;
        _actions = null;
        _authenticationRuntime = null;
        _runtime = null;
        _coordinator = null;
        _browserHost = null;
        _browserControl = null;
        _startTask = null;
        _pinCredentialBinding = null;

        // Start independent release work before awaiting any owner so a blocked or
        // failing stage cannot prevent later captured owners from beginning cleanup.
        var stages = new List<CleanupStage>
        {
            AwaitStage(
                "cancellation",
                CaptureTask(() => session?.RevokeAsync() ?? Task.CompletedTask)),
            AwaitStage(
                "display",
                CaptureTask(() =>
                {
                    display?.Dispose();
                    return Task.CompletedTask;
                })),
            AwaitStage(
                "detach",
                CaptureTask(() =>
                {
                    _window?.DetachBrowserControl();
                    return Task.CompletedTask;
                }))
        };
        if (exporter is not null)
        {
            stages.Add(AwaitStage(
                "exports",
                CaptureTask(() => exporter.DisposeAsync().AsTask())));
        }
        if (actions is not null)
        {
            stages.Add(AwaitStage(
                "actions",
                CaptureTask(() => actions.DisposeAsync().AsTask())));
        }
        if (runtime is not null)
        {
            stages.Add(AwaitStage(
                "runtime",
                CaptureTask(() => Task.Run(async () => await runtime.DisposeAsync()))));
        }
        else if (host is not null)
        {
            stages.Add(AwaitStage(
                "host",
                CaptureTask(() => host.DisposeAsync().AsTask())));
        }
        if (browser is not null)
        {
            stages.Add(AwaitStage(
                "browser",
                CaptureTask(() => browser.DisposeAsync().AsTask())));
        }
        if (start is not null)
        {
            stages.Add(AwaitStage(
                "start",
                CaptureTask(async () =>
                {
                    try
                    {
                        await start;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                })));
        }

        return new ApplicationRuntimeCleanupSnapshot(
            session,
            stages,
            () =>
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
            });
    }

    private static Task CaptureTask(Func<Task> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static CleanupStage AwaitStage(string name, Task task) =>
        new(name, () => new ValueTask(task));

    private static void ObserveCleanupStage(CleanupStageObservation observation)
    {
        var eventName = observation.Status switch
        {
            CleanupStageStatus.Started => "session-cleanup-start",
            CleanupStageStatus.Completed => "session-cleanup-complete",
            CleanupStageStatus.Failed => "session-cleanup-error",
            _ => "session-cleanup-observation"
        };
        CertificateLoginDiagnostics.Write(eventName, new
        {
            stage = observation.Stage,
            elapsedMs = (long)observation.Elapsed.TotalMilliseconds,
            exceptionType = observation.ExceptionType
        });
    }

    private static void ObserveBackground(Task task, string operation) =>
        BackgroundTaskObserver.Observe(
            task,
            operation,
            fault => CertificateLoginDiagnostics.Write(
                "background-task-error",
                new { operation = fault.Operation, exceptionType = fault.ExceptionType }));
}
