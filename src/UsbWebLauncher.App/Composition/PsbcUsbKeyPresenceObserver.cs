using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

internal sealed class PsbcUsbKeyPresenceObserver(
    PsbcUsbKeyDeviceMonitor monitor,
    Action<UsbKeyDetectionStatus?> publish) : IUsbKeyPresenceObserver
{
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
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
            if (disposed)
            {
                return Task.CompletedTask;
            }

            if (startup is not null)
            {
                return startup;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            startup = completion.Task;
            initialVersion = version;
            monitor.DetectionChanged += OnDetectionChanged;
        }

        _ = StartCoreAsync(completion, initialVersion, cancellationToken);
        return completion.Task;
    }

    private async Task StartCoreAsync(
        TaskCompletionSource completion,
        long initialVersion,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        try
        {
            await monitor.StartAsync(linked.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                initialVersion = version;
            }

            var snapshot = await monitor
                .ReadDetectionAsync(linked.Token)
                .ConfigureAwait(false);
            PublishIfCurrent(snapshot.Status, initialVersion);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch
        {
            PublishIfCurrent(status: null, initialVersion);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private void OnDetectionChanged(
        object? sender,
        PsbcUsbKeyDetectionChangedEventArgs args)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            version++;
            PublishUnderLock(args.Error is null ? args.Snapshot?.Status : null);
        }
    }

    private void PublishIfCurrent(
        UsbKeyDetectionStatus? status,
        long initialVersion)
    {
        lock (sync)
        {
            if (disposed || initialVersion != version)
            {
                return;
            }

            PublishUnderLock(status);
        }
    }

    private void PublishUnderLock(UsbKeyDetectionStatus? status)
    {
        try
        {
            publish(status);
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task started;
        lock (sync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }

            disposed = true;
            monitor.DetectionChanged -= OnDetectionChanged;
            started = startup ?? Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }

        _ = DisposeCoreAsync(completion, started);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(
        TaskCompletionSource completion,
        Task started)
    {
        try
        {
            var cancellation = lifetime.CancelAsync();
            await Task.WhenAll(
                    cancellation,
                    started,
                    monitor.DisposeAsync().AsTask())
                .ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
