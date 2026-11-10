namespace UsbWebLauncher.Core.Devices;

public static class PsbcUsbKeyMatcher
{
    private static readonly string[] FeitianMarkers =
    [
        "Token EasyPlay",
        "Token 2000Auto",
        "InterPass3000",
        "EnterSafe",
        "Feitian"
    ];

    private static readonly string[] HdzbMarkers =
    [
        "PSBC_HDZB_USBKey2G",
        "HDZB",
        "uKeyCI"
    ];

    private static readonly string[] ExcelsecuMarkers =
    [
        "ven_swukey",
        "ven_usbdisk",
        "prod_composite_token",
        "prod_scsitoken",
        "Excelsecu"
    ];

    private static readonly string[] WatchDataMarkers =
    [
        "WatchData",
        "Watch Data",
        "WatchKey"
    ];

    public static bool IsCandidate(UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Enum.GetValues<UsbKeyVendorFamily>()
            .Any(vendorFamily => IsCandidate(vendorFamily, device));
    }

    public static bool IsCandidate(
        UsbKeyVendorFamily vendorFamily,
        UsbDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return vendorFamily switch
        {
            UsbKeyVendorFamily.FeitianEnterSafe =>
                PsbcUsbKeyCandidateMatcher.HasVendorId(device, "096E") ||
                PsbcUsbKeyCandidateMatcher.ContainsAny(device, FeitianMarkers),
            UsbKeyVendorFamily.Hdzb =>
                PsbcUsbKeyCandidateMatcher.HasHardwareId(device, "1677", "6004") ||
                PsbcUsbKeyCandidateMatcher.ContainsAny(device, HdzbMarkers),
            UsbKeyVendorFamily.Tendyron =>
                PsbcUsbKeyCandidateMatcher.HasHardwareId(device, "1780", "0401") ||
                (PsbcUsbKeyCandidateMatcher.ContainsAny(
                     device.CdRomNames,
                     ["TENDYRON U-DISK"]) &&
                 PsbcUsbKeyCandidateMatcher.ContainsAny(
                     device.VolumeLabels,
                     ["PSBC"])),
            UsbKeyVendorFamily.Excelsecu =>
                PsbcUsbKeyCandidateMatcher.ContainsAny(device, ExcelsecuMarkers),
            UsbKeyVendorFamily.WatchData =>
                PsbcUsbKeyCandidateMatcher.HasVendorId(device, "163C") ||
                PsbcUsbKeyCandidateMatcher.ContainsAny(device, WatchDataMarkers),
            _ => false
        };
    }
}

public static class PsbcUsbKeyVendorProbes
{
    public static IReadOnlyList<IPsbcUsbKeyProbe> All { get; } =
        Array.AsReadOnly<IPsbcUsbKeyProbe>(
        [
            new FeitianEnterSafePsbcUsbKeyProbe(),
            new HdzbPsbcUsbKeyProbe(),
            new TendyronPsbcUsbKeyProbe(),
            new ExcelsecuPsbcUsbKeyProbe(),
            new WatchDataPsbcUsbKeyProbe()
        ]);
}

public sealed class FeitianEnterSafePsbcUsbKeyProbe : PsbcUsbKeyProbeBase
{
    public override UsbKeyVendorFamily VendorFamily =>
        UsbKeyVendorFamily.FeitianEnterSafe;

    protected override bool IsCandidate(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(VendorFamily, device);
}

public sealed class HdzbPsbcUsbKeyProbe : PsbcUsbKeyProbeBase
{
    private static readonly IReadOnlyList<PsbcRegistryMarker> Markers =
    [
        new(@"HKLM\SOFTWARE\HDZB\PSBC_HDZB_USBKey2G"),
        new(@"HKLM\SYSTEM\CurrentControlSet\Services\HDZB_Service_PSBC")
    ];

    public override UsbKeyVendorFamily VendorFamily => UsbKeyVendorFamily.Hdzb;

    protected override string? RequiredServiceName => "HDZB_Service_PSBC";

    protected override IReadOnlyList<PsbcRegistryMarker> RegistryMarkers => Markers;

    protected override bool IsCandidate(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(VendorFamily, device);
}

public sealed class TendyronPsbcUsbKeyProbe : PsbcUsbKeyProbeBase
{
    public override UsbKeyVendorFamily VendorFamily => UsbKeyVendorFamily.Tendyron;

    protected override string? RequiredServiceName => "OnKey Service _PSBC";

    protected override bool IsCandidate(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(VendorFamily, device);
}

public sealed class ExcelsecuPsbcUsbKeyProbe : PsbcUsbKeyProbeBase
{
    public override UsbKeyVendorFamily VendorFamily => UsbKeyVendorFamily.Excelsecu;

    protected override bool IsCandidate(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(VendorFamily, device);
}

public sealed class WatchDataPsbcUsbKeyProbe : PsbcUsbKeyProbeBase
{
    public override UsbKeyVendorFamily VendorFamily => UsbKeyVendorFamily.WatchData;

