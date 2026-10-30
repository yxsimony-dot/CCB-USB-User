namespace UsbWebLauncher.App.Composition;

// The ticket is invalidated before cancellation callbacks run. UI work already queued
// by an old session must check IsCurrent again when it is actually executed.
internal sealed class BusinessSessionScope : IDisposable
{
    private static long nextId;
    private static int liveCount;
    private readonly CancellationTokenSource cancellation;
    private int disposed;
    private int revoked;
    private readonly object sync = new();
    private Task? revocation;

    public BusinessSessionScope(CancellationToken applicationToken)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        Interlocked.Increment(ref liveCount);
    }

    internal static int LiveCount => Volatile.Read(ref liveCount);
    public long Id { get; } = Interlocked.Increment(ref nextId);
    public CancellationToken Token => cancellation.Token;
    public bool IsCurrent => Volatile.Read(ref revoked) == 0 && !cancellation.IsCancellationRequested;
    public Task RevokeAsync()
    {
        Interlocked.Exchange(ref revoked, 1);
        lock (sync)
        {
            // CancellationTokenSource.CancelAsync may return a completed task
            // to a repeated caller while the first callbacks are still running.
            return revocation ??= cancellation.CancelAsync();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            cancellation.Dispose();
        }
        finally
        {
            Interlocked.Decrement(ref liveCount);
        }
    }
}
