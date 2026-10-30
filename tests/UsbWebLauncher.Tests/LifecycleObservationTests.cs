using UsbWebLauncher.App;
using System.Windows;
using System.Windows.Controls;

namespace UsbWebLauncher.Tests;

public sealed class LifecycleObservationTests
{
    [Fact]
    public async Task BrowserPreparationMakesCollapsedContainerVisible()
    {
        await RunStaAsync(() =>
        {
            var window = new MainWindow();
            var container = Assert.IsType<ContentControl>(
                window.FindName("BrowserContainer"));
            Assert.Equal(Visibility.Collapsed, container.Visibility);

            window.PrepareBrowserForLifecycleObservation();

            Assert.Equal(Visibility.Visible, container.Visibility);
            window.Close();
        });
    }

    [Fact]
    public void Validate_AcceptsBoundedReleasedCycles()
    {
        var report = CreateReport();

        var failures = LifecycleObservation.Validate(report);

        Assert.Empty(failures);
        Assert.Equal(8, report.StoppedResourceDelta?.HandleCount);
        Assert.Equal(8L * 1024 * 1024, report.StoppedResourceDelta?.PrivateBytes);
    }

    [Fact]
    public void Validate_RejectsOwnerDirectoryExitAndResourceAccumulation()
    {
        var report = CreateReport();
        var finalCycle = report.Cycles[^1];
        finalCycle.BrowserProcessExitedWithinTimeout = false;
        finalCycle.ExitedBrowserProcessId = null;
        finalCycle.Stopped = finalCycle.Stopped! with
        {
            PrivateBytes = report.Cycles[0].Stopped!.PrivateBytes +
                report.Tolerances.MaximumStoppedPrivateBytesGrowth + 1,
            ManagedHeapBytes = report.Cycles[0].Stopped!.ManagedHeapBytes +
                report.Tolerances.MaximumStoppedManagedHeapGrowth + 1,
            HandleCount = report.Cycles[0].Stopped!.HandleCount +
                report.Tolerances.MaximumStoppedHandleGrowth + 1,
            LiveBusinessSessionOwners = 1,
            ActiveBrowserSessionDirectories = 1,
            RetiredBrowserSessionDirectories = 1
        };

        var failures = LifecycleObservation.Validate(report);

        Assert.Contains(failures, failure => failure.Contains("did not exit", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("live session owner", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("active browser session", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("retired browser session", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("private bytes", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("managed heap", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("handle count", StringComparison.Ordinal));
    }

    private static LifecycleObservationReport CreateReport()
    {
        var report = new LifecycleObservationReport
        {
            InitialSessionDirectories = new LifecycleSessionDirectoryCounts(0, 0)
        };
        for (var cycle = 1; cycle <= report.Tolerances.CycleCount; cycle++)
        {
            var browserProcessId = checked((uint)(1000 + cycle));
            report.Cycles.Add(new LifecycleObservationCycle
            {
                Cycle = cycle,
                BrowserProcessId = browserProcessId,
                ExitedBrowserProcessId = browserProcessId,
                BrowserProcessExitedWithinTimeout = true,
                Started = Snapshot(
                    privateBytes: 200 * 1024 * 1024,
                    managedHeapBytes: 24 * 1024 * 1024,
                    handles: 400,
                    liveOwners: 1,
                    activeDirectories: 1),
                Stopped = Snapshot(
                    privateBytes: (120 + ((cycle - 1) * 4)) * 1024 * 1024,
                    managedHeapBytes: (12 + cycle) * 1024 * 1024,
                    handles: 200 + ((cycle - 1) * 4),
                    liveOwners: 0,
                    activeDirectories: 0)
            });
        }

        return report;
    }

    private static LifecycleResourceSnapshot Snapshot(
        long privateBytes,
        long managedHeapBytes,
        int handles,
        int liveOwners,
        int activeDirectories) =>
        new(
            DateTimeOffset.UtcNow,
            privateBytes,
            managedHeapBytes,
            handles,
            liveOwners,
            activeDirectories,
            RetiredBrowserSessionDirectories: 0);

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
