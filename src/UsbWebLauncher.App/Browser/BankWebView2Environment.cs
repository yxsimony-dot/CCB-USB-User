using Microsoft.Web.WebView2.Core;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Browser;

internal static class BankWebView2Environment
{
    public static CoreWebView2EnvironmentOptions CreateOptions(
        BankBrowserCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.EnvironmentKind switch
        {
            BankWebView2EnvironmentKind.Standard => new CoreWebView2EnvironmentOptions(),
            BankWebView2EnvironmentKind.ExtensionEnabled =>
                CcbUKeyWebView2Environment.CreateOptions(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities.EnvironmentKind,
                "Unsupported WebView2 environment kind.")
        };
    }
}