    protected override bool IsCandidate(UsbDeviceDescriptor device) =>
        PsbcUsbKeyMatcher.IsCandidate(VendorFamily, device);
}

public abstract class PsbcUsbKeyProbeBase : IPsbcUsbKeyProbe
{
    public abstract UsbKeyVendorFamily VendorFamily { get; }

    protected virtual string? RequiredServiceName => null;

    protected virtual IReadOnlyList<PsbcRegistryMarker> RegistryMarkers => [];

    protected abstract bool IsCandidate(UsbDeviceDescriptor device);

    public async Task<UsbKeyDetectionResult> ProbeAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        IPsbcUsbKeyEnvironmentQuery environmentQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(environmentQuery);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = devices
            .Where(IsCandidate)
            .DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var candidate = candidates.FirstOrDefault();
        if (candidate is null)
        {
            return UsbKeyDetectionResult.Absent(VendorFamily);
        }

        // Provider-wide enumeration cannot attribute containers to one of two keys.
        if (candidates.Length != 1)
        {
            return EnvironmentUnavailable(candidate, UsbKeyDiagnosticCode.TokenUnavailable);
        }

        if (RequiredServiceName is { } serviceName)
        {
            var serviceState = await environmentQuery
                .GetServiceStateAsync(serviceName, cancellationToken)
                .ConfigureAwait(false);
            if (serviceState != PsbcServiceState.Running)
            {
                return EnvironmentUnavailable(
                    candidate,
                    UsbKeyDiagnosticCode.ServiceUnavailable);
            }
        }

        var providerRegistered = false;
        foreach (var providerName in
                 PsbcUsbKeyEnvironmentCatalog.GetProviderNames(VendorFamily))
        {
            if (await environmentQuery
                    .IsCryptographicProviderRegisteredAsync(
                        providerName,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                providerRegistered = true;
                break;
            }
        }

        if (!providerRegistered)
        {
            return EnvironmentUnavailable(
                candidate,
                UsbKeyDiagnosticCode.ProviderUnavailable);
        }

        if (RegistryMarkers.Count > 0)
        {
            var markerPresent = false;
            foreach (var marker in RegistryMarkers)
            {
                if (await environmentQuery
                        .IsRegistryMarkerPresentAsync(marker, cancellationToken)
                        .ConfigureAwait(false))
                {
                    markerPresent = true;
                    break;
                }
            }

            if (!markerPresent)
            {
                return EnvironmentUnavailable(
                    candidate,
                    UsbKeyDiagnosticCode.RegistryMarkerUnavailable);
            }
        }

        var token = await environmentQuery
            .QueryTokenAsync(
                new PsbcTokenQuery(VendorFamily, candidate.InstanceId),
                cancellationToken)
            .ConfigureAwait(false);
        if (!token.IsPresent)
        {
            return EnvironmentUnavailable(
                candidate,
                UsbKeyDiagnosticCode.TokenUnavailable);
        }

        return UsbKeyDetectionResult.Ready(
            VendorFamily,
            candidate.InstanceId,
            token.TokenIdentity,
            token.ContainerIdentities);
    }

    private UsbKeyDetectionResult EnvironmentUnavailable(
        UsbDeviceDescriptor candidate,
        UsbKeyDiagnosticCode diagnosticCode) =>
        UsbKeyDetectionResult.EnvironmentUnavailable(
            VendorFamily,
            diagnosticCode,
            candidate.InstanceId);
}

internal static class PsbcUsbKeyCandidateMatcher
{
    public static bool HasHardwareId(
        UsbDeviceDescriptor device,
        string vendorId,
        string productId) =>
        GetHardwareIdentifiers(device).Any(identifier =>
            identifier.Contains(
                $"VID_{vendorId}",
                StringComparison.OrdinalIgnoreCase) &&
            identifier.Contains(
                $"PID_{productId}",
                StringComparison.OrdinalIgnoreCase));

    public static bool HasVendorId(UsbDeviceDescriptor device, string vendorId) =>
        GetHardwareIdentifiers(device).Any(identifier =>
            identifier.Contains(
                $"VID_{vendorId}",
                StringComparison.OrdinalIgnoreCase));

    public static bool ContainsAny(
        UsbDeviceDescriptor device,
        IReadOnlyList<string> markers) =>
        ContainsAny(
            device.HardwareIds
                .Append(device.BusReportedDescription)
                .Concat(device.CdRomNames)
                .Concat(device.VolumeLabels)
                .OfType<string>(),
            markers);

    public static bool ContainsAny(
        IEnumerable<string> values,
        IReadOnlyList<string> markers) =>
        values.Any(value => markers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> GetHardwareIdentifiers(
        UsbDeviceDescriptor device)
    {
        if (device.HardwareIds.Count > 0)
        {
            return device.HardwareIds;
        }

        var separator = device.InstanceId.IndexOf(
            '\\',
            "USB\\".Length);
        return
        [
            separator >= 0
                ? device.InstanceId[..separator]
                : device.InstanceId
        ];
    }
}
