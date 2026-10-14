using System.IO.Compression;
using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public void IncludesSummaryAndOnlyAllowedLogFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            foreach (var name in new[]
                     {
                         "certificate-page-login.jsonl",
                         "statement-discovery.jsonl",
                         "receipt-discovery.jsonl",
                         "popup-structure.jsonl",
                         "unrelated.txt"
                     })
                File.WriteAllText(Path.Combine(root, name), name);

            var destination = Path.Combine(root, "bundle.zip");
            DiagnosticBundleExporter.Export(destination, root);

            using var archive = ZipFile.OpenRead(destination);
            var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
            Assert.Equal(new[]
            {
                "certificate-page-login.jsonl",
                "diagnostics-summary.json",
                "popup-structure.jsonl",
                "receipt-discovery.jsonl",
                "statement-discovery.jsonl"
            }, names);
        }
        finally { DeleteTempDirectory(root); }
    }

    [Fact]
    public void CreatesSummaryWhenLogDirectoryDoesNotExist()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "bundle.zip");
            DiagnosticBundleExporter.Export(destination, Path.Combine(root, "missing"));

            using var archive = ZipFile.OpenRead(destination);
            Assert.Single(archive.Entries);
            Assert.Equal("diagnostics-summary.json", archive.Entries[0].FullName);
        }
        finally { DeleteTempDirectory(root); }
    }

    [Fact]
    public void SummaryContainsNoUserMachinePinOrFullPathInformation()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "bundle.zip");
            DiagnosticBundleExporter.Export(destination, Path.Combine(root, "missing"));

            using var archive = ZipFile.OpenRead(destination);
            using var stream = archive.GetEntry("diagnostics-summary.json")!.Open();
            using var document = JsonDocument.Parse(stream);
            var json = document.RootElement.GetRawText();
            Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PIN", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally { DeleteTempDirectory(root); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "UsbWebLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
