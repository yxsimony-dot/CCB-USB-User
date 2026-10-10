namespace UsbWebLauncher.App;

/// <summary>
/// A responsive UI gets a cleanup timeout before the hard deadline. If UI/native
/// cleanup blocks synchronously, the independent watchdog exits this process.
/// Every stop owns its own deadline; disposing it cannot disarm application close.
/// </summary>
internal sealed class BusinessSessionStopDeadline : IDisposable
{
    private readonly ApplicationExitWatchdog watchdog;
    private readonly CancellationTokenSource budget;

    public BusinessSessionStopDeadline(TimeSpan? hardTimeout = null, TimeSpan? cleanupTimeout = null)
    {
        var hard = hardTimeout ?? ApplicationExitWatchdog.DefaultTimeout;
        var cleanup = cleanupTimeout ?? TimeSpan.FromMilliseconds(7500);
        if (cleanup <= TimeSpan.Zero || cleanup >= hard)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

        watchdog = new ApplicationExitWatchdog(hard);
        budget = new CancellationTokenSource();
        watchdog.Arm();
        budget.CancelAfter(cleanup);
    }

    public CancellationToken Token => budget.Token;

    public void Dispose()
    {
        watchdog.Dispose();
        budget.Dispose();
    }
}
