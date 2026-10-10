using System.IO;
using System.Text.Json;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class DependencyReadinessTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void EachMissingDependencyPreventsReady(bool webView, bool bank, bool broker) =>
        Assert.False(new DependencyReadiness(webView, bank, broker).IsReady);

    [Fact]
    public void AllDependenciesAreRequired() => Assert.True(new DependencyReadiness(true, true, true).IsReady);

    [Theory]
    [InlineData(false, "ccb.detector.ukey.edgeextension", false)]
    [InlineData(true, "different.host", false)]
    [InlineData(true, "ccb.detector.ukey.edgeextension", true)]
    public void BankManifestRequiresCorrectHostAndExistingExecutable(bool executableExists, string name, bool expected)
    {
        var json = JsonSerializer.Serialize(new { name, type = "stdio", path = "host.exe",
            allowed_origins = new[] { "chrome-extension://ahjacjkbgeefngpfnfhccehkfnaeahbf/" } });
        Assert.Equal(expected, CcbNativeHostReadiness.ValidateManifest(json,
            Path.Combine(Path.GetTempPath(), "manifest.json"), _ => executableExists));
    }

    [Fact]
    public void MalformedManifestDoesNotReportReady() =>
        Assert.False(CcbNativeHostReadiness.ValidateManifest("{}", @"C:\Bank\manifest.json", _ => true));
}
