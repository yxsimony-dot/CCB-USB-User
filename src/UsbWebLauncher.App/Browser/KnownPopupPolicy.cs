namespace UsbWebLauncher.App.Browser;

internal static class KnownPopupPolicy
{
    public static bool ShouldBlock(string source, string destination, bool userInitiated) =>
        IsBusinessSource(source) &&
        Uri.TryCreate(destination, UriKind.Absolute, out var uri) && Secure(uri) &&
        IsPublicInformation(uri);

    private static bool IsBusinessSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && Secure(uri) &&
        uri.Host is "b2bbank.ccb.com" or "b2b3.ccb.com" or "xc.b2b3.ccb.com" &&
        uri.AbsolutePath.StartsWith("/NCCB/", StringComparison.Ordinal);

    private static bool IsPublicInformation(Uri destination)
    {
        // These are public information sites, not the authenticated transaction
        // hosts. Bank scripts can inherit activation from the login click, so
        // IsUserInitiated is not a reliable distinction for these destinations.
        // Normalize the bank's observed double slash without pinning article dates.
        var path = "/" + string.Join("/", destination.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return destination.Host switch
        {
            "www.ccb.com" or "www2.ccb.com" or "wealth.ccb.com" =>
                path.StartsWith("/chn/", StringComparison.Ordinal),
            "i.xiu.ccb.com" => path.StartsWith("/v2/manage/book/", StringComparison.Ordinal),
            _ => false
        };
    }

    public static bool IsBankDocument(string source, string path) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && Secure(uri) &&
        uri.Host is "b2b3.ccb.com" or "xc.b2b3.ccb.com" && uri.AbsolutePath == path;

    public static bool IsPopupMessageSource(string source, string topSource) =>
        IsBankDocument(topSource, "/NCCB/V6/b2bindex.jsp") &&
        Uri.TryCreate(topSource, UriKind.Absolute, out var top) &&
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && Secure(uri) &&
        uri.Host == top.Host && uri.AbsolutePath.StartsWith("/NCCB/", StringComparison.Ordinal);

    private static bool Secure(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0;

    public static string? DiagnosticEvent(string message) => message switch
    {
        "usb-launcher-popup:notice-installed" => "popup-notice-installed",
        "usb-launcher-popup:notice-clicked" => "popup-notice-clicked",
        "usb-launcher-popup:notice-failed" => "popup-notice-failed",
        "usb-launcher-popup:mobile-clicked" => "popup-mobile-clicked",
        "usb-launcher-popup:guide-clicked" => "popup-guide-clicked",
        "usb-launcher-popup:promotion-clicked" => "popup-promotion-clicked",
        _ => null
    };
}
