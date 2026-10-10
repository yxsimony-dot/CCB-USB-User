using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.App.Composition;

public interface IClientCertificateAuthenticationRuntimeFactory
{
    IClientCertificateAuthenticationRuntime Create(
        ClientCertificateAuthenticationOptions authentication,
        ICorrelatedBrowserHost browser,
        Uri target);
}

public interface IClientCertificateAuthenticationRuntime : IAsyncDisposable
{
    event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged;
    AuthenticationState CurrentState { get; }
    IBrowserClientCertificateRequestHandler Handler { get; }
    IBrowserClientCertificateAuthenticationReporter Reporter { get; }
    ILauncherAuthenticationGate LaunchGate { get; }
    IClientCertificatePinSetupService PinSetupService { get; }
    INativePromptBrokerPrerequisites BrokerPrerequisites { get; }
    IManualAuthenticationControl? ManualControl => null;
    IClientCertificatePinStore? PinStore => null;
}

public sealed class LauncherApplicationComposer(
    IClientCertificateAuthenticationRuntimeFactory authenticationFactory)
{
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(30);

    public async Task<LauncherApplicationRuntime> ComposeAsync(
        LauncherProfile profile,
        IUsbDeviceMonitor deviceMonitor,
        IClientCertificateConfigurableBrowserHost browser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(deviceMonitor);
        ArgumentNullException.ThrowIfNull(browser);

        IClientCertificateAuthenticationRuntime? authentication = null;
        try
        {
            if (profile.ClientCertificateAuthentication is { } options)
            {
                authentication = authenticationFactory.Create(
                    options,
                    browser,
                    new Uri(profile.TargetUrl));
                await browser.ConfigureClientCertificateAuthenticationAsync(
                    authentication.Handler,
                    authentication.Reporter,
                    AuthenticationTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var launcher = new LauncherCoordinator(
                deviceMonitor,
                browser,
                profile,
                authentication?.LaunchGate);
            return new(launcher, authentication, browser);
        }
        catch
        {
            if (authentication is not null)
            {
                await authentication.DisposeAsync().ConfigureAwait(false);
            }

            await browser.DisposeAsync().ConfigureAwait(false);
            await deviceMonitor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class LauncherApplicationRuntime(
    LauncherCoordinator launcher,
    IClientCertificateAuthenticationRuntime? authentication,
    IAsyncDisposable browser) : IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public LauncherCoordinator Launcher { get; } = launcher;

    public IClientCertificateAuthenticationRuntime? Authentication { get; } = authentication;

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Cancellation of one owner must not be held behind another owner's
        // outstanding navigation or native authentication operation.
        var cleanup = new List<Task>
        {
            Task.Run(async () => await Launcher.DisposeAsync().ConfigureAwait(false)),
            Task.Run(async () => await browser.DisposeAsync().ConfigureAwait(false))
        };
        if (Authentication is not null)
            cleanup.Add(Task.Run(async () => await Authentication.DisposeAsync().ConfigureAwait(false)));
        await Task.WhenAll(cleanup).ConfigureAwait(false);
    }
}
