using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.App.Presentation;

internal sealed record DependencyReadiness(bool WebViewReady, bool BankHostInstalled, bool BrokerReady)
{
    public bool IsReady => WebViewReady && BankHostInstalled && BrokerReady;
}

internal static class CcbNativeHostReadiness
{
    internal const string HostName = "ccb.detector.ukey.edgeextension";

    public static bool IsInstalled()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var browser in new[] { @"Microsoft\Edge", @"Google\Chrome" })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"Software\{browser}\NativeMessagingHosts\{HostName}");
                if (key?.GetValue(null) is string path && File.Exists(path) &&
                    ValidateManifest(File.ReadAllText(path), path, File.Exists)) return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return false;
    }

    internal static bool ValidateManifest(string json, string manifestPath, Func<string, bool> fileExists)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("name").GetString() != HostName || root.GetProperty("type").GetString() != "stdio") return false;
            var path = root.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fullPath = Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || !fileExists(fullPath)) return false;
            return root.GetProperty("allowed_origins").EnumerateArray().Any(origin =>
                origin.GetString() == $"chrome-extension://{CcbUKeyBrowserExtensionPackage.ExpectedExtensionId}/");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or NotSupportedException) { return false; }
    }
}
