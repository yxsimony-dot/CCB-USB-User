namespace UsbWebLauncher.Core.Devices;

public enum UsbKeyDetectionStatus
{
    Absent,
    PresentButEnvironmentUnavailable,
    Ready
}

public enum UsbKeyVendorFamily
{
    FeitianEnterSafe,
    Hdzb,
    Tendyron,
    Excelsecu,
    WatchData
}

public enum UsbKeyDiagnosticCode
{
    None,
    DeviceAbsent,
    DriverUnavailable,
    ServiceUnavailable,
    ProviderUnavailable,
    RegistryMarkerUnavailable,
    TokenUnavailable,
    ProbeCanceled,
    ProbeTimedOut,
    ProbeFailed
}

public sealed record UsbKeyDetectionResult(
    UsbKeyDetectionStatus Status,
    UsbKeyVendorFamily? VendorFamily,
    UsbKeyDiagnosticCode DiagnosticCode,
    string? PhysicalInstanceId,
    string? TokenIdentity = null)
{
    public static UsbKeyDetectionResult Absent(
        UsbKeyVendorFamily? vendorFamily = null,
        UsbKeyDiagnosticCode diagnosticCode = UsbKeyDiagnosticCode.DeviceAbsent) =>
        new(
            UsbKeyDetectionStatus.Absent,
            vendorFamily,
            diagnosticCode,
            PhysicalInstanceId: null);

    public static UsbKeyDetectionResult EnvironmentUnavailable(
        UsbKeyVendorFamily vendorFamily,
        UsbKeyDiagnosticCode diagnosticCode,
        string? physicalInstanceId) =>
        new(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            vendorFamily,
            diagnosticCode,
            physicalInstanceId);

    public static UsbKeyDetectionResult Ready(
        UsbKeyVendorFamily vendorFamily,
        string physicalInstanceId,
        string? tokenIdentity = null) =>
        new(
            UsbKeyDetectionStatus.Ready,
            vendorFamily,
            UsbKeyDiagnosticCode.None,
            physicalInstanceId,
            tokenIdentity);
}
