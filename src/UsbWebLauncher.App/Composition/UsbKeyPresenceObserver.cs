using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

// Application-level observation only. This object never grants business or
// authentication permission and is independent of each business-session monitor.
internal sealed class UsbKeyPresenceObserver(
    IUsbDeviceMonitor inner,
    Func<UsbDeviceDescriptor, bool> matches,
    Action<bool?> publish) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationToken callerToken;
    private long version;
    private bool disposed;
    private Task? startup;
    private Task? disposal;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        long initialVersion;
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (startup is not null) return startup;
            callerToken = cancellationToken;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            startup = completion.Task;
            initialVersion = version;
            inner.DevicesChanged += OnDevicesChangedAsync;
        }
        _ = StartCoreAsync(completion, initialVersion);
        return completion.Task;
    }

    private async Task StartCoreAsync(TaskCompletionSource completion, long initialVersion)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await inner.StartAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            Task<IReadOnlyList<UsbDeviceDescriptor>> reading;
            lock (sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (disposed) return;
                // Startup may itself publish a snapshot. The explicit read that
                // begins afterwards is newer; only events during that read win.
                initialVersion = version;
                reading = inner.GetConnectedDevicesAsync(linked.Token);
            }
            var devices = await reading.WaitAsync(linked.Token).ConfigureAwait(false);
            PublishIfCurrent(devices.Any(matches), initialVersion);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { PublishIfCurrent(null, initialVersion); }
        finally { completion.TrySetResult(); }
    }

    private Task OnDevicesChangedAsync(object? sender, UsbDevicesChangedEventArgs args)
    {
        lock (sync)
        {
            if (disposed || callerToken.IsCancellationRequested || !ReferenceEquals(sender, inner))
                return Task.CompletedTask;
            version++;
            bool? present;
            try { present = args.Error is null ? args.Devices.Any(matches) : null; }
            catch { present = null; }
            PublishUnderLock(present);
        }
        return Task.CompletedTask;
    }

    private void PublishIfCurrent(bool? present, long initialVersion)
    {
        lock (sync)
        {
            if (disposed || callerToken.IsCancellationRequested || initialVersion != version) return;
            PublishUnderLock(present);
        }
    }

    private void PublishUnderLock(bool? present)
    {
        // The callback only posts a UI update. Publication and disposal are
        // serialized so a late snapshot cannot post after Dispose was accepted.
        try { publish(present); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task started;
        lock (sync)
        {
            if (disposal is not null) return new ValueTask(disposal);
            disposed = true;
            inner.DevicesChanged -= OnDevicesChangedAsync;
            started = startup ?? Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = DisposeCoreAsync(completion, started);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion, Task started)
    {
        try
        {
            var cancellation = lifetime.CancelAsync();
            // Release the monitor concurrently, because a startup read may need
            // its source to be stopped before it can return.
            await Task.WhenAll(cancellation, started, DisposeInnerAsync()).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lifetime.Dispose(); }
    }

    private async Task DisposeInnerAsync() => await inner.DisposeAsync().ConfigureAwait(false);
}
