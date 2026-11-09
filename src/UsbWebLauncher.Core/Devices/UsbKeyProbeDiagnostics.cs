using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbWebLauncher.Core.Devices;

public sealed record UsbKeyProbeDiagnostic(
    UsbKeyVendorFamily? Vendor,
    UsbKeyDetectionStatus Status,
    UsbKeyDiagnosticCode DiagnosticCode);

public static class UsbKeyProbeDiagnostics
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static UsbKeyProbeDiagnostic Sanitize(UsbKeyDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new UsbKeyProbeDiagnostic(
            result.VendorFamily,
            result.Status,
            result.DiagnosticCode);
    }

    public static string Serialize(UsbKeyDetectionResult result) =>
        JsonSerializer.Serialize(Sanitize(result), SerializerOptions);
}
