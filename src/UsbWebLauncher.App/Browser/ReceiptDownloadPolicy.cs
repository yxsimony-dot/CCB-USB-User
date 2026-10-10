using System.IO;
using System.Text.RegularExpressions;

namespace UsbWebLauncher.App.Browser;

public static class ReceiptDownloadPolicy
{
    public static bool IsExpectedEndpoint(string origin, string? raw) =>
        StatementDownloadPolicy.IsBankOrigin(origin) &&
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(uri.AbsolutePath, @"\A/NCCB/NECV6FileDownload_[0-9]+_LH\z", RegexOptions.CultureInvariant) &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static bool IsPdf(string? mime, string? suggestedFileName)
    {
        var mediaType = mime?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (mediaType == "application/pdf") return true;
        // The observed attachment had a non-standard media type but a .pdf name
        // and PDF signature. This is only a candidate; publication still validates
        // the completed bytes through StatementPdfFile, never the extension alone.
        if (!string.Equals(Path.GetExtension(suggestedFileName), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        return mediaType is not ("application/json" or "application/xml" or "application/zip" or "application/gzip" or "application/x-zip-compressed") &&
            !(mediaType?.StartsWith("text/", StringComparison.Ordinal) ?? false) &&
            !(mediaType?.StartsWith("image/", StringComparison.Ordinal) ?? false) &&
            !(mediaType?.StartsWith("audio/", StringComparison.Ordinal) ?? false) &&
            !(mediaType?.StartsWith("video/", StringComparison.Ordinal) ?? false) &&
            !(mediaType?.EndsWith("+json", StringComparison.Ordinal) ?? false) &&
            !(mediaType?.EndsWith("+xml", StringComparison.Ordinal) ?? false);
    }
}
