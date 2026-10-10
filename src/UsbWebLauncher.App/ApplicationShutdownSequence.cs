using System.Diagnostics;

namespace UsbWebLauncher.App;

internal sealed class ApplicationShutdownSequence(
    Func<Task> cleanup,
    Action closeWindow,
    Action<int> shutdownApplication,
    TimeSpan? cleanupTimeout = null)
{
    private readonly object _sync = new();
    private Task? _run;

    public Task RunAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_run is not null)
            {
                return _run;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _run = completion.Task;
        }

        // Publish the shared completion first, including for synchronous reentry
        // from a cancellation callback or Window.Close event.
        _ = RunCoreAsync(completion);
        return completion.Task;
    }

    private async Task RunCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            var timeout = cleanupTimeout ?? ApplicationExitWatchdog.DefaultTimeout;
            if (timeout > TimeSpan.Zero)
            {
                var started = Stopwatch.GetTimestamp();
                var pending = cleanup();
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                await pending.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(true);
            }
        }
        catch
        {
        }

        try
        {
            closeWindow();
        }
        catch
        {
        }

        try
        {
            shutdownApplication(0);
        }
        catch
        {
        }

        completion.TrySetResult();
    }
}
