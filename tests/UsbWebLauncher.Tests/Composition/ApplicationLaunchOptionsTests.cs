using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ApplicationLaunchOptionsTests
{
    [Fact]
    public void Parse_WithSmokeArgument_UsesEmptyDeviceMonitor()
    {
        var options = ApplicationLaunchOptions.Parse(
            ["--smoke-test-no-devices"]);

        Assert.True(options.UseEmptyDeviceMonitor);
    }

    [Fact]
    public void Parse_WithoutSmokeArgument_UsesProductionDeviceMonitor()
    {
        var options = ApplicationLaunchOptions.Parse(["--unrelated"]);

        Assert.False(options.UseEmptyDeviceMonitor);
    }

    [Fact]
    public void Parse_WithLifecycleObservationPath_UsesEmptyMonitorAndNormalizesPath()
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherLifecycleTests",
            "report.json");

        var options = ApplicationLaunchOptions.Parse(
            [$"--lifecycle-observation-report={reportPath}"]);

        Assert.True(options.UseEmptyDeviceMonitor);
        Assert.True(options.IsLifecycleObservation);
        Assert.Equal(Path.GetFullPath(reportPath), options.LifecycleObservationReportPath);
    }

    [Fact]
    public void Parse_WithRelativeLifecycleObservationPath_RejectsAmbiguousOutput()
    {
        Assert.Throws<ArgumentException>(() => ApplicationLaunchOptions.Parse(
            ["--lifecycle-observation-report=relative-report.json"]));
    }

    [Fact]
    public void Parse_WithPsbcCertificateDiagnosticsPath_ExplicitlyEnablesOutput()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcDiagnosticsTests",
            "certificates.jsonl");

        var options = ApplicationLaunchOptions.Parse(
            [$"--psbc-certificate-diagnostics={outputPath}"]);

        Assert.Equal(
            Path.GetFullPath(outputPath),
            options.PsbcCertificateDiagnosticsPath);
    }

    [Fact]
    public void Parse_WithRelativePsbcCertificateDiagnosticsPath_RejectsAmbiguousOutput()
    {
        Assert.Throws<ArgumentException>(() => ApplicationLaunchOptions.Parse(
            ["--psbc-certificate-diagnostics=certificates.jsonl"]));
    }

    [Fact]
    public void Parse_WithPsbcNativeWindowDiagnosticsPath_ExplicitlyEnablesOutput()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcDiagnosticsTests",
            "native-windows.jsonl");

        var options = ApplicationLaunchOptions.Parse(
            [$"--psbc-native-window-diagnostics={outputPath}"]);

        Assert.Equal(
            Path.GetFullPath(outputPath),
            options.PsbcNativeWindowDiagnosticsPath);
    }

    [Fact]
    public void Parse_WithRelativePsbcNativeWindowDiagnosticsPath_RejectsAmbiguousOutput()
    {
        Assert.Throws<ArgumentException>(() => ApplicationLaunchOptions.Parse(
            ["--psbc-native-window-diagnostics=native-windows.jsonl"]));
    }

    [Fact]
    public async Task EmptyDeviceMonitor_NeverReportsADevice()
    {
        await using var monitor = new EmptyUsbDeviceMonitor();

        await monitor.StartAsync();
        var devices = await monitor.GetConnectedDevicesAsync();

        Assert.Empty(devices);
    }
}
