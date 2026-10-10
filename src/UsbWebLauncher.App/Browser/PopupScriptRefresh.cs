namespace UsbWebLauncher.App.Browser;

internal sealed class PopupScriptRefresh(Func<Task<string>> execute, Action<string> report) : IDisposable
{
    private int _busy;
    private volatile bool _disposed;

    public async Task RunAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var result = await execute();
            if (!_disposed && result == "\"installed\"") report("popup-bootstrap-ready");
        }
        catch { /* Refresh failures must not affect navigation or log page payloads. */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _disposed = true;
}
