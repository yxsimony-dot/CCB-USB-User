using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.App;

internal sealed class LifecycleObservationReport
{
    public int SchemaVersion { get; init; } = 1;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public bool Success => Status == "passed";
    public LifecycleObservationTolerances Tolerances { get; init; } = new();
    public LifecycleSessionDirectoryCounts InitialSessionDirectories { get; init; } = new(0, 0);
    public List<LifecycleObservationPhase> Phases { get; } = [];
    public List<LifecycleObservationCycle> Cycles { get; } = [];
    public List<string> Failures { get; } = [];
    public LifecycleResourceDelta? StoppedResourceDelta { get; set; }
}

internal sealed record LifecycleObservationPhase(
    int Cycle,
    string Name,
    DateTimeOffset ObservedAtUtc);

internal sealed class LifecycleObservationCycle
{
    public int Cycle { get; init; }
    public LifecycleResourceSnapshot? Started { get; set; }
    public LifecycleResourceSnapshot? Stopped { get; set; }
    public uint? BrowserProcessId { get; set; }
    public uint? ExitedBrowserProcessId { get; set; }
    public bool BrowserProcessExitedWithinTimeout { get; set; }
}

internal sealed record LifecycleResourceSnapshot(
    DateTimeOffset CapturedAtUtc,
    long PrivateBytes,
    long ManagedHeapBytes,
    int HandleCount,
    int LiveBusinessSessionOwners,
    int ActiveBrowserSessionDirectories,
    int RetiredBrowserSessionDirectories);

internal sealed record LifecycleSessionDirectoryCounts(int Active, int Retired);

internal sealed record LifecycleResourceDelta(
    long PrivateBytes,
    long ManagedHeapBytes,
    int HandleCount);

internal sealed record LifecycleObservationTolerances
{
    public int CycleCount { get; init; } = 3;
    public int BrowserInitializationTimeoutSeconds { get; init; } = 45;
    public int BrowserExitTimeoutSeconds { get; init; } = 15;
    public int DirectoryCleanupTimeoutSeconds { get; init; } = 5;
    public long MaximumStoppedPrivateBytesGrowth { get; init; } = 96L * 1024 * 1024;
    public long MaximumStoppedManagedHeapGrowth { get; init; } = 16L * 1024 * 1024;
    public int MaximumStoppedHandleGrowth { get; init; } = 128;
}

internal static class LifecycleObservation
{
    private static readonly object ReportWriteSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetBrowserSessionRoot(string reportPath) =>
        Path.GetFullPath(reportPath) + ".browser-sessions";

    public static LifecycleResourceSnapshot CaptureSnapshot(string browserSessionRoot)
    {
        var managedHeap = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var directories = CaptureSessionDirectories(browserSessionRoot);
        return new(
            DateTimeOffset.UtcNow,
            process.PrivateMemorySize64,
            managedHeap,
            process.HandleCount,
            BusinessSessionScope.LiveCount,
            directories.Active,
            directories.Retired);
    }

    public static LifecycleSessionDirectoryCounts CaptureSessionDirectories(string root)
    {
        if (!Directory.Exists(root)) return new(0, 0);

        var active = 0;
        var retired = 0;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var ownershipMarker = Path.Combine(path, BrowserSessionDirectory.OwnershipFileName);
            if (!File.Exists(ownershipMarker)) continue;

            var retiredMarker = Path.Combine(path, BrowserSessionDirectory.RetiredFileName);
            if (File.Exists(retiredMarker)) retired++;
            else active++;
        }

        return new(active, retired);
    }

    public static async Task WaitForDirectoryCleanupAsync(
        string root,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            BrowserSessionDirectory.CleanupRetired(root);
            var counts = CaptureSessionDirectories(root);
            if (counts.Active == 0 && counts.Retired == 0) return;
            if (DateTimeOffset.UtcNow >= deadline) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public static IReadOnlyList<string> Validate(LifecycleObservationReport report)
    {
        var failures = new List<string>();
        var tolerances = report.Tolerances;
        if (report.Cycles.Count != tolerances.CycleCount)
        {
            failures.Add(
                $"Expected {tolerances.CycleCount} lifecycle cycles, observed {report.Cycles.Count}.");
        }

        if (report.InitialSessionDirectories.Active != 0)
        {
            failures.Add("The observation root contained an active browser session before the run.");
        }

        foreach (var cycle in report.Cycles)
        {
            if (cycle.Started is null || cycle.Stopped is null)
            {
                failures.Add($"Cycle {cycle.Cycle} did not record both resource snapshots.");
                continue;
            }

            if (cycle.BrowserProcessId is null)
                failures.Add($"Cycle {cycle.Cycle} did not record a browser process.");
            if (!cycle.BrowserProcessExitedWithinTimeout)
                failures.Add($"Cycle {cycle.Cycle} browser process did not exit within the timeout.");
            if (cycle.BrowserProcessId != cycle.ExitedBrowserProcessId)
                failures.Add($"Cycle {cycle.Cycle} observed a different browser process exit.");
            if (cycle.Started.LiveBusinessSessionOwners != 1)
                failures.Add($"Cycle {cycle.Cycle} did not have exactly one live session owner while started.");
            if (cycle.Started.ActiveBrowserSessionDirectories != 1)
                failures.Add($"Cycle {cycle.Cycle} did not have exactly one active browser session directory.");
            if (cycle.Stopped.LiveBusinessSessionOwners != 0)
                failures.Add($"Cycle {cycle.Cycle} retained a live session owner after stop.");
            if (cycle.Stopped.ActiveBrowserSessionDirectories != 0)
                failures.Add($"Cycle {cycle.Cycle} retained an active browser session directory after stop.");
            if (cycle.Stopped.RetiredBrowserSessionDirectories >
                report.InitialSessionDirectories.Retired)
            {
                failures.Add($"Cycle {cycle.Cycle} accumulated a retired browser session directory.");
            }
        }

        var firstStopped = report.Cycles.FirstOrDefault()?.Stopped;
        var finalStopped = report.Cycles.LastOrDefault()?.Stopped;
        if (firstStopped is not null && finalStopped is not null)
        {
            var delta = new LifecycleResourceDelta(
                finalStopped.PrivateBytes - firstStopped.PrivateBytes,
                finalStopped.ManagedHeapBytes - firstStopped.ManagedHeapBytes,
                finalStopped.HandleCount - firstStopped.HandleCount);
            report.StoppedResourceDelta = delta;
            if (delta.PrivateBytes > tolerances.MaximumStoppedPrivateBytesGrowth)
                failures.Add($"Stopped private bytes grew by {delta.PrivateBytes} bytes.");
            if (delta.ManagedHeapBytes > tolerances.MaximumStoppedManagedHeapGrowth)
                failures.Add($"Stopped managed heap grew by {delta.ManagedHeapBytes} bytes.");
            if (delta.HandleCount > tolerances.MaximumStoppedHandleGrowth)
                failures.Add($"Stopped handle count grew by {delta.HandleCount}.");
        }

        return failures;
    }

    public static Task WriteReportAsync(string reportPath, LifecycleObservationReport report)
    {
        WriteReport(reportPath, report);
        return Task.CompletedTask;
    }

    public static void WriteReport(string reportPath, LifecycleObservationReport report)
    {
        reportPath = Path.GetFullPath(reportPath);
        lock (ReportWriteSync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var temporaryPath = reportPath + $".{Environment.ProcessId}.tmp";
            var json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, reportPath, overwrite: true);
        }
    }
}
