using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Browser;

internal sealed class BankBrowserExtensionInitializer
{
    private readonly CcbUKeyBrowserExtensionInstaller _ccbInstaller = new();

    public async Task EnsureConfiguredAsync(
        BankBrowserCapabilities capabilities,
        Func<IBrowserExtensionProfile> profileFactory,
        Func<CcbUKeyBrowserExtensionPackage> packageFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(profileFactory);
        ArgumentNullException.ThrowIfNull(packageFactory);

        if (!capabilities.InstallCcbUKeyExtension)
        {
            return;
        }

        if (capabilities.EnvironmentKind !=
            BankWebView2EnvironmentKind.ExtensionEnabled)
        {
            throw new InvalidOperationException(
                "The CCB UKey extension requires an extension-enabled WebView2 environment.");
        }

        await _ccbInstaller.EnsureInstalledAsync(
            profileFactory(),
            packageFactory(),
            cancellationToken);
    }
}
