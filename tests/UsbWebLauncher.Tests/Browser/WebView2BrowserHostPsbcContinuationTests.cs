using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed partial class WebView2BrowserHostTests
{
    private static readonly Uri PsbcTarget = new("https://corpebank.psbc.com/#/login");

    [Fact]
    public async Task Psbc412ThenAutomatic200_CompletesOriginalAttemptWithoutAnotherSelection()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());

        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);

        Assert.False(navigation.IsCompleted);
        Assert.Null(reporter.FailureAttempt);
        Assert.Null(reporter.SuccessAttempt);
        var continuation = control.RaiseExternalNavigationStarting(4, PsbcTarget, false);
        await continuation.AuthenticationBindingTask.WaitAsync(TimeSpan.FromSeconds(2));
        // Duplicate callbacks from the first document cannot finish the new one.
        control.CompleteNavigation(1, true, httpStatusCode: 200, target: PsbcTarget);
        Assert.False(navigation.IsCompleted);
        control.CompleteNavigation(4, true, httpStatusCode: 200, target: PsbcTarget);
        await navigation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(handler.ResultAttempt, reporter.SuccessAttempt);
        Assert.Null(reporter.FailureAttempt);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, control.NavigateCount);
        Assert.Single(reporter.NavigationLifecycleEvents);
    }

    [Theory]
    [InlineData("https://corpebank.psbc.com/", 403, null)]
    [InlineData("https://corpebank.psbc.com/", 500, null)]
    [InlineData("https://b2b.ccb.com/", 412, null)]
    [InlineData("http://corpebank.psbc.com/", 412, null)]
    [InlineData("https://corpebank.psbc.com:444/", 412, null)]
    [InlineData("https://corpebank.psbc.com/", 412, "ERR_CONNECTION_CLOSED")]
    public async Task NonQualifyingFailure_DoesNotWaitForContinuation(
        string target, int status, string? networkError)
    {
        var uri = new Uri(target);
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(uri);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, $"Unknown (HTTP {status})", status, uri, networkError);

        await Assert.ThrowsAsync<BrowserNavigationException>(
            () => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(reporter.FailureAttempt);
        Assert.Null(reporter.SuccessAttempt);
    }

    [Fact]
    public async Task Psbc412WithoutSelectedCertificate_DoesNotWaitOrAuthorize()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(), reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);

        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(reporter.PreCertificateNavigationSucceeded);
        Assert.Null(reporter.SuccessAttempt);
    }

    [Theory]
    [InlineData("https://example.com/", false)]
    [InlineData("https://corpebank.psbc.com.evil.example/", false)]
    [InlineData("http://corpebank.psbc.com/", false)]
    [InlineData("https://corpebank.psbc.com:444/", false)]
    [InlineData("https://user@corpebank.psbc.com/", false)]
    [InlineData("https://corpebank.psbc.com/", true)]
    public async Task PsbcContinuation_RejectsForeignOrUserNavigation(string target, bool userInitiated)
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);
        var start = control.RaiseExternalNavigationStarting(4, new Uri(target), userInitiated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => start.AuthenticationBindingTask);
        control.CompleteNavigation(4, true, httpStatusCode: 200, target: new Uri(target));

        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        await control.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(host.IsAvailable);
        Assert.NotNull(reporter.FailureAttempt);
        Assert.Null(reporter.SuccessAttempt);
    }

    [Theory]
    [InlineData("https://example.com/", 200, true)]
    [InlineData("https://corpebank.psbc.com/", 412, false)]
    [InlineData("https://corpebank.psbc.com/", 500, false)]
    [InlineData("https://corpebank.psbc.com/", 0, true)]
    public async Task PsbcContinuation_RequiresSuccessfulSameOriginFinalResponse(
        string finalTarget, int status, bool success)
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) },
            reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);
        await control.RaiseExternalNavigationStarting(4, PsbcTarget, false).AuthenticationBindingTask;
        control.CompleteNavigation(4, success, $"Unknown (HTTP {status})", status, new Uri(finalTarget));

        var error = await Assert.ThrowsAsync<BrowserNavigationException>(
            () => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("HTTP 412", error.Message);
        Assert.NotNull(reporter.FailureAttempt);
        Assert.Null(reporter.SuccessAttempt);
    }

    [Fact]
    public async Task PsbcContinuation_RejectsRepeatedCertificateWithoutCallingHandler()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);
        await control.RaiseExternalNavigationStarting(4, PsbcTarget, false).AuthenticationBindingTask;
        var repeat = await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());

        Assert.Null(repeat.SelectedCertificate);
        Assert.Equal(1, handler.CallCount);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(reporter.SuccessAttempt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PsbcContinuation_TimeoutDisposesBeforeAnyFurtherAttempt(bool startContinuation)
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        await using var host = new WebView2BrowserHost(control, new BrowserTimeoutScheduler(),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(150));
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) },
            reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);
        if (startContinuation)
            await control.RaiseExternalNavigationStarting(4, PsbcTarget, false).AuthenticationBindingTask;

        var error = await Assert.ThrowsAsync<BrowserNavigationException>(
            () => navigation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("HTTP 412", error.Message);
        Assert.False(host.IsAvailable);
        await control.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        control.CompleteNavigation(4, true, httpStatusCode: 200, target: PsbcTarget);
        Assert.Null(reporter.SuccessAttempt);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.NavigateAsync(PsbcTarget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PsbcContinuation_CancellationCannotBecomeReadyFromLateCompletion(bool startContinuation)
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        using var cancellation = new CancellationTokenSource();
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(true) { ResultAttempt = new(1, 1, 1) },
            reporter, TimeSpan.FromSeconds(30));
        var navigation = host.NavigateAsync(PsbcTarget, cancellation.Token);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        control.CompleteNavigation(1, false, "Unknown (HTTP 412)", 412, PsbcTarget);
        if (startContinuation)
            await control.RaiseExternalNavigationStarting(4, PsbcTarget, false).AuthenticationBindingTask;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => navigation.WaitAsync(TimeSpan.FromSeconds(2)));
        await control.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        control.CompleteNavigation(4, true, httpStatusCode: 200, target: PsbcTarget);
        Assert.False(host.IsAvailable);
        Assert.Null(reporter.SuccessAttempt);
    }
}
