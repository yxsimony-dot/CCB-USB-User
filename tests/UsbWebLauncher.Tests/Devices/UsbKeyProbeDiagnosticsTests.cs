using System.Text.Json;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class UsbKeyProbeDiagnosticsTests
{
    [Fact]
    public void Serialize_IncludesOnlySanitizedProbeFields()
    {
        var result = UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\SENSITIVE-INSTANCE",
            "SENSITIVE-TOKEN-IDENTITY");

        var json = UsbKeyProbeDiagnostics.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[] { "diagnosticCode", "status", "vendor" },
            properties);
        Assert.Equal(
            nameof(UsbKeyVendorFamily.Tendyron),
            document.RootElement.GetProperty("vendor").GetString());
        Assert.Equal(
            nameof(UsbKeyDetectionStatus.Ready),
            document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            nameof(UsbKeyDiagnosticCode.None),
            document.RootElement.GetProperty("diagnosticCode").GetString());
        Assert.DoesNotContain("SENSITIVE", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_DropsPhysicalInstanceAndTokenIdentity()
    {
        var result = UsbKeyDetectionResult.EnvironmentUnavailable(
            UsbKeyVendorFamily.Hdzb,
            UsbKeyDiagnosticCode.ServiceUnavailable,
            "USB\\PRIVATE-INSTANCE");

        var diagnostic = UsbKeyProbeDiagnostics.Sanitize(result);

        Assert.Equal(UsbKeyVendorFamily.Hdzb, diagnostic.Vendor);
        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            diagnostic.Status);
        Assert.Equal(
            UsbKeyDiagnosticCode.ServiceUnavailable,
            diagnostic.DiagnosticCode);
        Assert.DoesNotContain(
            diagnostic.GetType().GetProperties(),
            property => property.Name.Contains(
                "Identity",
                StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains(
                    "Instance",
                    StringComparison.OrdinalIgnoreCase));
    }
}
