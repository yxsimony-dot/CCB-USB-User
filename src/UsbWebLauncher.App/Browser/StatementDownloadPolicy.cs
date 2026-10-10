using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

public static class StatementDownloadPolicy
{
    public static bool ShouldFailNavigation(bool isSuccess, CoreWebView2WebErrorStatus status, bool downloadStarted) =>
        // An attachment can abort its navigation before DownloadStarting is raised.
        !isSuccess && status is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted)
        && !downloadStarted;

    public static bool IsBankOrigin(string? origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Host is "b2b3.ccb.com" or "xc.b2b3.ccb.com" && uri.AbsolutePath == "/" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static bool IsExpectedWindow(string origin, string target, string? source) =>
        IsResultPage(origin, target) && IsSourcePage(origin, source);

    public static bool IsResultPage(string origin, string? raw) => SameOrigin(origin, raw, out var uri) &&
        Regex.IsMatch(uri!.AbsolutePath, @"\A/NCCB/NECV6B2BMainPlat_[0-9]+_LH\z", RegexOptions.CultureInvariant);

    public static bool IsSourcePage(string origin, string? raw) => SameOrigin(origin, raw, out var uri) &&
        Regex.IsMatch(uri!.AbsolutePath, @"\A/NCCB/NECV6B2BMainPlat_[0-9]+_(?:LH|FW)\z", RegexOptions.CultureInvariant);

    public static bool IsPdf(string origin, string raw, string? mime, string? suggestedFileName = null)
    {
        var blob = raw.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);
        if (!SameOrigin(origin, blob ? raw[5..] : raw, out var uri)) return false;
        var mediaType = mime?.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType == "application/pdf" ||
            (mediaType is null or "" or "application/octet-stream" &&
                (uri!.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetExtension(suggestedFileName), ".pdf", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool SameOrigin(string origin, string? raw, out Uri? uri)
    {
        uri = null;
        return IsBankOrigin(origin) && Uri.TryCreate(raw, UriKind.Absolute, out uri) &&
            uri.UserInfo.Length == 0 && uri.Scheme == "https" && uri.Port == 443 &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);
    }
}
