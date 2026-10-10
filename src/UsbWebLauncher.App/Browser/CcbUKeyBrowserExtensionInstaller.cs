using System.IO;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

internal interface IBrowserExtension
{
    string Id { get; }

    string Name { get; }

    bool IsEnabled { get; }

    Task EnableAsync(CancellationToken cancellationToken);
}

internal interface IBrowserExtensionProfile
{
    Task<IReadOnlyList<IBrowserExtension>> GetBrowserExtensionsAsync(
        CancellationToken cancellationToken);

    Task<IBrowserExtension> AddBrowserExtensionAsync(
        string extensionDirectoryPath,
        CancellationToken cancellationToken);
}

internal sealed class CcbUKeyBrowserExtensionInstaller
{
    public async Task EnsureInstalledAsync(
        IBrowserExtensionProfile profile,
        CcbUKeyBrowserExtensionPackage package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(package);

        var installed = await profile.GetBrowserExtensionsAsync(cancellationToken);
        var matches = installed.Where(extension =>
                string.Equals(
                    extension.Id,
                    CcbUKeyBrowserExtensionPackage.ExpectedExtensionId,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException(
                "More than one installed CCB UKey browser extension matched the authorized ID.");
        }

        var extension = matches.SingleOrDefault();
        if (extension is null)
        {
            extension = await profile.AddBrowserExtensionAsync(
                package.DirectoryPath,
                cancellationToken);
        }

        ValidateInstalledExtension(extension);
        if (!extension.IsEnabled)
        {
            await extension.EnableAsync(cancellationToken);
        }
    }

    private static void ValidateInstalledExtension(IBrowserExtension extension)
    {
        if (!string.Equals(
                extension.Id,
                CcbUKeyBrowserExtensionPackage.ExpectedExtensionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"WebView2 installed an unexpected browser extension ID: {extension.Id}");
        }

        if (!string.Equals(
                extension.Name,
                CcbUKeyBrowserExtensionPackage.ExpectedName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The installed browser extension name does not match the authorized CCB UKey extension.");
        }
    }
}

internal sealed class WebView2BrowserExtensionProfile(CoreWebView2Profile profile)
    : IBrowserExtensionProfile
{
    private readonly CoreWebView2Profile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));

    public async Task<IReadOnlyList<IBrowserExtension>> GetBrowserExtensionsAsync(
        CancellationToken cancellationToken)
    {
        var extensions = await _profile.GetBrowserExtensionsAsync()
            .WaitAsync(cancellationToken);
        return extensions
            .Select(extension => (IBrowserExtension)new WebView2BrowserExtension(extension))
            .ToArray();
    }

    public async Task<IBrowserExtension> AddBrowserExtensionAsync(
        string extensionDirectoryPath,
        CancellationToken cancellationToken)
    {
        var extension = await _profile.AddBrowserExtensionAsync(extensionDirectoryPath)
            .WaitAsync(cancellationToken);
        return new WebView2BrowserExtension(extension);
    }
}

internal sealed class WebView2BrowserExtension(CoreWebView2BrowserExtension extension)
    : IBrowserExtension
{
    private readonly CoreWebView2BrowserExtension _extension =
        extension ?? throw new ArgumentNullException(nameof(extension));

    public string Id => _extension.Id;

    public string Name => _extension.Name;

    public bool IsEnabled => _extension.IsEnabled;

    public Task EnableAsync(CancellationToken cancellationToken) =>
        _extension.EnableAsync(true).WaitAsync(cancellationToken);
}
