using System.IO;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Browser;

internal static class BankBrowserSessionDirectory
{
    public static BrowserSessionDirectory Create(
        BankBrowserCapabilities capabilities,
        string? baseRoot = null) =>
        new(ResolveRoot(capabilities, baseRoot));

    public static string ResolveRoot(
        BankBrowserCapabilities capabilities,
        string? baseRoot = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var directoryName = capabilities.SessionDirectoryName;
        if (string.IsNullOrWhiteSpace(directoryName) ||
            !string.Equals(
                directoryName,
                Path.GetFileName(directoryName),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The browser session directory name must be a single path segment.",
                nameof(capabilities));
        }

        return Path.GetFullPath(Path.Combine(
            baseRoot ?? BrowserSessionDirectory.DefaultRoot,
            directoryName));
    }
}
