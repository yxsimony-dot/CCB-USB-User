using System.Diagnostics;

namespace UsbWebLauncher.Core.Lifecycle;

public readonly record struct CleanupStage
{
    public CleanupStage(string name, Func<ValueTask> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);
        Name = name;
        Execute = execute;
    }

    public string Name { get; }

    public Func<ValueTask> Execute { get; }
}

public enum CleanupStageStatus
{
    Started,
    Completed,
    Failed
}

public sealed record CleanupStageObservation(
    string Stage,
    CleanupStageStatus Status,
    TimeSpan Elapsed,
    string? ExceptionType = null);

public sealed record CleanupStageFailure(string Stage, string ExceptionType);

public sealed class CleanupStageAggregateException : Exception
{
    public CleanupStageAggregateException(IReadOnlyList<CleanupStageFailure> failures)
        : base(CreateMessage(failures))
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("At least one cleanup failure is required.", nameof(failures));
        }

        Failures = failures.ToArray();
    }

    public IReadOnlyList<CleanupStageFailure> Failures { get; }

    private static string CreateMessage(IReadOnlyList<CleanupStageFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? "Runtime cleanup failed."
            : $"Runtime cleanup failed in {failures.Count} stage(s): " +
              string.Join(", ", failures.Select(failure =>
                  $"{failure.Stage} ({failure.ExceptionType})"));
    }
}

public static class CleanupStageRunner
{
    public static async Task RunAsync(
        IEnumerable<CleanupStage> stages,
        Action<CleanupStageObservation>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var failures = new List<CleanupStageFailure>();
        foreach (var stage in stages)
        {
            var clock = Stopwatch.StartNew();
            Observe(observe, new CleanupStageObservation(
                stage.Name,
                CleanupStageStatus.Started,
                TimeSpan.Zero));
            try
            {
                await stage.Execute().ConfigureAwait(false);
                Observe(observe, new CleanupStageObservation(
                    stage.Name,
                    CleanupStageStatus.Completed,
                    clock.Elapsed));
            }
            catch (Exception exception)
            {
                var failure = new CleanupStageFailure(
                    stage.Name,
                    exception.GetType().Name);
                failures.Add(failure);
                Observe(observe, new CleanupStageObservation(
                    stage.Name,
                    CleanupStageStatus.Failed,
                    clock.Elapsed,
                    failure.ExceptionType));
            }
        }

        if (failures.Count > 0)
        {
            throw new CleanupStageAggregateException(failures);
        }
    }

    private static void Observe(
        Action<CleanupStageObservation>? observe,
        CleanupStageObservation observation)
    {
        try
        {
            observe?.Invoke(observation);
        }
        catch
        {
            // Diagnostics cannot change cleanup ownership or completion.
        }
    }
}
