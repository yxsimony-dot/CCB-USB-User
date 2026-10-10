using System.IO;
using System.Text.RegularExpressions;

namespace UsbWebLauncher.App.Browser;

/// <summary>Reduces bank request metadata to structural diagnostic information.</summary>
public static class StatementDiagnosticPolicy
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_.:-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex LongNumber = new(@"[0-9]{6,}", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"(?:^|[_.:-])(?:[a-fA-F0-9]{24,}|[A-Za-z0-9]{32,})(?:$|[_.:-])", RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(@"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}", RegexOptions.CultureInvariant);
    private static readonly Regex LongOpaque = new(@"[A-Za-z0-9_-]{40,}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".svg", ".ico", ".webp", ".bmp", ".avif",
        ".css", ".js", ".mjs", ".map", ".woff", ".woff2", ".ttf", ".otf", ".eot"
    };

    public static bool IsBankPage(string? raw)
        => TryBankUri(raw, out var uri) && uri.AbsolutePath.StartsWith("/NCCB/", StringComparison.OrdinalIgnoreCase);

    /// <returns>An object with origin, path, queryNames; null for an untrusted address.</returns>
    public static object? DescribeAddress(string? raw)
    {
        if (!TryBankUri(raw, out var uri)) return null;
        var parts = uri.AbsolutePath.Split('/').Take(25).Select(segment =>
        {
            var decoded = Decode(segment);
            return decoded.Length == 0 ? "" : CleanIdentifier(decoded) is { Length: > 0 } clean ? clean : "[redacted]";
        });
        var names = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => CleanIdentifier(Decode(part.Split('=', 2)[0])))
            .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).Take(40).ToArray();
        return new
        {
            origin = $"https://{uri.IdnHost.ToLowerInvariant()}",
            path = string.Join('/', parts),
            queryNames = names
        };
    }

    public static string CleanIdentifier(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 100 || !Identifier.IsMatch(raw)
            || LongNumber.IsMatch(raw) || Token.IsMatch(raw) || Uuid.IsMatch(raw) || LongOpaque.IsMatch(raw)) return "";
        return raw;
    }

    public static string MediaType(string? header)
        => HeaderKind(header) switch
        {
            "" => "unknown",
            "application/pdf" => "pdf",
            "text/html" or "application/xhtml+xml" => "html",
            "application/json" or "text/json" => "json",
            "application/octet-stream" => "octet-stream",
            "text/plain" => "plain",
            "application/xml" or "text/xml" => "xml",
            "text/javascript" or "application/javascript" or "application/x-javascript" => "javascript",
            _ => "other"
        };

    public static string Disposition(string? header)
        => HeaderKind(header) switch
        {
            "" => "none",
            "attachment" => "attachment",
            "inline" => "inline",
            _ => "other"
        };

    public static bool ShouldObserveResource(string uri, string? mime)
    {
        if (!TryBankUri(uri, out var parsed)) return false;
        var kind = HeaderKind(mime);
        if (kind.StartsWith("image/", StringComparison.Ordinal) || kind.StartsWith("font/", StringComparison.Ordinal)
            || kind == "text/css" || MediaType(mime) == "javascript"
            || StaticExtensions.Contains(Path.GetExtension(parsed.AbsolutePath))) return false;
        return IsBankPage(uri) || MediaType(mime) == "pdf"
            || parsed.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBankUri(string? raw, out Uri uri)
    {
        uri = null!;
        return raw is { Length: > 0 and <= 8192 }
            && Uri.TryCreate(raw, UriKind.Absolute, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
            && (uri.IdnHost.Equals("ccb.com", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.EndsWith(".ccb.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string HeaderKind(string? header)
        => string.IsNullOrWhiteSpace(header) ? "" : header.Split(';', 2)[0].Trim().ToLowerInvariant();

    private static string Decode(string raw)
    {
        for (var count = 0; count < 3; count++)
        {
            var decoded = Uri.UnescapeDataString(raw);
            if (decoded == raw) break;
            raw = decoded;
        }
        return raw;
    }
}
