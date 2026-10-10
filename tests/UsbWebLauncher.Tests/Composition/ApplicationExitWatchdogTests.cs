using System.Diagnostics;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ApplicationExitWatchdogTests
{
    [Theory]
    [InlineData("blocked-main-thread")]
    [InlineData("blocked-cancel")]
    [InlineData("blocked-thread-pool")]
    [InlineData("never-completing-cleanup")]
    [InlineData("repeat")]
    [InlineData("stop-blocked-main-thread")]
    [InlineData("stop-close-independent")]
    public async Task ArmedWatchdog_ExitsOnlyItsOwnProcessWithinOriginalDeadline(string mode)
    {
        using var process = StartHarness(mode);
        try
        {
            Assert.Equal("armed", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, process.ExitCode);
            // The test runner remains alive. No process-name based termination is used.
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    [Theory]
    [InlineData("disarm")]
    [InlineData("stop-responsive-timeout")]
    public async Task DisarmedWatchdog_DoesNotTerminateANormallyFinishingProcess(string mode)
    {
        using var process = StartHarness(mode);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(23, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    private static Process StartHarness(string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "shutdown-harness", "UsbWebLauncher.ShutdownHarness.dll"));
        start.ArgumentList.Add(mode);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start shutdown test subprocess.");
    }
}
