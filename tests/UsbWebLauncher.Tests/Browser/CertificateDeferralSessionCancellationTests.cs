using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Browser;

public sealed class CertificateDeferralSessionCancellationTests
{
    [Fact]
    public async Task AlreadyRevokedSessionDoesNotReadRequestOrAuthorizeNativePromptButCompletesDeferral()
    {
        var requestReads = 0;
        var authorizations = 0;
        var denied = 0;
        var handled = 0;
        var completed = 0;
        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            () => () => completed++,
            () => { requestReads++; return Request(); },
            (_, _) => { authorizations++; return ValueTask.FromResult(WebClientCertificateSelection.PassThrough(new())); },
            _ => throw new InvalidOperationException("must not select"),
            () => denied++, () => handled++, new CancellationToken(true));

        Assert.Equal(0, requestReads);
        Assert.Equal(0, authorizations);
        Assert.Equal(1, denied);
        Assert.Equal(1, handled);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task RevokeWhileAuthorizationIgnoresCancellationRejectsLateResultAndCompletesOnce()
    {
        using var session = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<WebClientCertificateSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var denied = 0;
        var handled = 0;
        var completed = 0;
        var work = WebClientCertificateRequestDeferralDispatcher.DispatchAsync(Request(),
            async (_, token) =>
            {
                Assert.Equal(session.Token, token);
                entered.SetResult();
                return await result.Task;
            }, _ => throw new InvalidOperationException("must not select"),
            () => denied++, () => handled++, () => completed++, session.Token).AsTask();
        await entered.Task;
        await session.CancelAsync();
        result.SetResult(WebClientCertificateSelection.PassThrough(new ClientCertificateSelectionAuthorization()));
        await work;
        Assert.Equal(1, denied);
        Assert.Equal(1, handled);
        Assert.Equal(1, completed);
    }

    private static WebClientCertificateRequest Request() => new(new Uri("https://b2b.ccb.com/"), false, []);
}
