using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BusinessSessionScopeTests
{
    [Fact]
    public async Task RevokeAsync_WhenRepeated_SharesPendingCancellationCompletion()
    {
        using var scope = new BusinessSessionScope(CancellationToken.None);
        using var release = new ManualResetEventSlim();
        using var registration = scope.Token.Register(() => release.Wait());
        var first = scope.RevokeAsync();
        try
        {
            var second = scope.RevokeAsync();
            Assert.False(second.IsCompleted);
            Assert.Same(first, second);
        }
        finally
        {
            release.Set();
            await first;
        }
    }

    [Fact]
    public async Task RevokeAsync_InvalidatesImmediatelyWithoutBlockingOnCancellationCallbacks()
    {
        using var scope = new BusinessSessionScope(CancellationToken.None);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = scope.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Wait();
        });
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(() => returned.SetResult(scope.RevokeAsync()));
        try
        {
            var revocation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(scope.IsCurrent);
            Assert.True(scope.Token.IsCancellationRequested);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(revocation.IsCompleted);
            release.Set();
            await revocation.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            await invocation;
        }
    }

    [Fact]
    public async Task RevokingPriorScope_DoesNotCancelOrInvalidateNextSession()
    {
        using var prior = new BusinessSessionScope(CancellationToken.None);
        var priorToken = prior.Token;
        await prior.RevokeAsync();
        using var next = new BusinessSessionScope(CancellationToken.None);

        await prior.RevokeAsync();

        Assert.False(prior.IsCurrent);
        Assert.True(priorToken.IsCancellationRequested);
        Assert.True(next.IsCurrent);
        Assert.False(next.Token.IsCancellationRequested);
        Assert.NotEqual(prior.Id, next.Id);
    }

    [Fact]
    public async Task ApplicationCancellation_InvalidatesItsSessionWithoutAnExplicitStop()
    {
        using var application = new CancellationTokenSource();
        using var scope = new BusinessSessionScope(application.Token);

        await application.CancelAsync();

        Assert.False(scope.IsCurrent);
        Assert.True(scope.Token.IsCancellationRequested);
    }
}
