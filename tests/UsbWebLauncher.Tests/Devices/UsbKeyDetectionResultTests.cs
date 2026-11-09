using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class UsbKeyDetectionResultTests
{
    [Fact]
    public void Absent_CapturesStableAbsenceState()
    {
        var result = UsbKeyDetectionResult.Absent();

        Assert.Equal(UsbKeyDetectionStatus.Absent, result.Status);
        Assert.Null(result.VendorFamily);
        Assert.Equal(UsbKeyDiagnosticCode.DeviceAbsent, result.DiagnosticCode);
        Assert.Null(result.PhysicalInstanceId);
        Assert.Null(result.TokenIdentity);
    }

    [Fact]
    public void EnvironmentUnavailable_CapturesVendorAndPhysicalDevice()
    {
        var result = UsbKeyDetectionResult.EnvironmentUnavailable(
            UsbKeyVendorFamily.Tendyron,
            UsbKeyDiagnosticCode.ServiceUnavailable,
            @"USB\VID_1780&PID_0401\SAMPLE");

        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            result.Status);
        Assert.Equal(UsbKeyVendorFamily.Tendyron, result.VendorFamily);
        Assert.Equal(UsbKeyDiagnosticCode.ServiceUnavailable, result.DiagnosticCode);
        Assert.Equal(@"USB\VID_1780&PID_0401\SAMPLE", result.PhysicalInstanceId);
        Assert.Null(result.TokenIdentity);
    }

    [Fact]
    public void Ready_CapturesOptionalTokenIdentity()
    {
        var result = UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.Tendyron,
            @"USB\VID_1780&PID_0401\SAMPLE",
            "sample-token");

        Assert.Equal(UsbKeyDetectionStatus.Ready, result.Status);
        Assert.Equal(UsbKeyVendorFamily.Tendyron, result.VendorFamily);
        Assert.Equal(UsbKeyDiagnosticCode.None, result.DiagnosticCode);
        Assert.Equal(@"USB\VID_1780&PID_0401\SAMPLE", result.PhysicalInstanceId);
        Assert.Equal("sample-token", result.TokenIdentity);
    }
}
