using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

internal static class DiagnosticBundleExporter
{
    private static readonly string[] AllowedLogNames =
    [
        "certificate-page-login.jsonl",
        "statement-discovery.jsonl",
        "receipt-discovery.jsonl",
        "popup-structure.jsonl"
    ];

    public static void Export(string destination, string? logDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination) ??
            throw new IOException("诊断日志保存目录无效。");
        Directory.CreateDirectory(parent);
        var temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var summary = archive.CreateEntry("diagnostics-summary.json", CompressionLevel.Fastest);
                using (var stream = summary.Open())
                {
                    JsonSerializer.Serialize(stream, CreateSummary(), new JsonSerializerOptions { WriteIndented = true });
                }

                var sourceDirectory = logDirectory ?? CertificateLoginDiagnostics.LogDirectory;
                foreach (var name in AllowedLogNames)
                {
                    var source = Path.Combine(sourceDirectory, name);
                    if (File.Exists(source)) archive.CreateEntryFromFile(source, name, CompressionLevel.Fastest);
                }
            }

            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static object CreateSummary()
    {
        string webView2Version;
        try { webView2Version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { webView2Version = "unknown"; }

        return new
        {
            schemaVersion = 1,
            applicationVersion = ProductVersion.Display,
            exportedAt = DateTimeOffset.UtcNow,
            windowsVersion = Environment.OSVersion.Version.ToString(),
            webView2Version
        };
    }
}
