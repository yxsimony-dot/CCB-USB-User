using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class PsbcUsbKeyProbeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsProbeResult()
    {
        var expected = UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.Tendyron,
            @"USB\VID_1780&PID_0401\SAMPLE");
        var executor = new PsbcUsbKeyProbeExecutor();

        var result = await executor.ExecuteAsync(
            new DelegateProbe((_, _, _) => Task.FromResult(expected)),
            [],
            new StubEnvironmentQuery(),
            TimeSpan.FromSeconds(1));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ExecuteAsync_MapsCallerCancellationWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new PsbcUsbKeyProbeExecutor();

        var result = await executor.ExecuteAsync(
            new DelegateProbe(async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return UsbKeyDetectionResult.Absent();
            }),
            [],
            new StubEnvironmentQuery(),
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        Assert.Equal(UsbKeyDetectionStatus.Absent, result.Status);
        Assert.Equal(UsbKeyDiagnosticCode.ProbeCanceled, result.DiagnosticCode);
    }

    [Fact]
    public async Task ExecuteAsync_MapsTimeoutWithoutThrowing()
    {
        var executor = new PsbcUsbKeyProbeExecutor();

        var result = await executor.ExecuteAsync(
            new DelegateProbe((_, _, _) =>
                new TaskCompletionSource<UsbKeyDetectionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task),
            [],
            new StubEnvironmentQuery(),
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            result.Status);
        Assert.Equal(UsbKeyDiagnosticCode.ProbeTimedOut, result.DiagnosticCode);
    }

    [Fact]
    public async Task ExecuteAsync_MapsProbeExceptionWithoutThrowing()
    {
        var executor = new PsbcUsbKeyProbeExecutor();

        var result = await executor.ExecuteAsync(
            new DelegateProbe((_, _, _) =>
                Task.FromException<UsbKeyDetectionResult>(
                    new InvalidOperationException("probe failed"))),
            [],
            new StubEnvironmentQuery(),
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            result.Status);
        Assert.Equal(UsbKeyDiagnosticCode.ProbeFailed, result.DiagnosticCode);
    }

    private sealed class DelegateProbe(
        Func<
            IReadOnlyList<UsbDeviceDescriptor>,
            IPsbcUsbKeyEnvironmentQuery,
            CancellationToken,
            Task<UsbKeyDetectionResult>> probe) : IPsbcUsbKeyProbe
    {
        public UsbKeyVendorFamily VendorFamily => UsbKeyVendorFamily.Tendyron;

        public Task<UsbKeyDetectionResult> ProbeAsync(
            IReadOnlyList<UsbDeviceDescriptor> devices,
            IPsbcUsbKeyEnvironmentQuery environmentQuery,
            CancellationToken cancellationToken = default) =>
            probe(devices, environmentQuery, cancellationToken);
    }

    private sealed class StubEnvironmentQuery : IPsbcUsbKeyEnvironmentQuery
    {
        public Task<PsbcServiceState> GetServiceStateAsync(
            string serviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PsbcServiceState.NotInstalled);

        public Task<bool> IsCryptographicProviderRegisteredAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsRegistryMarkerPresentAsync(
            PsbcRegistryMarker marker,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<PsbcTokenEvidence> QueryTokenAsync(
            PsbcTokenQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PsbcTokenEvidence(false));
    }
}
