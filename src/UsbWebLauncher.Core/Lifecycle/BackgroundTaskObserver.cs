namespace UsbWebLauncher.Core.Lifecycle;

public sealed record BackgroundTaskFault(string Operation, string ExceptionType);

public static class BackgroundTaskObserver
{
    public static void Observe(
        Task task,
        string operation,
        Action<BackgroundTaskFault>? report = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _ = ObserveCoreAsync(task, operation, report);
    }

    internal static async Task ObserveCoreAsync(
        Task task,
        string operation,
        Action<BackgroundTaskFault>? report)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
        }
        catch (Exception exception)
        {
            try
            {
                report?.Invoke(new BackgroundTaskFault(
                    operation,
                    exception.GetType().Name));
            }
            catch
            {
                // Reporting must not create another unobserved background failure.
            }
        }
    }
}
