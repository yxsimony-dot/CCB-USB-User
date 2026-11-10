namespace UsbWebLauncher.App.Browser;

internal static class PsbcNavigationContinuation
{
    public static bool IsBankOrigin(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("corpebank.psbc.com", StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 &&
        uri.UserInfo.Length == 0;

    public static bool CanWait(Uri? initialTarget, WebNavigationCompletedEventArgs response) =>
        IsBankOrigin(initialTarget) &&
        IsBankOrigin(response.TargetUri) &&
        !response.IsSuccess &&
        response.HttpStatusCode == 412 &&
        response.NetworkError is null &&
        response.ErrorStatus is "Unknown" or "Unknown (HTTP 412)";

    public static bool IsSuccessful(WebNavigationCompletedEventArgs response) =>
        IsBankOrigin(response.TargetUri) &&
        response.IsSuccess &&
        response.HttpStatusCode == 200 &&
        response.NetworkError is null;
}
