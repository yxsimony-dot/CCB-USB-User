namespace UsbWebLauncher.App.Composition;

// The ticket is invalidated before cancellation callbacks run. UI work already queued
// by an old session must check IsCurrent again when it is actually executed.
internal sealed class BusinessSessionScope(CancellationToken applicationToken) : IDisposable
{
    private static long nextId;
    private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
    private int revoked;
    private readonly object sync = new();
    private Task? revocation;
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
    public void Dispose() => cancellation.Dispose();
}
