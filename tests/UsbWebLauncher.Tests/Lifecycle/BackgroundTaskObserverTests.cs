using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.Tests.Lifecycle;

public sealed class BackgroundTaskObserverTests
{
    [Fact]
    public async Task ObserveCoreAsync_ReportsOrdinaryFaultOnceWithoutItsMessage()
    {
        const string secret = "pin=483921";
        var faults = new List<BackgroundTaskFault>();

        await BackgroundTaskObserver.ObserveCoreAsync(
            Task.FromException(new IOException(secret)),
            "startup-update-check",
            faults.Add);

        var fault = Assert.Single(faults);
        Assert.Equal("startup-update-check", fault.Operation);
        Assert.Equal(nameof(IOException), fault.ExceptionType);
        Assert.DoesNotContain(secret, fault.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveCoreAsync_IgnoresExpectedCancellation()
    {
        var faults = new List<BackgroundTaskFault>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await BackgroundTaskObserver.ObserveCoreAsync(
            Task.FromCanceled(cancellation.Token),
            "presence-observer-start",
            faults.Add);

        Assert.Empty(faults);
    }

    [Fact]
    public async Task Observe_WhenReporterThrows_DoesNotRaiseUnobservedWork()
    {
        await BackgroundTaskObserver.ObserveCoreAsync(
            Task.FromException(new InvalidOperationException("failure")),
            "session-revocation",
            _ => throw new IOException("report failure"));
    }
}
