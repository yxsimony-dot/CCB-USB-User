using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

internal static class CcbUKeyWebView2Environment
{
    public static CoreWebView2EnvironmentOptions CreateOptions() =>
        new()
        {
            AreBrowserExtensionsEnabled = true
        };
}
