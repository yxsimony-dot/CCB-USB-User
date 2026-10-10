// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using System.Collections.Concurrent;

namespace UsbWebLauncher.Tests.Browser;

public sealed class WebView2BrowserHostTests
{
    [Fact]
    public async Task SuccessfulLandingPageWithoutCertificate_AllowsWebTriggeredCertificateNavigation()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "landing-page-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            new TestPinStore(),
            new TestStampStore(authentication),
            new TestUnlocker(session));
        var control = new RecordingWebViewControl();
        control.NavigationIds.Enqueue(701);
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            coordinator, coordinator, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var ready = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.State.Stage == AuthenticationStage.Ready)
            {
                ready.TrySetResult();
            }
        };

        await coordinator.HandleDevicePresenceAsync(true);
        var landingNavigation = host.NavigateAsync(new Uri("https://b2bbank.ccb.com/"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(701, success: true);
        await landingNavigation;

        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(0, session.DisposeCount);

        var externalNavigation = control.RaiseExternalNavigationStarting(
            702,
            new Uri("https://b2b3.ccb.com/"));
        await externalNavigation.AuthenticationBindingTask.WaitAsync(TimeSpan.FromSeconds(1));
        var selection = await control.RaiseClientCertificateRequestAsync(
            CreateMatchingWebRequest());

        Assert.NotNull(selection.SelectedCertificate);

        control.CompleteNavigation(702, success: true);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task StaleCertificateRequestFromOldOperation_CannotAuthenticateNewInsertionCycle()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "operation-cycle-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            new TestPinStore(),
            new TestStampStore(authentication),
            new SequenceTestUnlocker(firstSession, secondSession));
        var control = new RecordingWebViewControl();
        control.NavigationIds.Enqueue(501);
        control.NavigationIds.Enqueue(502);
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            coordinator, coordinator, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        await coordinator.HandleDevicePresenceAsync(true);
        var firstNavigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/first"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.HandleDevicePresenceAsync(false);
        await coordinator.HandleDevicePresenceAsync(true);

        var staleSelection = await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());

        Assert.Null(staleSelection.SelectedCertificate);
        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(0, secondSession.DisposeCount);

        control.CompleteNavigation(501, success: false);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => firstNavigation);
        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(0, secondSession.DisposeCount);

        control.ResetNavigationInvoked();
        var secondNavigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var currentSelection = await control.RaiseClientCertificateRequestAsync(CreateMatchingWebRequest());
        Assert.NotNull(currentSelection.SelectedCertificate);

        control.CompleteNavigation(502, success: true);
        await secondNavigation;

        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(1, secondSession.DisposeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(99L)]
    public async Task CertificateRequestWithoutCurrentOperationIdentity_IsDeniedWithoutEndingCycle(
        long? browserOperationId)
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "invalid-operation-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(42);

        var result = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://b2b.ccb.com/"), false,
            [new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")],
            browserOperationId));

        Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest, result.Status);
        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task NavigateAsync_AwaitsAuthenticationOperationBindingBeforeStartingNativeNavigation()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter { BlockNavigationStarted = true };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(), reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await reporter.NavigationStartReportInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, control.NavigateCount);
        reporter.AllowNavigationStartReport();
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task BrowserOperationIdentity_IsUniqueAcrossHostInstances()
    {
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler();
        var firstControl = new RecordingWebViewControl();
        var secondControl = new RecordingWebViewControl();
        await using var firstHost = new WebView2BrowserHost(firstControl);
        await using var secondHost = new WebView2BrowserHost(secondControl);
        await firstHost.ConfigureClientCertificateAuthenticationAsync(
            handler, reporter, TimeSpan.FromSeconds(30));
        await secondHost.ConfigureClientCertificateAuthenticationAsync(
            handler, reporter, TimeSpan.FromSeconds(30));

        var firstNavigation = firstHost.NavigateAsync(new Uri("https://b2b.ccb.com/first"));
        await firstControl.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstControl.CompleteNavigation(1, success: true);
        await firstNavigation;

        var secondNavigation = secondHost.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await secondControl.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        secondControl.CompleteNavigation(1, success: true);
        await secondNavigation;

        var operationIds = reporter.NavigationLifecycleEvents
            .Where(item => item.StartsWith("started:", StringComparison.Ordinal))
            .Select(item => long.Parse(item["started:".Length..]))
            .ToArray();
        Assert.Equal(2, operationIds.Length);
        Assert.NotEqual(operationIds[0], operationIds[1]);
    }

    [Fact]
    public async Task NavigateAsync_WhenOperationBindingReportFails_DoesNotStartNativeAndReleasesGate()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter
        {
            NavigationStartedException = new InvalidOperationException("binding failed")
        };
        await using var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(), reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.NavigateAsync(new Uri("https://b2b.ccb.com/first")));
        Assert.Equal(0, control.NavigateCount);

        reporter.NavigationStartedException = null;
        var secondNavigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(1, success: true);
        await secondNavigation;
    }

    [Fact]
    public async Task OldNavigationTerminal_DoesNotReleaseNewInsertionCycleLease()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "navigation-cycle-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            new TestPinStore(),
            new TestStampStore(authentication),
            new SequenceTestUnlocker(firstSession, secondSession));

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(101);
        await coordinator.HandleDevicePresenceAsync(false);
        await coordinator.HandleDevicePresenceAsync(true);

        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(101, isSuccess: false);

        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(0, secondSession.DisposeCount);

        await coordinator.ReportNavigationStartedAsync(202);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(202, isSuccess: false);

        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(1, secondSession.DisposeCount);
    }

    [Fact]
    public void WebView2ControlLifetime_DisposalBetweenCallAndDispatchedExecutionFailsFastAndIsIdempotent()
    {
        var lifetime = new WebView2ControlLifetime();
        lifetime.ThrowIfDisposed();

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lifetime.ThrowIfDisposed());
    }

    [Fact]
    public async Task InitializeAsync_CompletesAfterControlInitialization()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);

        await host.InitializeAsync();

        Assert.Equal(1, control.InitializeCount);
    }

    [Fact]
    public async Task NavigateAsync_WaitsForSuccessfulNavigationCompletion()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        var target = new Uri("https://b2b.ccb.com/");

        var navigation = host.NavigateAsync(target);
        Assert.False(navigation.IsCompleted);
        Assert.Equal(target, control.LastTarget);

        control.CompleteNavigation(success: true);

        await navigation;
    }

    [Fact]
    public async Task NavigateAsync_ThrowsDiagnosticExceptionWhenNavigationFails()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);

        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        control.CompleteNavigation(success: false, "ConnectionAborted");

        var exception = await Assert.ThrowsAsync<BrowserNavigationException>(
            () => navigation);
        Assert.Contains("ConnectionAborted", exception.Message);
    }

    [Fact]
    public async Task NavigateAsync_StopsWaitingWhenCancelled()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        using var cancellation = new CancellationTokenSource();

        var navigation = host.NavigateAsync(
            new Uri("https://b2b.ccb.com/"),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
    }

    [Fact]
    public async Task NavigateAsync_WaitsForNavigationStartingRaisedAfterNativeNavigateReturns()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var host = new WebView2BrowserHost(control);

        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(navigation.IsCompleted);
        control.RaiseNavigationStarting(41);
        control.CompleteNavigation(41, success: true);
        await navigation;
    }

    [Fact]
    public async Task NavigateAsync_CachesCompletionThatArrivesBeforeDelayedNavigationIdentityBinding()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var host = new WebView2BrowserHost(control);

        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(41, success: true);
        Assert.False(navigation.IsCompleted);

        control.RaiseNavigationStarting(41);

        await navigation;
    }

    [Fact]
    public async Task EarlyCompletion_ReportsNavigationStartedBeforePreCertificateTerminal()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(), reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        control.CompleteNavigation(41, success: false);
        control.RaiseNavigationStarting(41);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);

        Assert.Equal(2, reporter.NavigationLifecycleEvents.Count);
        Assert.StartsWith("started:", reporter.NavigationLifecycleEvents[0], StringComparison.Ordinal);
        Assert.Equal(
            reporter.NavigationLifecycleEvents[0]["started:".Length..],
            reporter.NavigationLifecycleEvents[1]["terminal:".Length..]);
    }

    [Fact]
    public async Task DisposeAfterHandlerSelectedBeforePendingRegistration_FailsAttemptAndReleasesLeaseOnce()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "dispose-handler-race-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        var handler = new BlockingAfterSelectedHandler(coordinator);
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            handler, coordinator, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        var certificateRequest = control.RaiseClientCertificateRequestAsync(
            new WebClientCertificateRequest(
                new Uri("https://b2b.ccb.com/"), false,
                [new WebClientCertificate(
                    "1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")])).AsTask();
        await handler.Selected.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        handler.AllowReturn();
        var selection = await certificateRequest.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(selection.SelectedCertificate);
        Assert.NotEqual(AuthenticationStage.Authenticating, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => navigation);
    }

    [Fact]
    public async Task StartNavigationAsync_WhenControlThrowsSynchronously_ReleasesSessionForNextNavigation()
    {
        var control = new RecordingWebViewControl
        {
            NavigationStartException = new InvalidOperationException("native start failed")
        };
        var host = new WebView2BrowserHost(control);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.NavigateAsync(new Uri("https://b2b.ccb.com/first")));

        control.NavigationStartException = null;
        control.ResetNavigationInvoked();
        var second = host.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(1, success: true);
        await second;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartNavigationAsync_WhenReturnedTaskTerminates_ReleasesSessionForNextNavigation(
        bool cancelStart)
    {
        var control = new RecordingWebViewControl
        {
            CancelNavigationStartTask = cancelStart,
            NavigationStartTaskException = cancelStart
                ? null
                : new InvalidOperationException("asynchronous native start failed")
        };
        var host = new WebView2BrowserHost(control);

        if (cancelStart)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.NavigateAsync(new Uri("https://b2b.ccb.com/first")));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.NavigateAsync(new Uri("https://b2b.ccb.com/first")));
        }

        control.CancelNavigationStartTask = false;
        control.NavigationStartTaskException = null;
        control.ResetNavigationInvoked();
        var second = host.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(1, success: true);
        await second;
    }

    [Fact]
    public async Task DisposeBeforeNavigationStarting_FaultsPendingNavigationAndRejectsFutureNavigation()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var host = new WebView2BrowserHost(control);
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => navigation);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.NavigateAsync(new Uri("https://b2b.ccb.com/again")));
    }

    [Fact]
    public async Task CallerCancellationBeforeNavigationStarting_DisposesUnknownNativeSessionWithoutOrphanedBinding()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var host = new WebView2BrowserHost(control);
        using var cancellation = new CancellationTokenSource();
        var navigation = host.NavigateAsync(
            new Uri("https://b2b.ccb.com/"), cancellation.Token);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await control.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.NavigateAsync(new Uri("https://b2b.ccb.com/again")));
    }

    [Fact]
    public async Task CallerCancellation_WhenStopHasNoTerminalCallback_DisposesBrowserFailClosed()
    {
        var control = new RecordingWebViewControl
        {
            DeferNavigationStarting = true,
            CompleteNavigationOnStop = false
        };
        var host = new WebView2BrowserHost(
            control,
            new ManualTimeoutScheduler(),
            TimeSpan.FromMilliseconds(10));
        await host.InitializeAsync();

        using var cancellation = new CancellationTokenSource();
        var navigation = host.NavigateAsync(
            new Uri("https://b2b.ccb.com/"), cancellation.Token);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.RaiseNavigationStarting(101);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await control.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, control.StopCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.NavigateAsync(new Uri("https://b2b.ccb.com/again")));
    }

    [Fact]
    public async Task NavigationSession_DelaysNativeTerminalUntilInflightCertificateDeferralCompletes()
    {
        var sessions = new WebView2NavigationSessionTracker();
        sessions.BeginNavigation();
        Assert.True(sessions.TryBindNavigationId(101));
        Assert.True(sessions.TryEnterCertificateRequest(
            out var certificateLease,
            out _));

        var terminal = sessions.CompleteNavigationAsync(101);

        Assert.False(terminal.IsCompleted);
        Assert.False(sessions.TryEnterCertificateRequest(out _, out _));
        Assert.Throws<InvalidOperationException>(() => sessions.BeginNavigation());
        certificateLease!.Dispose();
        Assert.True(await terminal);

        sessions.BeginNavigation();
        Assert.True(sessions.TryBindNavigationId(202));
    }

    [Fact]
    public async Task CancelledPhysicalNavigation_StopsOldNavigationBeforeStartingTheNextOne()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true)
        {
            ResultAttempt = new AuthenticationAttempt(6, 7)
        };
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        using var cancellation = new CancellationTokenSource();
        var first = host.NavigateAsync(new Uri("https://b2b.ccb.com/a"), cancellation.Token);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.RaiseNavigationStarting(101);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, control.StopCount);

        control.ResetNavigationInvoked();
        var second = host.NavigateAsync(new Uri("https://b2b.ccb.com/b"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, control.NavigateCount);
        control.RaiseNavigationStarting(202);
        control.CompleteNavigation(202, success: true);
        await second;
        Assert.Null(reporter.SuccessAttempt);
        Assert.Null(reporter.FailureAttempt);
    }

    [Fact]
    public void ClientCertificateRequest_DoesNotExposeSyntheticNavigationIdentity()
    {
        Assert.Null(typeof(WebClientCertificateRequest).GetProperty("NavigationId"));
    }

    [Fact]
    public async Task LateCompletionFromCancelledNavigation_DoesNotCompleteOrTerminateNewNavigation()
    {
        var control = new RecordingWebViewControl { DeferNavigationStarting = true };
        control.NavigationIds.Enqueue(101);
        control.NavigationIds.Enqueue(202);
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true)
        {
            ResultAttempt = new AuthenticationAttempt(2, 9)
        };
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        using var firstCancellation = new CancellationTokenSource();
        var first = host.NavigateAsync(new Uri("https://b2b.ccb.com/first"), firstCancellation.Token);
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.RaiseNavigationStarting(101);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var preCertificateCountAfterCancellation = reporter.PreCertificateTerminals.Count;

        control.ResetNavigationInvoked();
        var second = host.NavigateAsync(new Uri("https://b2b.ccb.com/second"));
        await control.NavigationInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, control.NavigateCount);

        control.CompleteNavigation(101, success: true);
        await Task.Yield();
        Assert.False(second.IsCompleted);
        control.RaiseNavigationStarting(202);

        await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"),
            false,
            [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));

        control.CompleteNavigation(101, success: true);
        await Task.Yield();
        Assert.False(second.IsCompleted);
        Assert.Null(reporter.SuccessAttempt);
        Assert.Null(reporter.FailureAttempt);
        Assert.Equal(preCertificateCountAfterCancellation, reporter.PreCertificateTerminals.Count);

        control.CompleteNavigation(202, success: true);
        await second;
        await reporter.SuccessReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new AuthenticationAttempt(2, 9), reporter.SuccessAttempt);
    }

    [Fact]
    public async Task ConfigureAndInitializeConcurrent_RegisterOneHandlerAndNavigationWaitsForRegistration()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.InitializeAsync();
        control.BlockRegistration = true;

        var configure = host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(),
            new RecordingAuthenticationReporter(),
            TimeSpan.FromSeconds(30));
        await control.RegistrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var initialize = host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        await Task.Yield();
        Assert.Equal(1, control.CertificateHandlerRegistrationCount);
        Assert.DoesNotContain("navigate", control.Operations);

        control.AllowRegistration();
        await Task.WhenAll(configure, initialize);
        await control.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, control.CertificateHandlerRegistrationCount);
        Assert.Contains("navigate", control.Operations);
        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task ConfigureAuthentication_AfterFirstNavigationStarted_IsRejected()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ConfigureClientCertificateAuthenticationAsync(
                new RecordingCertificateHandler(),
                new RecordingAuthenticationReporter(),
                TimeSpan.FromSeconds(30)));

        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task ConfigureAuthentication_RegistersCertificateHandlerAfterInitializationAndBeforeNavigation()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(),
            new RecordingAuthenticationReporter(),
            TimeSpan.FromSeconds(30));
        await host.InitializeAsync();

        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        Assert.Equal(["initialize", "register", "navigate"], control.Operations);
        control.CompleteNavigation(success: true);
        await navigation;
    }

    [Fact]
    public async Task ConfigureAuthentication_RegistersCertificateHandlerOnlyOnceAcrossRepeatedInitialization()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(),
            new RecordingAuthenticationReporter(),
            TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        await host.InitializeAsync();

        Assert.Equal(1, control.CertificateHandlerRegistrationCount);
    }

    [Fact]
    public async Task ClientCertificateRequest_WithUniqueHandlerSelection_SelectsOriginalCertificateAndReportsAttempt()
    {
        var control = new RecordingWebViewControl();
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true);
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);
        var attempt = new AuthenticationAttempt(7, 11);
        handler.ResultAttempt = attempt;

        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        var original = new WebClientCertificate("AA11", "CN=Target", "CN=Issuer");

        var selection = await control.RaiseClientCertificateRequestAsync(
            new WebClientCertificateRequest(
                new Uri("https://b2b.ccb.com/"),
                false,
                [original]));

        Assert.True(selection.Handled);
        Assert.Same(original, selection.SelectedCertificate);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(attempt, reporter.CertificateSelectedAttempt);
        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task ClientCertificateRequest_WhenAutomaticMatchIsUnavailable_PassesThroughToBrowserCertificatePicker()
    {
        var control = new RecordingWebViewControl();
        var attempt = new AuthenticationAttempt(9, 14);
        var handler = new ManualSelectionHandler(
            ClientCertificateRequestMatchStatus.CertificateMissing,
            attempt);
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(
            handler,
            reporter,
            TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        var selection = await control.RaiseClientCertificateRequestAsync(
            new WebClientCertificateRequest(
                new Uri("https://b2b.ccb.com/"),
                false,
                [new WebClientCertificate("AA11", "CN=Other", "CN=Issuer")]));

        Assert.False(selection.Handled);
        Assert.Null(selection.SelectedCertificate);
        Assert.Equal(attempt, reporter.CertificateSelectedAttempt);

        control.CompleteNavigation(1, success: true);
        await navigation;

        Assert.Equal(attempt, reporter.SuccessAttempt);
    }

    [Fact]
    public async Task ClientCertificateRequest_WhenHandlerThrows_AlwaysDeniesWithoutFallback()
    {
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(
            new ThrowingCertificateHandler(),
            new RecordingAuthenticationReporter(),
            TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        var original = new WebClientCertificate("AA11", "CN=Target", "CN=Issuer");

        var selection = await control.RaiseClientCertificateRequestAsync(
            new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, [original]));

        Assert.True(selection.Handled);
        Assert.Null(selection.SelectedCertificate);
        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task CertificateSelectedReporterFailure_DeniesAndReportsTlsFailureForSameAttempt()
    {
        var control = new RecordingWebViewControl();
        var attempt = new AuthenticationAttempt(8, 13);
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true) { ResultAttempt = attempt };
        var reporter = new RecordingAuthenticationReporter { CertificateSelectedException = new InvalidOperationException("failure") };
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        var selection = await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"),
            false,
            [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));

        Assert.True(selection.Handled);
        Assert.Null(selection.SelectedCertificate);
        await reporter.FailureReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(attempt, reporter.FailureAttempt);
        control.CompleteNavigation(1, success: false);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);
    }

    [Fact]
    public async Task CertificateSelectedReporterCancellation_ReleasesCoordinatorLeaseAndLeavesAuthenticating()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "browser-test",
            "b2b.ccb.com",
            443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"),
            "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            new TestPinStore(),
            new TestStampStore(authentication),
            new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        var reporter = new CancelCertificateSelectedReporter(coordinator);
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(coordinator, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        var selection = await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"),
            false,
            [new WebClientCertificate(
                "1111111111111111111111111111111111111111",
                "CN=Target",
                "CN=Issuer")]));

        Assert.Null(selection.SelectedCertificate);
        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
        control.CompleteNavigation(1, success: false);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);
    }

    [Fact]
    public async Task CoordinatorAcknowledgmentAfterUsbRemoval_IsRejectedAndDoesNotReleaseTwice()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "stale-ack-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(1);
        var match = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://b2b.ccb.com/"), false,
            [new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")],
            1));
        await coordinator.HandleDevicePresenceAsync(false);

        var authorization = await coordinator.ReportCertificateSelectedAsync(match.Attempt!);

        Assert.Null(authorization);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task AuthorizedSelection_WhenUsbRemovedBeforeUiApply_IsRevokedAndDispatcherDenies()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "authorization-revoke-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            coordinator, coordinator, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        var selection = await control.RaiseClientCertificateRequestAsync(
            new WebClientCertificateRequest(
                new Uri("https://b2b.ccb.com/"), false,
                [new WebClientCertificate(
                    "1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")]));
        Assert.NotNull(selection.Authorization);
        await coordinator.HandleDevicePresenceAsync(false);
        var nativeApplied = false;
        var denied = false;

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            new WebClientCertificateRequest(
                new Uri("https://b2b.ccb.com/"), false,
                [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]),
            (_, _) => ValueTask.FromResult(selection),
            _ => nativeApplied = true,
            () => denied = true,
            () => { },
            () => { },
            CancellationToken.None);

        Assert.False(nativeApplied);
        Assert.True(denied);
        Assert.Equal(1, session.DisposeCount);
        control.CompleteNavigation(1, success: false);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);
    }

    [Fact]
    public async Task AuthorizedSelection_CommitInProgress_BlocksUsbSessionReleaseUntilApplyReturns()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "authorization-linearization-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(1);
        var match = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://b2b.ccb.com/"), false,
            [new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")],
            1));
        var authorization = await coordinator.ReportCertificateSelectedAsync(match.Attempt!);
        Assert.NotNull(authorization);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowApplyToReturn = new ManualResetEventSlim();
        var applied = false;
        var commit = Task.Run(() => authorization.TryCommit(() =>
        {
            applyEntered.TrySetResult();
            allowApplyToReturn.Wait();
            applied = true;
        }));
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var removalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removal = Task.Run(async () =>
        {
            removalStarted.TrySetResult();
            await coordinator.HandleDevicePresenceAsync(false);
        });
        await removalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(removal.IsCompleted);
        Assert.Equal(0, session.DisposeCount);
        allowApplyToReturn.Set();

        Assert.True(await commit);
        await removal;
        Assert.True(applied);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task AuthorizedSelection_AfterTimeoutCannotCommitNativeSelection()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "authorization-timeout-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(1);
        var match = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://b2b.ccb.com/"), false,
            [new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")],
            1));
        var authorization = await coordinator.ReportCertificateSelectedAsync(match.Attempt!);
        Assert.NotNull(authorization);

        await coordinator.ReportTimeoutAsync(match.Attempt!);
        var applied = false;

        Assert.False(authorization.TryCommit(() => applied = true));
        Assert.False(applied);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task AuthorizedSelection_CommitInProgress_LinearizesBeforeConcurrentTimeout()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "authorization-timeout-race-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(1);
        var match = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://b2b.ccb.com/"), false,
            [new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")],
            1));
        var authorization = await coordinator.ReportCertificateSelectedAsync(match.Attempt!);
        Assert.NotNull(authorization);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowApplyToReturn = new ManualResetEventSlim();
        var commit = Task.Run(() => authorization.TryCommit(() =>
        {
            applyEntered.TrySetResult();
            allowApplyToReturn.Wait();
        }));
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var timeoutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = Task.Run(async () =>
        {
            timeoutStarted.TrySetResult();
            await coordinator.ReportTimeoutAsync(match.Attempt!);
        });
        await timeoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(timeout.IsCompleted);
        Assert.Equal(0, session.DisposeCount);
        allowApplyToReturn.Set();

        Assert.True(await commit);
        await timeout;
        Assert.Equal(1, session.DisposeCount);
        Assert.False(authorization.TryCommit(() => { }));
    }

    [Fact]
    public async Task Host_WhenUsbIsRemovedBetweenMatchAndAcknowledgment_DeniesStaleCertificateWithoutSecondTerminal()
    {
        var authentication = new ClientCertificateAuthenticationOptions(
            "host-stale-ack-test", "b2b.ccb.com", 443,
            new("1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer"),
            new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"), "test-target");
        var session = new RecordingUnlockSession();
        await using var coordinator = new AuthenticationCoordinator(
            authentication, new TestPinStore(), new TestStampStore(authentication), new TestUnlocker(session));
        await coordinator.HandleDevicePresenceAsync(true);
        var reporter = new RemoveDeviceBeforeAcknowledgmentReporter(coordinator);
        var control = new RecordingWebViewControl();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(coordinator, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        var selection = await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false,
            [new WebClientCertificate(
                "1111111111111111111111111111111111111111", "CN=Target", "CN=Issuer")]));

        Assert.Null(selection.SelectedCertificate);
        Assert.Equal(AuthenticationStage.WaitingForDevice, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, reporter.AttemptTerminalCount);

        control.CompleteNavigation(1, success: false);
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, reporter.AttemptTerminalCount);
    }

    [Fact]
    public async Task DuplicateValueCandidates_MapTheExactCandidateReturnedByHandler()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);
        await host.ConfigureClientCertificateAuthenticationAsync(
            new SelectCandidateHandler(1, new AuthenticationAttempt(4, 6)), reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        var first = new WebClientCertificate("AA11", "CN=Target", "CN=Issuer");
        var second = new WebClientCertificate("AA11", "CN=Target", "CN=Issuer");

        var result = await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false, [second, first]));

        Assert.Same(first, result.SelectedCertificate);
        control.CompleteNavigation(1, success: true);
        await navigation;
    }

    [Fact]
    public async Task NavigationCompletedAfterCertificateSelection_ReportsSuccessAndCancelsTimeout()
    {
        var control = new RecordingWebViewControl();
        var scheduler = new ManualTimeoutScheduler();
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true)
        {
            ResultAttempt = new AuthenticationAttempt(1, 2)
        };
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control, scheduler);

        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false, [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));

        control.CompleteNavigation(success: true);
        await navigation;
        await reporter.SuccessReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scheduler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new AuthenticationAttempt(1, 2), reporter.CertificateSelectedAttempt);
        Assert.Equal(new AuthenticationAttempt(1, 2), reporter.SuccessAttempt);
        Assert.Null(reporter.TimeoutAttempt);
    }

    [Fact]
    public async Task NavigationTerminalBeforeReporterFailure_OwnsAttemptExactlyOnce()
    {
        var control = new RecordingWebViewControl();
        var scheduler = new ManualTimeoutScheduler();
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true)
        {
            ResultAttempt = new AuthenticationAttempt(12, 21)
        };
        var reporter = new BlockingAcknowledgmentReporter();
        var host = new WebView2BrowserHost(control, scheduler);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        var certificateRequest = control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false,
            [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")])).AsTask();
        await reporter.AcknowledgmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scheduler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        control.CompleteNavigation(1, success: true);
        await navigation;
        await reporter.SuccessReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        reporter.FailAcknowledgment(new InvalidOperationException("late reporter failure"));
        var selection = await certificateRequest;

        Assert.Null(selection.SelectedCertificate);
        Assert.Equal(1, reporter.SuccessCount);
        Assert.Equal(0, reporter.FailureCount);
        Assert.Equal(0, reporter.TimeoutCount);
    }

    [Fact]
    public async Task NavigationTerminal_CancelsTimeoutBeforeImmediateTokenSourceDisposal()
    {
        var control = new RecordingWebViewControl();
        var scheduler = new ManualTimeoutScheduler();
        var reporter = new RecordingAuthenticationReporter();
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true)
        {
            ResultAttempt = new AuthenticationAttempt(13, 22)
        };
        var host = new WebView2BrowserHost(control, scheduler);
        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false,
            [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));
        await scheduler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        control.CompleteNavigation(1, success: true);
        await navigation;
        await reporter.SuccessReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scheduler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(reporter.TimeoutAttempt);
    }

    [Fact]
    public async Task FailedNavigationAfterCertificateSelection_ReportsOnlyTheMatchingAttempt()
    {
        var control = new RecordingWebViewControl();
        var attempt = new AuthenticationAttempt(5, 8);
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true) { ResultAttempt = attempt };
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false, [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));

        control.CompleteNavigation(success: false, "ConnectionAborted");
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);
        await reporter.FailureReported.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(attempt, reporter.FailureAttempt);
        Assert.Null(reporter.SuccessAttempt);
        Assert.Null(reporter.TimeoutAttempt);
    }

    [Fact]
    public async Task TimeoutAfterCertificateSelection_ReportsTheAttemptOnce()
    {
        var control = new RecordingWebViewControl();
        var scheduler = new ManualTimeoutScheduler();
        var attempt = new AuthenticationAttempt(3, 4);
        var handler = new RecordingCertificateHandler(selectFirstCertificate: true) { ResultAttempt = attempt };
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control, scheduler);

        await host.ConfigureClientCertificateAuthenticationAsync(handler, reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));
        await control.RaiseClientCertificateRequestAsync(new WebClientCertificateRequest(
            new Uri("https://b2b.ccb.com/"), false, [new WebClientCertificate("AA11", "CN=Target", "CN=Issuer")]));

        await scheduler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scheduler.Elapse();
        await reporter.TimeoutReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        control.CompleteNavigation(success: true);
        await navigation;

        Assert.Equal(attempt, reporter.TimeoutAttempt);
        Assert.Null(reporter.SuccessAttempt);
    }

    [Fact]
    public async Task NavigationFailureBeforeCertificateSelection_ReportsPreCertificateTerminalState()
    {
        var control = new RecordingWebViewControl();
        var reporter = new RecordingAuthenticationReporter();
        var host = new WebView2BrowserHost(control);

        await host.ConfigureClientCertificateAuthenticationAsync(
            new RecordingCertificateHandler(), reporter, TimeSpan.FromSeconds(30));
        await host.InitializeAsync();
        var navigation = host.NavigateAsync(new Uri("https://b2b.ccb.com/"));

        control.CompleteNavigation(success: false, "ConnectionAborted");
        await reporter.PreCertificateTerminalReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<BrowserNavigationException>(() => navigation);

        var terminal = Assert.Single(reporter.PreCertificateTerminals);
        Assert.False(terminal.IsSuccess);
        Assert.True(terminal.NavigationId > 0);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenHandlerDoesNotHandle_DeniesMarksHandledAndCompletes()
    {
        var probe = new DeferralProbe();
        var request = new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, []);

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            request,
            (_, _) => ValueTask.FromResult(new WebClientCertificateSelection(false, new WebClientCertificate("AA11", "CN=Target", "CN=Issuer"))),
            selection => probe.Selected = selection.SelectedCertificate,
            () => probe.Denied = true,
            () => probe.Handled = true,
            () => probe.Completed = true,
            CancellationToken.None);

        Assert.True(probe.Denied);
        Assert.True(probe.Handled);
        Assert.True(probe.Completed);
        Assert.Null(probe.Selected);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenHandlerPassesThrough_LeavesRequestUnhandledAndCompletes()
    {
        var probe = new DeferralProbe();
        var request = new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, []);

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            request,
            (_, _) => ValueTask.FromResult(
                WebClientCertificateSelection.PassThrough(
                    new ClientCertificateSelectionAuthorization())),
            selection => probe.Selected = selection.SelectedCertificate,
            () => probe.Denied = true,
            () => probe.Handled = true,
            () => probe.Completed = true,
            CancellationToken.None);

        Assert.False(probe.Denied);
        Assert.False(probe.Handled);
        Assert.True(probe.Completed);
        Assert.Null(probe.Selected);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenHandlerThrows_StillMarksHandledAndCompletes()
    {
        var probe = new DeferralProbe();
        var request = new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, []);

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            request,
            (_, _) => ValueTask.FromException<WebClientCertificateSelection>(new InvalidOperationException("failure")),
            selection => probe.Selected = selection.SelectedCertificate,
            () => probe.Denied = true,
            () => probe.Handled = true,
            () => probe.Completed = true,
            CancellationToken.None);

        Assert.True(probe.Denied);
        Assert.True(probe.Handled);
        Assert.True(probe.Completed);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenAuthorizedNativeApplyThrows_DeniesAndCompletes()
    {
        var probe = new DeferralProbe();
        var certificate = new WebClientCertificate("AA11", "CN=Target", "CN=Issuer");

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, [certificate]),
            (_, _) => ValueTask.FromResult(WebClientCertificateSelection.Select(
                certificate,
                new ClientCertificateSelectionAuthorization())),
            _ => throw new InvalidOperationException("native assignment failed"),
            () => probe.Denied = true,
            () => probe.Handled = true,
            () => probe.Completed = true,
            CancellationToken.None);

        Assert.True(probe.Denied);
        Assert.True(probe.Handled);
        Assert.True(probe.Completed);
        Assert.Null(probe.Selected);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenCompleteThrows_AttemptsCompletionExactlyOnce()
    {
        var completeCalls = 0;
        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, []),
            (_, _) => ValueTask.FromResult(WebClientCertificateSelection.Deny()),
            _ => { },
            () => { },
            () => { },
            () => { completeCalls++; throw new InvalidOperationException("complete failed"); },
            CancellationToken.None);

        Assert.Equal(1, completeCalls);
    }

    [Fact]
    public async Task DeferralDispatcher_WhenDeferralAcquisitionThrows_DefaultDeniesWithoutEscaping()
    {
        var probe = new DeferralProbe();
        var completeCalls = 0;

        await WebClientCertificateRequestDeferralDispatcher.DispatchAsync(
            () => throw new InvalidOperationException("get deferral failed"),
            () => new WebClientCertificateRequest(new Uri("https://b2b.ccb.com/"), false, []),
            (_, _) => ValueTask.FromResult(WebClientCertificateSelection.Deny()),
            _ => { },
            () => probe.Denied = true,
            () => probe.Handled = true,
            CancellationToken.None);

        Assert.True(probe.Denied);
        Assert.True(probe.Handled);
        Assert.Equal(0, completeCalls);
    }

    [Fact]
    public async Task UiThreadResourceDisposer_FromWorkerThread_DispatchesCleanupOnceAcrossRepeatedDispose()
    {
        var dispatchCalls = 0;
        var cleanupCalls = 0;
        var disposer = new UiThreadResourceDisposer(
            () => false,
            action =>
            {
                dispatchCalls++;
                action();
                return Task.CompletedTask;
            },
            () => cleanupCalls++);

        await disposer.DisposeAsync();
        await disposer.DisposeAsync();

        Assert.Equal(1, dispatchCalls);
        Assert.Equal(1, cleanupCalls);
    }

    private static WebClientCertificateRequest CreateMatchingWebRequest() => new(
        new Uri("https://b2b.ccb.com/"),
        false,
        [new WebClientCertificate(
            "1111111111111111111111111111111111111111",
            "CN=Target",
            "CN=Issuer")]);

    private sealed class RecordingWebViewControl : IWebView2Control
    {
        public event EventHandler<WebNavigationStartingEventArgs>? NavigationStarting;

        public event EventHandler<WebNavigationCompletedEventArgs>? NavigationCompleted;

        private WebClientCertificateRequestHandler? _clientCertificateHandler;

        private readonly TaskCompletionSource _continueRegistration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InitializeCount { get; private set; }

        public int CertificateHandlerRegistrationCount { get; private set; }

        public bool BlockRegistration { get; set; }

        public bool DeferNavigationStarting { get; set; }

        public Exception? NavigationStartException { get; set; }

        public Exception? NavigationStartTaskException { get; set; }

        public bool CancelNavigationStartTask { get; set; }

        public bool CompleteNavigationOnStop { get; set; } = true;

        public int NavigateCount { get; private set; }
        public int StopCount { get; private set; }

        public Queue<long> NavigationIds { get; } = new();

        public TaskCompletionSource RegistrationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource NavigationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource NavigationInvoked { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<long>? _deferredNavigationId;

        public ConcurrentQueue<string> Operations { get; } = new();

        public Uri? LastTarget { get; private set; }

        public Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            Operations.Enqueue("initialize");
            return Task.CompletedTask;
        }

        public async Task RegisterClientCertificateRequestHandlerAsync(
            WebClientCertificateRequestHandler handler,
            CancellationToken cancellationToken)
        {
            _clientCertificateHandler = handler;
            CertificateHandlerRegistrationCount++;
            Operations.Enqueue("register");
            RegistrationStarted.TrySetResult();
            if (BlockRegistration)
            {
                await _continueRegistration.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<long> StartNavigationAsync(Uri target, CancellationToken cancellationToken)
        {
            if (NavigationStartException is not null)
            {
                throw NavigationStartException;
            }

            NavigateCount++;
            LastTarget = target;
            Operations.Enqueue("navigate");
            NavigationInvoked.TrySetResult();
            if (CancelNavigationStartTask)
            {
                return Task.FromCanceled<long>(new CancellationToken(canceled: true));
            }
            if (NavigationStartTaskException is not null)
            {
                return Task.FromException<long>(NavigationStartTaskException);
            }
            if (!DeferNavigationStarting)
            {
                LastNavigationId = NavigationIds.Count == 0 ? 1 : NavigationIds.Dequeue();
                NavigationStarted.TrySetResult();
                return Task.FromResult(LastNavigationId);
            }

            _deferredNavigationId = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _deferredNavigationId.Task.WaitAsync(cancellationToken);
        }

        public Task StopNavigationAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            if (CompleteNavigationOnStop && LastNavigationId > 0)
            {
                CompleteNavigation(LastNavigationId, false, "OperationCanceled");
            }

            return Task.CompletedTask;
        }

        public void RaiseNavigationStarting(long navigationId)
        {
            LastNavigationId = navigationId;
            NavigationStarted.TrySetResult();
            _deferredNavigationId?.TrySetResult(navigationId);
        }

        public WebNavigationStartingEventArgs RaiseExternalNavigationStarting(
            long navigationId,
            Uri? target = null)
        {
            LastNavigationId = navigationId;
            var eventArgs = new WebNavigationStartingEventArgs(
                navigationId,
                isUserInitiated: true,
                target);
            NavigationStarting?.Invoke(this, eventArgs);
            return eventArgs;
        }

        public void ResetNavigationInvoked() => NavigationInvoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long LastNavigationId { get; private set; }

        public void CompleteNavigation(bool success, string? error = null) =>
            CompleteNavigation(LastNavigationId, success, error);

        public void CompleteNavigation(long navigationId, bool success, string? error = null) =>
            NavigationCompleted?.Invoke(this, new(navigationId, success, error));

        public void AllowRegistration() => _continueRegistration.TrySetResult();

        public ValueTask<WebClientCertificateSelection> RaiseClientCertificateRequestAsync(
            WebClientCertificateRequest request) =>
            _clientCertificateHandler is null
                ? ValueTask.FromResult(WebClientCertificateSelection.Deny())
                : _clientCertificateHandler(request, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            _deferredNavigationId?.TrySetException(
                new ObjectDisposedException(nameof(RecordingWebViewControl)));
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCertificateHandler(bool selectFirstCertificate = false)
        : IBrowserClientCertificateRequestHandler
    {
        public int CallCount { get; private set; }

        public AuthenticationAttempt? ResultAttempt { get; set; }

        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
            ClientCertificateRequestSnapshot request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(selectFirstCertificate
                ? new ClientCertificateRequestMatchResult(
                    ClientCertificateRequestMatchStatus.Selected,
                    request.AvailableCertificates[0],
                    ResultAttempt)
                : ClientCertificateRequestMatchResult.TokenNotUnlocked());
        }
    }

    private sealed class ThrowingCertificateHandler : IBrowserClientCertificateRequestHandler
    {
        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
            ClientCertificateRequestSnapshot request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ClientCertificateRequestMatchResult>(new InvalidOperationException("failure"));
    }

    private sealed class SelectCandidateHandler(int index, AuthenticationAttempt attempt)
        : IBrowserClientCertificateRequestHandler
    {
        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
            ClientCertificateRequestSnapshot request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ClientCertificateRequestMatchResult(
                ClientCertificateRequestMatchStatus.Selected,
                request.AvailableCertificates[index],
                attempt));
    }

    private sealed class ManualSelectionHandler(
        ClientCertificateRequestMatchStatus status,
        AuthenticationAttempt attempt)
        : IBrowserClientCertificateRequestHandler
    {
        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
            ClientCertificateRequestSnapshot request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ClientCertificateRequestMatchResult(
                status,
                Attempt: attempt));
    }

    private sealed class RecordingAuthenticationReporter : IBrowserClientCertificateAuthenticationReporter
    {
        public AuthenticationAttempt? CertificateSelectedAttempt { get; private set; }

        public AuthenticationAttempt? SuccessAttempt { get; private set; }

        public AuthenticationAttempt? TimeoutAttempt { get; private set; }

        public AuthenticationAttempt? FailureAttempt { get; private set; }

        public TaskCompletionSource SuccessReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailureReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TimeoutReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool? PreCertificateNavigationSucceeded { get; private set; }

        public List<(long NavigationId, bool IsSuccess)> PreCertificateTerminals { get; } = [];

        public List<string> NavigationLifecycleEvents { get; } = [];

        public TaskCompletionSource PreCertificateTerminalReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? CertificateSelectedException { get; init; }

        public bool BlockNavigationStarted { get; init; }

        public Exception? NavigationStartedException { get; set; }

        public TaskCompletionSource NavigationStartReportInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _continueNavigationStartReport =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ReportNavigationStartedAsync(
            long navigationId,
            CancellationToken cancellationToken = default)
        {
            NavigationLifecycleEvents.Add($"started:{navigationId}");
            NavigationStartReportInvoked.TrySetResult();
            if (NavigationStartedException is not null)
            {
                throw NavigationStartedException;
            }
            if (BlockNavigationStarted)
            {
                await _continueNavigationStartReport.Task.WaitAsync(cancellationToken);
            }
        }

        public void AllowNavigationStartReport() => _continueNavigationStartReport.TrySetResult();

        public Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            CertificateSelectedAttempt = attempt;
            if (CertificateSelectedException is not null)
            {
                return Task.FromException<ClientCertificateSelectionAuthorization?>(CertificateSelectedException);
            }
            return Task.FromResult<ClientCertificateSelectionAuthorization?>(
                new ClientCertificateSelectionAuthorization());
        }

        public Task ReportTlsCompletedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default)
        {
            SuccessAttempt = attempt;
            SuccessReported.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ReportTlsFailureAsync(AuthenticationAttempt attempt, int? publicErrorCode = null, CancellationToken cancellationToken = default)
        {
            FailureAttempt = attempt;
            FailureReported.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ReportTimeoutAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default)
        {
            TimeoutAttempt = attempt;
            TimeoutReported.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ReportNavigationCompletedWithoutCertificateAsync(
            long navigationId,
            bool isSuccess,
            CancellationToken cancellationToken = default)
        {
            PreCertificateNavigationSucceeded = isSuccess;
            PreCertificateTerminals.Add((navigationId, isSuccess));
            NavigationLifecycleEvents.Add($"terminal:{navigationId}");
            PreCertificateTerminalReported.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingAcknowledgmentReporter : IBrowserClientCertificateAuthenticationReporter
    {
        private readonly TaskCompletionSource<ClientCertificateSelectionAuthorization?> _acknowledgment =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AcknowledgmentStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SuccessReported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }
        public int TimeoutCount { get; private set; }

        public async Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            AcknowledgmentStarted.TrySetResult();
            return await _acknowledgment.Task.WaitAsync(cancellationToken);
        }

        public void FailAcknowledgment(Exception exception) =>
            _acknowledgment.TrySetException(exception);

        public Task ReportTlsCompletedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            SuccessCount++;
            SuccessReported.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ReportTlsFailureAsync(
            AuthenticationAttempt attempt,
            int? publicErrorCode = null,
            CancellationToken cancellationToken = default)
        {
            FailureCount++;
            return Task.CompletedTask;
        }

        public Task ReportTimeoutAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            TimeoutCount++;
            return Task.CompletedTask;
        }

        public Task ReportNavigationCompletedWithoutCertificateAsync(
            long navigationId,
            bool isSuccess,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ManualTimeoutScheduler : IBrowserTimeoutScheduler
    {
        private readonly TaskCompletionSource _elapsed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await _elapsed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public void Elapse() => _elapsed.TrySetResult();
    }

    private sealed class DeferralProbe
    {
        public WebClientCertificate? Selected { get; set; }

        public bool Denied { get; set; }

        public bool Handled { get; set; }

        public bool Completed { get; set; }
    }

    private sealed class TestPinStore : IClientCertificatePinStore
    {
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificatePin?>(ClientCertificatePin.CopyOf(['t', 'e', 's', 't']));

        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestStampStore(ClientCertificateAuthenticationOptions authentication)
        : IClientCertificateValidationStampStore
    {
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificateValidationStamp?>(new(authentication.Id, authentication.GetNormalizedHash()));

        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestUnlocker(RecordingUnlockSession session) : IClientCertificateTokenUnlocker
    {
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ClientCertificateTokenUnlockResult.Succeeded(session));
    }

    private sealed class SequenceTestUnlocker(params RecordingUnlockSession[] sessions)
        : IClientCertificateTokenUnlocker
    {
        private readonly Queue<RecordingUnlockSession> _sessions = new(sessions);

        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ClientCertificateTokenUnlockResult.Succeeded(_sessions.Dequeue()));
    }

    private sealed class BlockingAfterSelectedHandler(AuthenticationCoordinator coordinator)
        : IBrowserClientCertificateRequestHandler
    {
        private readonly TaskCompletionSource _allowReturn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Selected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
            ClientCertificateRequestSnapshot request,
            CancellationToken cancellationToken = default)
        {
            var result = await coordinator.HandleAsync(request, cancellationToken);
            Selected.TrySetResult();
            await _allowReturn.Task.WaitAsync(cancellationToken);
            return result;
        }

        public void AllowReturn() => _allowReturn.TrySetResult();
    }

    private sealed class RecordingUnlockSession : IClientCertificateTokenUnlockSession
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class CancelCertificateSelectedReporter(AuthenticationCoordinator coordinator)
        : IBrowserClientCertificateAuthenticationReporter
    {
        public Task ReportNavigationStartedAsync(
            long browserOperationId,
            CancellationToken cancellationToken = default) =>
            coordinator.ReportNavigationStartedAsync(browserOperationId, cancellationToken);

        public Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<ClientCertificateSelectionAuthorization?>(new CancellationToken(canceled: true));

        public Task ReportTlsCompletedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) =>
            coordinator.ReportTlsCompletedAsync(attempt, cancellationToken);

        public Task ReportTlsFailureAsync(AuthenticationAttempt attempt, int? publicErrorCode = null, CancellationToken cancellationToken = default) =>
            coordinator.ReportTlsFailureAsync(attempt, publicErrorCode, cancellationToken);

        public Task ReportTimeoutAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) =>
            coordinator.ReportTimeoutAsync(attempt, cancellationToken);

        public Task ReportNavigationCompletedWithoutCertificateAsync(long navigationId, bool isSuccess, CancellationToken cancellationToken = default) =>
            coordinator.ReportNavigationCompletedWithoutCertificateAsync(navigationId, isSuccess, cancellationToken);
    }

    private sealed class RemoveDeviceBeforeAcknowledgmentReporter(AuthenticationCoordinator coordinator)
        : IBrowserClientCertificateAuthenticationReporter
    {
        public int AttemptTerminalCount { get; private set; }

        public Task ReportNavigationStartedAsync(
            long browserOperationId,
            CancellationToken cancellationToken = default) =>
            coordinator.ReportNavigationStartedAsync(browserOperationId, cancellationToken);

        public async Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            await coordinator.HandleDevicePresenceAsync(false, cancellationToken);
            return await coordinator.ReportCertificateSelectedAsync(attempt, cancellationToken);
        }

        public async Task ReportTlsCompletedAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            AttemptTerminalCount++;
            await coordinator.ReportTlsCompletedAsync(attempt, cancellationToken);
        }

        public async Task ReportTlsFailureAsync(
            AuthenticationAttempt attempt,
            int? publicErrorCode = null,
            CancellationToken cancellationToken = default)
        {
            AttemptTerminalCount++;
            await coordinator.ReportTlsFailureAsync(attempt, publicErrorCode, cancellationToken);
        }

        public async Task ReportTimeoutAsync(
            AuthenticationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            AttemptTerminalCount++;
            await coordinator.ReportTimeoutAsync(attempt, cancellationToken);
        }

        public Task ReportNavigationCompletedWithoutCertificateAsync(
            long navigationId,
            bool isSuccess,
            CancellationToken cancellationToken = default) =>
            coordinator.ReportNavigationCompletedWithoutCertificateAsync(
                navigationId, isSuccess, cancellationToken);
    }
}
