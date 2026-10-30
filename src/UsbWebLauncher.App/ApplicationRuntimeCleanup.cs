using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.App;

internal sealed record ApplicationRuntimeCleanupSnapshot(
    BusinessSessionScope? Session,
    IReadOnlyList<CleanupStage> Stages,
    Action ClearSession);

internal sealed class ApplicationRuntimeCleanup
{
    private readonly object _sync = new();
    private Task? _run;

    public Task RunAsync(
        Func<ApplicationRuntimeCleanupSnapshot> capture,
        Action<CleanupStageObservation>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        lock (_sync)
        {
            return _run ??= RunCoreAsync(capture(), observe);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_run is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "Runtime cleanup cannot be reset while it is running.");
            }

            _run = null;
        }
    }

    private static async Task RunCoreAsync(
        ApplicationRuntimeCleanupSnapshot snapshot,
        Action<CleanupStageObservation>? observe)
    {
        try
        {
            await CleanupStageRunner.RunAsync(snapshot.Stages, observe)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                snapshot.Session?.Dispose();
            }
            finally
            {
                snapshot.ClearSession();
            }
        }
    }
}
