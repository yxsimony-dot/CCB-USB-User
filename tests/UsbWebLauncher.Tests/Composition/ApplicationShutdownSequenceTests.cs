namespace UsbWebLauncher.Tests.Composition;

public sealed class ApplicationShutdownSequenceTests
{
    [Fact]
    public async Task RunAsync_WhenCleanupNeverCompletes_StillClosesWithinExitBudget()
    {
        var calls = new List<string>();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = CreateSequence(() => never.Task,
            () => calls.Add("close"), code => calls.Add($"shutdown:{code}"));

        await RunAsync(sequence).WaitAsync(TimeSpan.FromSeconds(9));

        Assert.Equal(["close", "shutdown:0"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenCalledAgain_SharesTheOriginalCleanupAndClose()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var sequence = CreateSequence(() => { calls.Add("cleanup"); return cleanup.Task; },
            () => calls.Add("close"), code => calls.Add($"shutdown:{code}"));

        var first = sequence.RunAsync();
        var second = sequence.RunAsync();
        cleanup.SetResult();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(["cleanup", "close", "shutdown:0"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenCleanupReentersClose_DoesNotRecursivelyCleanUp()
    {
        var cleanupCalls = 0;
        Task? reentrant = null;
        UsbWebLauncher.App.ApplicationShutdownSequence? sequence = null;
        sequence = CreateSequence(() =>
        {
            cleanupCalls++;
            if (cleanupCalls == 1) reentrant = sequence!.RunAsync();
            return Task.CompletedTask;
        }, () => { }, _ => { });

        var completion = sequence.RunAsync();
        await completion;

        Assert.Same(completion, reentrant);
        Assert.Equal(1, cleanupCalls);
        Assert.Same(completion, sequence.RunAsync());
    }

    [Fact]
    public async Task RunAsync_WhenExitDeadlineHasElapsed_DoesNotStartAnotherCleanupBudget()
    {
        var calls = new List<string>();
        var sequence = new UsbWebLauncher.App.ApplicationShutdownSequence(
            () => { calls.Add("cleanup"); return new TaskCompletionSource().Task; },
            () => calls.Add("close"), code => calls.Add($"shutdown:{code}"), TimeSpan.Zero);

        await sequence.RunAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(["close", "shutdown:0"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenCleanupThrows_StillClosesAndRequestsSuccessfulShutdown()
    {
        var calls = new List<string>();
        var sequence = CreateSequence(
            () =>
            {
                calls.Add("cleanup");
                return Task.FromException(new InvalidOperationException("test cleanup failure"));
            },
            () => calls.Add("close"),
            exitCode => calls.Add($"shutdown:{exitCode}"));

        await RunAsync(sequence);

        Assert.Equal(["cleanup", "close", "shutdown:0"], calls);
    }

    [Fact]
    public async Task RunAsync_WhenWindowCloseThrows_StillRequestsSuccessfulShutdown()
    {
        var calls = new List<string>();
        var sequence = CreateSequence(
            () =>
            {
                calls.Add("cleanup");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("close");
                throw new InvalidOperationException("test close failure");
            },
            exitCode => calls.Add($"shutdown:{exitCode}"));

        await RunAsync(sequence);

        Assert.Equal(["cleanup", "close", "shutdown:0"], calls);
    }

    private static UsbWebLauncher.App.ApplicationShutdownSequence CreateSequence(
        Func<Task> cleanup,
        Action closeWindow,
        Action<int> shutdownApplication) =>
        new(cleanup, closeWindow, shutdownApplication);

    private static Task RunAsync(UsbWebLauncher.App.ApplicationShutdownSequence sequence) =>
        sequence.RunAsync();
}
