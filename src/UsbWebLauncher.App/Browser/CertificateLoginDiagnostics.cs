using System.IO;
using System.Text.Json;

namespace UsbWebLauncher.App.Browser;

// Callers pass only state enums, URI origin/path and DOM attributes; never values,
// script payloads, credentials, exception messages or full URL query strings.
internal static class CertificateLoginDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsbWebLauncher", "logs", "certificate-page-login.jsonl");

    internal static string LogDirectory => Path.GetDirectoryName(PathName)!;

    public static void BeginRun()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.WriteAllText(PathName, string.Empty);
            }
            catch { }
        }
        Write("run-start");
    }

    public static void Write(string eventName, object? data = null)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                if (File.Exists(PathName) && new FileInfo(PathName).Length > 512 * 1024) return;
                File.AppendAllText(PathName, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now, eventName, data
                }) + Environment.NewLine);
            }
            catch { /* Diagnostics must not change authentication or UI behavior. */ }
        }
    }
}
