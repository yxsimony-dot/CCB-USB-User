namespace UsbWebLauncher.Core.Devices;

public enum PsbcServiceState
{
    NotInstalled,
    Stopped,
    Running
}

public sealed record PsbcRegistryMarker(
    string KeyPath,
    string? ValueName = null,
    string? ExpectedValue = null);

public sealed record PsbcTokenQuery(
    UsbKeyVendorFamily VendorFamily,
    string PhysicalInstanceId);

public sealed record PsbcTokenEvidence(
    bool IsPresent,
    string? TokenIdentity = null);

public interface IPsbcUsbKeyEnvironmentQuery
{
    Task<PsbcServiceState> GetServiceStateAsync(
        string serviceName,
        CancellationToken cancellationToken = default);

    Task<bool> IsCryptographicProviderRegisteredAsync(
        string providerName,
        CancellationToken cancellationToken = default);

    Task<bool> IsRegistryMarkerPresentAsync(
        PsbcRegistryMarker marker,
        CancellationToken cancellationToken = default);

    Task<PsbcTokenEvidence> QueryTokenAsync(
        PsbcTokenQuery query,
        CancellationToken cancellationToken = default);
}

public interface IPsbcUsbKeyProbe
{
    UsbKeyVendorFamily VendorFamily { get; }

    Task<UsbKeyDetectionResult> ProbeAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        IPsbcUsbKeyEnvironmentQuery environmentQuery,
        CancellationToken cancellationToken = default);
}

public interface IPsbcUsbKeyProbeExecutor
{
    Task<UsbKeyDetectionResult> ExecuteAsync(
        IPsbcUsbKeyProbe probe,
        IReadOnlyList<UsbDeviceDescriptor> devices,
        IPsbcUsbKeyEnvironmentQuery environmentQuery,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class PsbcUsbKeyProbeExecutor : IPsbcUsbKeyProbeExecutor
{
    public async Task<UsbKeyDetectionResult> ExecuteAsync(
        IPsbcUsbKeyProbe probe,
        IReadOnlyList<UsbDeviceDescriptor> devices,
        IPsbcUsbKeyEnvironmentQuery environmentQuery,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(environmentQuery);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            return await probe.ProbeAsync(
                    devices,
                    environmentQuery,
                    linkedCancellation.Token)
                .WaitAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UsbKeyDetectionResult.Absent(
                probe.VendorFamily,
                UsbKeyDiagnosticCode.ProbeCanceled);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return UsbKeyDetectionResult.EnvironmentUnavailable(
                probe.VendorFamily,
                UsbKeyDiagnosticCode.ProbeTimedOut,
                physicalInstanceId: null);
        }
        catch
        {
            return UsbKeyDetectionResult.EnvironmentUnavailable(
                probe.VendorFamily,
                UsbKeyDiagnosticCode.ProbeFailed,
                physicalInstanceId: null);
        }
    }
}
