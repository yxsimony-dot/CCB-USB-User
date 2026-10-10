namespace UsbWebLauncher.App.Browser;

internal sealed class UiThreadResourceDisposer(
    Func<bool> checkAccess,
    Func<Action, Task> dispatchAsync,
    Action cleanup) : IAsyncDisposable
{
    private readonly object _sync = new();
    private Task? _disposeTask;

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        // Publish the shared task before invoking COM or dispatching. Either can
        // synchronously reenter disposal, and no caller should wait on our lock
        // while UI cleanup is running.
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            if (checkAccess())
            {
                cleanup();
            }
            else await dispatchAsync(cleanup).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
