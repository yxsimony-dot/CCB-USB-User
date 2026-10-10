namespace UsbWebLauncher.App.Browser;

internal interface IBrowserTimeoutScheduler
{
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class BrowserTimeoutScheduler : IBrowserTimeoutScheduler
{
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Delay(timeout, cancellationToken);
}
