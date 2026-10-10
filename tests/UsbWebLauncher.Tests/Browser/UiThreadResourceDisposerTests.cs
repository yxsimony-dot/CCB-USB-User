using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class UiThreadResourceDisposerTests
{
    [Fact]
    public async Task SynchronousReentryJoinsPublishedTaskWithoutInvokingCleanupTwice()
    {
        UiThreadResourceDisposer disposer = null!;
        var calls = 0;
        Task? reentered = null;
        disposer = new(() => true, _ => throw new InvalidOperationException(), () =>
        {
            if (++calls == 1) reentered = disposer.DisposeAsync().AsTask();
        });

        var initial = disposer.DisposeAsync().AsTask();
        await initial;

        Assert.Equal(1, calls);
        Assert.Same(initial, reentered);
    }

    [Fact]
    public async Task ConcurrentCallerCanJoinWithoutWaitingForUiCleanupToReturn()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var disposer = new UiThreadResourceDisposer(() => true, _ => throw new InvalidOperationException(), () =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var owner = Task.Run(async () => await disposer.DisposeAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var joinReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joiner = Task.Run(() => joinReturned.SetResult(disposer.DisposeAsync().AsTask()));
        try
        {
            var joined = await joinReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(joined.IsCompleted);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(owner, joiner);
        }
    }

    [Fact]
    public async Task CleanupFailureRemainsSharedForLaterCallers()
    {
        var calls = 0;
        var disposer = new UiThreadResourceDisposer(() => true, _ => throw new InvalidOperationException(), () =>
        {
            calls++;
            throw new IOException("cleanup unavailable");
        });
        var first = disposer.DisposeAsync().AsTask();
        var second = disposer.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }
}
