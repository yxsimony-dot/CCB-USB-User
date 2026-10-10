namespace UsbWebLauncher.App.Browser;

internal sealed class PopupFrameRegistry<TFrame> : IDisposable where TFrame : class
{
    private readonly HashSet<TFrame> frames = new(ReferenceEqualityComparer.Instance);
    private bool disposed;

    public bool Register(TFrame frame, Action<Action> subscribeDestroyed)
    {
        if (disposed || !frames.Add(frame)) return false;
        try
        {
            // Native Destroyed may report a different managed wrapper as sender.
            // Capture the subscribed frame and only release managed ownership.
            subscribeDestroyed(() => frames.Remove(frame));
        }
        catch
        {
            frames.Remove(frame);
            throw;
        }
        return true;
    }

    public void Dispatch(TFrame frame, Action action)
    {
        if (!disposed && frames.Contains(frame)) action();
    }

    public void Dispose()
    {
        disposed = true;
        frames.Clear();
    }
}
