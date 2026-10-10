using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ManualLauncherActionsTests
{
    [Fact]
    public async Task PageLogin_OnePresentationSubscriberCannotBlockRemainingStatusSubscribers()
    {
        await using var launcher = Create(new Monitor(true), new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", new LoginPage());
        await using var actions = new ManualLauncherActions(launcher, new Preparation(), pageLogin: login);
        actions.StateChanged += (_, _) => throw new InvalidOperationException("synthetic presentation failure");
        string? observed = null;
        actions.StateChanged += (_, _) => observed = actions.Message;
        login.BeginAttempt();
        await Task.Run(login.ObserveAsync);
        Assert.Contains("提交", observed);
    }

    [Fact]
    public async Task PageLogin_BackgroundCompletionPublishesStatusWithRealWpfCommandSubscribers()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await using var launcher = Create(new Monitor(true), new Browser());
                    await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", new LoginPage());
                    await using var actions = new ManualLauncherActions(launcher, new Preparation(), pageLogin: login,
                        uiDispatcher: new WpfUiDispatcher(dispatcher));
                    var viewModel = new LauncherViewModel(actions);
                    var button = new System.Windows.Controls.Button { Command = viewModel.InstallNativePromptBrokerCommand };
                    string? observed = null;
                    var notificationsOnUi = true;
                    actions.StateChanged += (_, _) =>
                    {
                        notificationsOnUi &= dispatcher.CheckAccess();
                        observed = actions.Message;
                    };
                    login.BeginAttempt();
                    await Task.Run(login.ObserveAsync);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.Contains("提交", observed);
                    Assert.True(notificationsOnUi);
                    GC.KeepAlive(button);
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task PageLogin_CancelledBusinessStartCannotSubmitOnLaterReadyCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var monitor = new Monitor(false)
        {
            OnStart = () =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        var page = new LoginPage();
        await using var launcher = Create(monitor, new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(), pageLogin: login);

        await actions.StartBusinessAsync(cancellation.Token);
        await monitor.PublishAsync(true);
        await login.ObserveAsync();

        Assert.Equal(0, page.Submissions);
        Assert.True(actions.RequiresExplicitStart);
    }

    [Fact]
    public async Task PageLogin_WaitsForExplicitStartAndUsb_ThenSubmitsOnlyOnce()
    {
        var monitor = new Monitor(false);
        var page = new LoginPage();
        await using var launcher = Create(monitor, new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(), pageLogin: login);
        await actions.ValidateAndSaveAsync();
        Assert.Equal(0, page.Submissions);
        await actions.StartBusinessAsync();
        Assert.Equal(0, page.Submissions);
        Assert.False(actions.RequiresExplicitStart);
        await monitor.PublishAsync(true);
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
        await actions.StartBusinessAsync();
        await monitor.PublishAsync(true);
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
    }

    [Fact]
    public async Task PageLogin_RemovalCancelsAndReinsertionNeedsNewExplicitStart()
    {
        var monitor = new Monitor(true);
        var page = new LoginPage();
        var stage = AuthenticationStage.Authenticating;
        await using var launcher = Create(monitor, new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(),
            pageLogin: login, authenticationStage: () => stage);
        await actions.StartBusinessAsync();
        Assert.Equal(0, page.Submissions);
        await monitor.PublishAsync(false);
        Assert.True(actions.RequiresExplicitStart);
        stage = AuthenticationStage.Ready;
        await monitor.PublishAsync(true);
        actions.RefreshPageLogin();
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        await actions.StartBusinessAsync();
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
    }

    [Theory]
    [InlineData(AuthenticationStage.Failed)]
    [InlineData(AuthenticationStage.Incompatible)]
    [InlineData(AuthenticationStage.RequiresPinSetup)]
    public async Task PageLogin_AuthenticationFailureCancelsUntilExplicitRestart(AuthenticationStage failed)
    {
        var stage = AuthenticationStage.Authenticating;
        var page = new LoginPage();
        await using var launcher = Create(new Monitor(true), new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(),
            pageLogin: login, authenticationStage: () => stage);
        await actions.StartBusinessAsync();
        stage = failed;
        actions.RefreshPageLogin();
        Assert.True(actions.RequiresExplicitStart);
        stage = AuthenticationStage.Ready;
        actions.RefreshPageLogin();
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        await actions.StartBusinessAsync();
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
    }

    [Fact]
    public async Task PageLogin_PreparationAndDisposalCancelPendingLogin()
    {
        var stage = AuthenticationStage.Authenticating;
        var page = new LoginPage();
        await using var launcher = Create(new Monitor(true), new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        var actions = new ManualLauncherActions(launcher, new Preparation(),
            pageLogin: login, authenticationStage: () => stage);
        await actions.StartBusinessAsync();
        await actions.ResetAsync();
        stage = AuthenticationStage.Ready;
        actions.RefreshPageLogin();
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        stage = AuthenticationStage.Authenticating;
        await actions.StartBusinessAsync();
        await actions.DisposeAsync();
        stage = AuthenticationStage.Ready;
        actions.RefreshPageLogin();
        Assert.Equal(0, page.Submissions);
    }

    [Fact]
    public async Task PageLogin_AuthenticationReadyCallbackFinishesPendingAttempt()
    {
        var stage = AuthenticationStage.Authenticating;
        var page = new LoginPage();
        await using var launcher = Create(new Monitor(true), new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(),
            pageLogin: login, authenticationStage: () => stage);
        await actions.StartBusinessAsync();
        Assert.Equal(0, page.Submissions);
        stage = AuthenticationStage.Ready;
        actions.RefreshPageLogin();
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
        Assert.Contains("提交", actions.Message);
    }

    [Fact]
    public async Task PageLogin_UnhealthyEnvironmentNeverArmsAttempt()
    {
        var page = new LoginPage();
        await using var launcher = Create(new Monitor(true), new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(),
            prerequisites: new Prerequisites(), pageLogin: login);
        await actions.StartBusinessAsync();
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        Assert.False(actions.HasStarted);
    }

    [Fact]
    public async Task PageLogin_NavigationFailureCancels_ExplicitRetryArmsNewAttempt()
    {
        var page = new LoginPage();
        await using var launcher = Create(new Monitor(true), new Browser { FailNext = true });
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(), pageLogin: login);
        await actions.StartBusinessAsync();
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        Assert.True(actions.RequiresExplicitStart);
        await actions.StartBusinessAsync();
        await login.ObserveAsync();
        Assert.Equal(1, page.Submissions);
    }

    [Fact]
    public async Task PageLogin_RemovalDuringInitialAuthenticationCancelsBeforeLauncherSeesUsb()
    {
        var stage = AuthenticationStage.WaitingForDevice;
        var monitor = new Monitor(false);
        var page = new LoginPage();
        await using var launcher = Create(monitor, new Browser());
        await using var login = new CertificatePageLoginCoordinator(new PinStore(), "test", page);
        await using var actions = new ManualLauncherActions(launcher, new Preparation(),
            pageLogin: login, authenticationStage: () => stage);
        monitor.OnStart = () =>
        {
            stage = AuthenticationStage.UnlockingToken;
            actions.RefreshPageLogin();
            Assert.False(actions.RequiresExplicitStart);
            stage = AuthenticationStage.WaitingForDevice;
            actions.RefreshPageLogin();
        };
        await actions.StartBusinessAsync();
        Assert.True(actions.RequiresExplicitStart);
        stage = AuthenticationStage.Ready;
        await monitor.PublishAsync(true);
        await login.ObserveAsync();
        Assert.Equal(0, page.Submissions);
    }

    private sealed class PinStore : IClientCertificatePinStore
    {
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificatePin?>(ClientCertificatePin.CopyOf("test-only-pin".AsMemory()));
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LoginPage : ICertificateLoginPage
    {
        public int Submissions;
        public Task<CertificateLoginPageState> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(CertificateLoginPageState.Ready);
        public Task<CertificateLoginResult> TryLoginAsync(ClientCertificatePin pin, CancellationToken cancellationToken)
        {
            Assert.True(pin.AsMemory().Span.SequenceEqual("test-only-pin".AsSpan()));
            Interlocked.Increment(ref Submissions);
            return Task.FromResult(CertificateLoginResult.Submitted);
        }
    }

    [Fact]
    public async Task ConstructionAndDisposal_DoNotStartDeviceOrBrowserWork()
    {
        var monitor = new Monitor(true);
        var browser = new Browser();
        await using var launcher = Create(monitor, browser);
        await using (var actions = new ManualLauncherActions(launcher, new Preparation()))
        {
            Assert.False(actions.HasStarted);
            Assert.True(actions.RequiresExplicitStart);
            Assert.Equal(0, monitor.Starts);
            Assert.Equal(0, monitor.Reads);
            Assert.Equal(0, browser.Navigations);
        }
        Assert.Equal(0, monitor.Starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitStart_UsesExistingDeviceFlow(bool inserted)
    {
        var monitor = new Monitor(inserted);
        var browser = new Browser();
        await using var launcher = Create(monitor, browser);
        await using var actions = new ManualLauncherActions(launcher, new Preparation());
        await actions.StartBusinessAsync();
        Assert.True(actions.HasStarted);
        Assert.Equal(1, monitor.Starts);
        Assert.Equal(1, monitor.Reads);
        Assert.Equal(inserted ? 1 : 0, browser.Navigations);
        if (!inserted) await monitor.PublishAsync(true);
        await actions.StartBusinessAsync();
        Assert.Equal(1, browser.Navigations);
        Assert.Equal(1, monitor.Starts);
    }

    [Fact]
    public async Task BusinessRetry_ReusesCoordinatorAndDoesNotRestartMonitor()
    {
        var monitor = new Monitor(true);
        var browser = new Browser { FailNext = true };
        await using var launcher = Create(monitor, browser);
        await using var actions = new ManualLauncherActions(launcher, new Preparation());
        await actions.StartBusinessAsync();
        Assert.True(launcher.CurrentState.CanRetry);
        await actions.StartBusinessAsync();
        Assert.Equal(LauncherStage.Ready, launcher.CurrentState.Stage);
        Assert.Equal(1, monitor.Starts);
        Assert.Equal(2, browser.Navigations);
    }

    [Fact]
    public async Task EnvironmentAndPinActions_NeverStartBusiness()
    {
        var monitor = new Monitor(true);
        var browser = new Browser();
        var preparation = new Preparation();
        await using var launcher = Create(monitor, browser);
        await using var actions = new ManualLauncherActions(launcher, preparation);
        await actions.InstallNativePromptBrokerAsync();
        await actions.ValidateAndSaveAsync();
        Assert.Equal(1, preparation.Installs);
        Assert.Equal(1, preparation.Saves);
        Assert.Equal(0, monitor.Starts);
        Assert.Equal(0, browser.Navigations);
        Assert.True(actions.RequiresExplicitStart);
    }

    [Fact]
    public async Task PreparationInFlight_RejectsConcurrentBusinessAndOtherPreparation()
    {
        var preparation = new Preparation { BlockSave = true };
        var monitor = new Monitor(true);
        await using var launcher = Create(monitor, new Browser());
        await using var actions = new ManualLauncherActions(launcher, preparation);
        var save = actions.ValidateAndSaveAsync();
        await preparation.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(actions.IsBusy);
        await actions.StartBusinessAsync();
        await actions.InstallNativePromptBrokerAsync();
        Assert.Equal(0, monitor.Starts);
        Assert.Equal(0, preparation.Installs);
        preparation.FinishSave.SetResult();
        await save;
        Assert.False(actions.IsBusy);
    }

    [Fact]
    public async Task AuthenticationBusy_RejectsMaintenanceAndDuplicateStart()
    {
        var preparation = new Preparation();
        var monitor = new Monitor(true);
        await using var launcher = Create(monitor, new Browser());
        await using var actions = new ManualLauncherActions(launcher, preparation, isAuthenticationBusy: () => true);
        await actions.ValidateAndSaveAsync();
        await actions.DeleteAsync();
        await actions.InstallNativePromptBrokerAsync();
        await actions.StartBusinessAsync();
        Assert.Equal(0, preparation.Saves + preparation.Deletes + preparation.Installs + monitor.Starts);
    }

    [Fact]
    public async Task MissingEnvironment_RequiresExplicitStartAfterInstall()
    {
        var preparation = new Preparation();
        var monitor = new Monitor(true);
        var environment = new Prerequisites();
        await using var launcher = Create(monitor, new Browser());
        await using var actions = new ManualLauncherActions(launcher, preparation, prerequisites: environment);
        await actions.StartBusinessAsync();
        Assert.False(actions.HasStarted);
        Assert.Equal(0, monitor.Starts);
        environment.Healthy = true;
        await actions.InstallNativePromptBrokerAsync();
        Assert.True(actions.RequiresExplicitStart);
        Assert.Equal(0, monitor.Starts);
        await actions.StartBusinessAsync();
        Assert.Equal(1, monitor.Starts);
    }

    [Fact]
    public async Task CloseDuringPreparation_CancelsAndDrainsWithoutStartingBusiness()
    {
        var preparation = new Preparation { BlockSave = true };
        var monitor = new Monitor(true);
        await using var launcher = Create(monitor, new Browser());
        var actions = new ManualLauncherActions(launcher, preparation);
        var save = actions.ValidateAndSaveAsync();
        await preparation.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await actions.DisposeAsync();
        await save;
        await actions.StartBusinessAsync();
        Assert.Equal(0, monitor.Starts);
    }

    private static LauncherCoordinator Create(Monitor monitor, Browser browser) =>
        new(monitor, browser, new LauncherProfile("https://example.test/", new("1234", "5678", "Test")));

    private sealed class Monitor(bool inserted) : IUsbDeviceMonitor
    {
        public int Starts, Reads;
        public Action? OnStart;
        public event UsbDevicesChangedEventHandler? DevicesChanged;
        private bool _inserted = inserted;
        private IReadOnlyList<UsbDeviceDescriptor> Devices => _inserted
            ? [new("USB\\VID_1234&PID_5678\\TEST", ["USB\\VID_1234&PID_5678"], "Test", [], [], [])] : [];
        public Task StartAsync(CancellationToken cancellationToken = default) { Starts++; OnStart?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(Devices); }
        public async Task PublishAsync(bool present)
        {
            _inserted = present;
            if (DevicesChanged is { } handlers)
                foreach (UsbDevicesChangedEventHandler handler in handlers.GetInvocationList())
                    await handler(this, new(Devices));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Browser : IBrowserHost
    {
        public int Navigations;
        public bool FailNext;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default)
        {
            Navigations++;
            if (FailNext) { FailNext = false; throw new InvalidOperationException("synthetic navigation failure"); }
            return Task.CompletedTask;
        }
    }
    private sealed class Preparation : ILocalPinCommands, INativePromptBrokerCommands
    {
        public int Saves, Installs, Deletes;
        public bool BlockSave;
        public TaskCompletionSource SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? StateChanged { add { } remove { } }
        public NativePromptBrokerPrerequisiteState CurrentState => new(true, true, true, "Ready");
        public async Task ValidateAndSaveAsync(CancellationToken cancellationToken = default)
        { Saves++; SaveStarted.TrySetResult(); if (BlockSave) await FinishSave.Task.WaitAsync(cancellationToken); }
        public Task ResetAsync(CancellationToken cancellationToken = default) => ValidateAndSaveAsync(cancellationToken);
        public Task DeleteAsync(CancellationToken cancellationToken = default) { Deletes++; return Task.CompletedTask; }
        public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) { Installs++; return Task.CompletedTask; }
        public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Prerequisites : INativePromptBrokerPrerequisites
    {
        public bool Healthy;
        public event EventHandler? StateChanged { add { } remove { } }
        public NativePromptBrokerPrerequisiteState CurrentState => new(true, Healthy, Healthy, "Test prerequisite");
        public Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(Healthy);
        public Task<bool> InstallAsync(CancellationToken cancellationToken = default) => Task.FromResult(Healthy = true);
        public Task<bool> UninstallAsync(CancellationToken cancellationToken = default) => Task.FromResult(Healthy = false);
    }
}
