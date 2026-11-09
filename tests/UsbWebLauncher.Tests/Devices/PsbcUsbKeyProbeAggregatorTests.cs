using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class PsbcUsbKeyProbeAggregatorTests
{
    [Fact]
    public async Task DetectAsync_ReturnsNoCandidatesWhenEveryProbeIsAbsent()
    {
        var aggregator = CreateAggregator(
            new DelegateProbe(
                UsbKeyVendorFamily.FeitianEnterSafe,
                UsbKeyDetectionResult.Absent(
                    UsbKeyVendorFamily.FeitianEnterSafe)),
            new DelegateProbe(
                UsbKeyVendorFamily.Tendyron,
                UsbKeyDetectionResult.Absent(UsbKeyVendorFamily.Tendyron)));

        var results = await aggregator.DetectAsync([]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DetectAsync_ReturnsReadyCandidate()
    {
        var expected = UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.Tendyron,
            @"USB\VID_1780&PID_0401\CURRENT");
        var aggregator = CreateAggregator(
            new DelegateProbe(UsbKeyVendorFamily.Tendyron, expected));

        var result = Assert.Single(await aggregator.DetectAsync([]));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DetectAsync_PreservesEnvironmentUnavailableCandidate()
    {
        var expected = UsbKeyDetectionResult.EnvironmentUnavailable(
            UsbKeyVendorFamily.Hdzb,
            UsbKeyDiagnosticCode.ServiceUnavailable,
            @"USB\VID_1677&PID_6004\HDZB");
        var aggregator = CreateAggregator(
            new DelegateProbe(UsbKeyVendorFamily.Hdzb, expected));

        var result = Assert.Single(await aggregator.DetectAsync([]));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DetectAsync_DeduplicatesPhysicalDeviceAndPrefersReady()
    {
        const string physicalInstanceId = @"USB\VID_1780&PID_0401\SHARED";
        var aggregator = CreateAggregator(
            new DelegateProbe(
                UsbKeyVendorFamily.FeitianEnterSafe,
                UsbKeyDetectionResult.EnvironmentUnavailable(
                    UsbKeyVendorFamily.FeitianEnterSafe,
                    UsbKeyDiagnosticCode.ProviderUnavailable,
                    physicalInstanceId)),
            new DelegateProbe(
                UsbKeyVendorFamily.Tendyron,
                UsbKeyDetectionResult.Ready(
                    UsbKeyVendorFamily.Tendyron,
                    physicalInstanceId)));

        var result = Assert.Single(await aggregator.DetectAsync([]));

        Assert.Equal(UsbKeyDetectionStatus.Ready, result.Status);
        Assert.Equal(UsbKeyVendorFamily.Tendyron, result.VendorFamily);
    }

    [Fact]
    public async Task DetectAsync_IsolatesAdapterFailure()
    {
        var expected = UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.WatchData,
            @"USB\VID_163C&PID_0620\WATCH");
        var aggregator = CreateAggregator(
            new ThrowingProbe(UsbKeyVendorFamily.FeitianEnterSafe),
            new DelegateProbe(UsbKeyVendorFamily.WatchData, expected));

        var result = Assert.Single(await aggregator.DetectAsync([]));

        Assert.Equal(expected, result);
    }

    private static PsbcUsbKeyProbeAggregator CreateAggregator(
        params IPsbcUsbKeyProbe[] probes) =>
        new(
            probes,
            new ReadyEnvironmentQuery(),
            new PsbcUsbKeyProbeExecutor(),
            TimeSpan.FromSeconds(1));

    private sealed class DelegateProbe(
        UsbKeyVendorFamily vendorFamily,
        UsbKeyDetectionResult result) : IPsbcUsbKeyProbe
    {
        public UsbKeyVendorFamily VendorFamily => vendorFamily;

        public Task<UsbKeyDetectionResult> ProbeAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            IPsbcUsbKeyEnvironmentQuery environmentQuery,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingProbe(UsbKeyVendorFamily vendorFamily)
        : IPsbcUsbKeyProbe
    {
        public UsbKeyVendorFamily VendorFamily => vendorFamily;

        public Task<UsbKeyDetectionResult> ProbeAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            IPsbcUsbKeyEnvironmentQuery environmentQuery,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("fixture failure");
    }

    private sealed class ReadyEnvironmentQuery : IPsbcUsbKeyEnvironmentQuery
    {
        public Task<PsbcServiceState> GetServiceStateAsync(
            string serviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PsbcServiceState.Running);

        public Task<bool> IsCryptographicProviderRegisteredAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsRegistryMarkerPresentAsync(
            PsbcRegistryMarker marker,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<PsbcTokenEvidence> QueryTokenAsync(
            PsbcTokenQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PsbcTokenEvidence(true));
    }
}
