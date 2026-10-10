using System.Text.Json;

namespace UsbWebLauncher.App.Browser;

// Best-effort diagnostics are independent of login and business readiness.
public sealed class PopupDiagnosticSampler(Func<Task<string>> read, Func<string, Task> write) : IDisposable
{
    private int _busy;
    private volatile bool _disposed;
    private string? _last;

    public async Task CaptureAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var json = await read();
            if (_disposed || json.Length > 128 * 1024 || json == _last) return;
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "diagnostic") return;
            await write(json);
            _last = json;
        }
        catch { /* Never forward script or exception text, or affect the caller. */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _disposed = true;
}
