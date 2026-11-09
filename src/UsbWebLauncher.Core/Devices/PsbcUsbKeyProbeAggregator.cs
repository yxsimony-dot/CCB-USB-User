namespace UsbWebLauncher.Core.Devices;

public interface IPsbcUsbKeyDetector
{
    Task<IReadOnlyList<UsbKeyDetectionResult>> DetectAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        CancellationToken cancellationToken = default);
}

public sealed class PsbcUsbKeyProbeAggregator : IPsbcUsbKeyDetector
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IPsbcUsbKeyProbe> _probes;
    private readonly IPsbcUsbKeyEnvironmentQuery _environmentQuery;
    private readonly IPsbcUsbKeyProbeExecutor _executor;
    private readonly TimeSpan _probeTimeout;

    public PsbcUsbKeyProbeAggregator(IPsbcUsbKeyEnvironmentQuery environmentQuery)
        : this(
            PsbcUsbKeyVendorProbes.All,
            environmentQuery,
            new PsbcUsbKeyProbeExecutor(),
            DefaultProbeTimeout)
    {
    }

    public PsbcUsbKeyProbeAggregator(
        IReadOnlyList<IPsbcUsbKeyProbe> probes,
        IPsbcUsbKeyEnvironmentQuery environmentQuery,
        IPsbcUsbKeyProbeExecutor executor,
        TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
        _environmentQuery = environmentQuery ??
            throw new ArgumentNullException(nameof(environmentQuery));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _probeTimeout = probeTimeout > TimeSpan.Zero
            ? probeTimeout
            : throw new ArgumentOutOfRangeException(nameof(probeTimeout));
    }

    public async Task<IReadOnlyList<UsbKeyDetectionResult>> DetectAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var executions = _probes.Select(probe =>
            _executor.ExecuteAsync(
                probe,
                devices,
                _environmentQuery,
                _probeTimeout,
                cancellationToken));
        var results = await Task.WhenAll(executions).ConfigureAwait(false);

        return results
            .Where(result =>
                result.Status != UsbKeyDetectionStatus.Absent &&
                !string.IsNullOrWhiteSpace(result.PhysicalInstanceId))
            .GroupBy(
                result => result.PhysicalInstanceId!,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => GetReadinessRank(result.Status))
                .First())
            .ToArray();
    }

    private static int GetReadinessRank(UsbKeyDetectionStatus status) => status switch
    {
        UsbKeyDetectionStatus.Ready => 2,
        UsbKeyDetectionStatus.PresentButEnvironmentUnavailable => 1,
        _ => 0
    };
}
