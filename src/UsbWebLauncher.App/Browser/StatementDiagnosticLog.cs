using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace UsbWebLauncher.App.Browser;

// Best-effort local diagnostics: bounded queue and file; no synchronous disk I/O on the UI thread.
public sealed class StatementDiagnosticLog : IDisposable
{
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private int _closed;
    private int _dropped;

    public StatementDiagnosticLog(string path, int maxBytes = 2 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 256);
        Completion = Task.Run(() => WriteAsync(path, maxBytes));
    }

    public Task Completion { get; }
    public int DroppedRecords => Volatile.Read(ref _dropped);
    public bool IsAccepting => Volatile.Read(ref _closed) == 0;

    public bool TryWrite(string eventName, object data)
    {
        if (!IsAccepting) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now, eventName, data
            }) + "\n");
            if (bytes.Length <= 128 * 1024 && _queue.Writer.TryWrite(bytes)) return true;
        }
        catch { }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    private async Task WriteAsync(string path, int maxBytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            var limit = Encoding.UTF8.GetBytes("{\"eventName\":\"capture-stopped\",\"data\":{\"reason\":\"file-limit\"}}\n");
            var total = 0;
            await foreach (var record in _queue.Reader.ReadAllAsync())
            {
                if (total + record.Length > maxBytes - limit.Length)
                {
                    Dispose();
                    await file.WriteAsync(limit);
                    break;
                }
                await file.WriteAsync(record);
                await file.FlushAsync();
                total += record.Length;
            }
        }
        catch { /* Failure is local to diagnostics; never emit paths or exception text. */ }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _queue.Writer.TryComplete();
    }
}
