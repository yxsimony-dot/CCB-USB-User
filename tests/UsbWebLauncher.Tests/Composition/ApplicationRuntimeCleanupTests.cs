using UsbWebLauncher.App;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ApplicationRuntimeCleanupTests
{
    [Fact]
    public async Task RunAsync_WhenOwnerFails_ReleasesLaterOwnersAndClearsSession()
    {
        var cleanup = new ApplicationRuntimeCleanup();
        var session = new BusinessSessionScope(CancellationToken.None);
        BusinessSessionScope? published = session;
        var calls = new List<string>();

        var exception = await Assert.ThrowsAsync<CleanupStageAggregateException>(
            () => cleanup.RunAsync(() => new(
                session,
                [
                    new CleanupStage("failing-owner", () =>
                    {
                        calls.Add("failing-owner");
                        throw new IOException("owner failed");
                    }),
                    new CleanupStage("later-owner", () =>
                    {
                        calls.Add("later-owner");
                        return ValueTask.CompletedTask;
                    })
                ],
                () =>
                {
                    if (ReferenceEquals(published, session))
                    {
                        published = null;
                    }
                })));

        Assert.Equal(["failing-owner", "later-owner"], calls);
        Assert.Equal("failing-owner", Assert.Single(exception.Failures).Stage);
        Assert.Null(published);
        Assert.Throws<ObjectDisposedException>(() => _ = session.Token);
    }

    [Fact]
    public async Task RunAsync_WhenCalledRepeatedly_CapturesOwnershipOnceAndSharesTask()
    {
        var cleanup = new ApplicationRuntimeCleanup();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var clears = 0;

        ApplicationRuntimeCleanupSnapshot Capture()
        {
            captures++;
            return new(
                null,
                [new CleanupStage("owner", () => new ValueTask(release.Task))],
                () => clears++);
        }

        var first = cleanup.RunAsync(Capture);
        var second = cleanup.RunAsync(Capture);
        Assert.Same(first, second);
        Assert.Equal(1, captures);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, clears);
    }
}
