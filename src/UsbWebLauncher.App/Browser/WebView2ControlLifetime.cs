namespace UsbWebLauncher.App.Browser;

internal sealed class WebView2ControlLifetime : IDisposable
{
    private int _disposed;

    public void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            nameof(WpfWebView2Control));

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
