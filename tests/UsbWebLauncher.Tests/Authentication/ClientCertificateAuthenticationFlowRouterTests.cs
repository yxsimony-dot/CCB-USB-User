using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class ClientCertificateAuthenticationFlowRouterTests
{
    [Fact]
    public async Task VerifyAsync_UsesExactMatcherAndCompletesOnlyAfterTlsSuccess()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var router = new ClientCertificateAuthenticationFlowRouter(
            Authentication(),
            browser,
            new Uri("https://example.test/entry"),
            fallback,
            fallback);
        var states = new List<AuthenticationState>();
        router.StateChanged += (_, args) => states.Add(args.State);
        browser.OnNavigate = async (target, operationId) =>
        {
            Assert.Equal(new Uri("https://example.test/entry"), target);
            await router.ReportNavigationStartedAsync(operationId);
            var candidate = MatchingCandidate();
            var match = await router.HandleAsync(new(
                new Uri("https://example.test/auth"),
                false,
                [candidate],
                operationId));
            Assert.Equal(ClientCertificateRequestMatchStatus.Selected, match.Status);
            Assert.Same(candidate, match.SelectedCertificate);
            Assert.Equal(operationId, match.Attempt!.BrowserOperationId);
            var authorization = await router.ReportCertificateSelectedAsync(match.Attempt!);
            Assert.NotNull(authorization);
            Assert.True(authorization!.TryCommit(() => { }));
            await router.ReportTlsCompletedAsync(match.Attempt!);
        };

        using var pin = RandomPin();
        var result = await router.VerifyAsync(Authentication(), new RecordingSession(), pin);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fallback.HandleCount);
        Assert.Contains(states, state => state.Stage == AuthenticationStage.Authenticating);
    }

    [Fact]
    public async Task VerifyAsync_WhenCertificateDoesNotMatch_FailsClosed()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var router = new ClientCertificateAuthenticationFlowRouter(
            Authentication(), browser, new Uri("https://example.test/"), fallback, fallback);
        browser.OnNavigate = async (_, operationId) =>
        {
            await router.ReportNavigationStartedAsync(operationId);
            var match = await router.HandleAsync(new(
                new Uri("https://example.test/"),
                false,
                [new("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "CN=Test", "CN=Issuer")],
                operationId));
            Assert.Equal(ClientCertificateRequestMatchStatus.CertificateMissing, match.Status);
        };

        using var pin = RandomPin();
        var result = await router.VerifyAsync(Authentication(), new RecordingSession(), pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.CertificateRejected, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_IgnoresAnUnrelatedNavigationAndUsesOnlyItsReservedOperationIdentity()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var router = new ClientCertificateAuthenticationFlowRouter(
            Authentication(), browser, new Uri("https://example.test/"), fallback, fallback);
        browser.OnNavigate = async (_, reservedOperationId) =>
        {
            var unrelatedOperationId = reservedOperationId + 10_000;
            await router.ReportNavigationStartedAsync(unrelatedOperationId);
            var unrelated = await router.HandleAsync(new(
                new Uri("https://example.test/"),
                false,
                [MatchingCandidate()],
                unrelatedOperationId));
            Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest, unrelated.Status);

            await router.ReportNavigationStartedAsync(reservedOperationId);
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"),
                false,
                [MatchingCandidate()],
                reservedOperationId));
            var authorization = await router.ReportCertificateSelectedAsync(selected.Attempt!);
            Assert.NotNull(authorization);
            await router.ReportTlsCompletedAsync(selected.Attempt!);
        };

        using var pin = RandomPin();
        var result = await router.VerifyAsync(Authentication(), new RecordingSession(), pin);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fallback.NavigationStartedCount);
        Assert.Equal(1, fallback.HandleCount);
    }

    [Fact]
    public async Task OutsideSetup_DelegatesBrowserCallbacksToNormalCoordinator()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var router = new ClientCertificateAuthenticationFlowRouter(
            Authentication(), browser, new Uri("https://example.test/"), fallback, fallback);
        var request = new ClientCertificateRequestSnapshot(
            new Uri("https://example.test/"), false, [], 88);

        await router.ReportNavigationStartedAsync(88);
        var result = await router.HandleAsync(request);
        await router.ReportNavigationCompletedWithoutCertificateAsync(88, false);

        Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest, result.Status);
        Assert.Equal(1, fallback.HandleCount);
        Assert.Equal(1, fallback.NavigationStartedCount);
        Assert.Equal(1, fallback.NavigationWithoutCertificateCount);
    }

    [Fact]
    public async Task SetupCertificateSelection_ArmsOnceAndReturnsAuthorizationBeforePromptCompletion()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"),
                false,
                [MatchingCandidate()],
                operationId));

            var authorization = await router.ReportCertificateSelectedAsync(selected.Attempt!)
                .WaitAsync(TimeSpan.FromSeconds(1));
            var duplicate = await router.ReportCertificateSelectedAsync(selected.Attempt! with { });

            Assert.NotNull(authorization);
            Assert.Same(authorization, duplicate);
            Assert.Equal(1, continuation.ArmCount);
            promptAttempt.Complete(new(ClientCertificateNativePromptStatus.Success));
            await continuation.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await router.ReportTlsCompletedAsync(selected.Attempt!);
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Theory]
    [InlineData(ClientCertificateNativePromptStatus.TargetMismatch, ClientCertificateSetupValidationStatus.Incompatible)]
    [InlineData(ClientCertificateNativePromptStatus.TimedOut, ClientCertificateSetupValidationStatus.TimedOut)]
    [InlineData(ClientCertificateNativePromptStatus.Cancelled, ClientCertificateSetupValidationStatus.Cancelled)]
    public async Task SetupContinuationFailure_PreventsSuccessAndDisposesAttempt(
        ClientCertificateNativePromptStatus promptStatus,
        ClientCertificateSetupValidationStatus expectedStatus)
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            var authorization = await router.ReportCertificateSelectedAsync(selected.Attempt!);
            Assert.NotNull(authorization);
            promptAttempt.Complete(new(promptStatus));
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task SetupContinuationArmFailure_ReturnsNoAuthorizationAndFailsClosed()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.BrokerUnavailable));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            var authorization = await router.ReportCertificateSelectedAsync(selected.Attempt!);
            Assert.Null(authorization);
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.Incompatible, result.Status);
        Assert.Equal(1, continuation.ArmCount);
    }

    [Fact]
    public async Task SetupCertificateSelection_WhenConfiguredContinuationIsMissing_ReturnsNoAuthorizationAndFailsClosed()
    {
        ClientCertificateSelectionAuthorization? authorization = null;
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            authorization = await router.ReportCertificateSelectedAsync(selected.Attempt!);
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Null(authorization);
        Assert.Equal(ClientCertificateSetupValidationStatus.Incompatible, result.Status);
    }

    [Fact]
    public async Task SetupCertificateSelection_WhenCallerIsAlreadyCancelled_CompletesCancelledAndClearsOwnedResources()
    {
        ClientCertificateSelectionAuthorization? authorization = null;
        using var selectionCancellation = new CancellationTokenSource();
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.BrokerUnavailable));
        var authentication = AuthenticationWithContinuation();
        await using var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        var session = new RecordingSession();
        var pinStore = new RecordingPinStore();
        var stampStore = new RecordingStampStore();
        var service = new ClientCertificatePinSetupService(
            authentication,
            pinStore,
            stampStore,
            new SuccessfulUnlocker(session),
            router);
        var pin = RandomPin();
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            selectionCancellation.Cancel();
            authorization = await router.ReportCertificateSelectedAsync(
                selected.Attempt!, selectionCancellation.Token);
        };

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.Null(authorization);
        Assert.Equal(ClientCertificateSetupValidationStatus.Cancelled, result.Status);
        Assert.Equal(0, continuation.ArmCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, pinStore.SaveCount);
        Assert.Equal(0, stampStore.SaveCount);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task SetupCertificateSelection_WhenQueuedAcknowledgementIsCancelled_CompletesCancelledAndClearsOwnedResources()
    {
        ClientCertificateSelectionAuthorization? firstAuthorization = null;
        ClientCertificateSelectionAuthorization? queuedAuthorization = null;
        Exception? queuedException = null;
        using var queuedCancellation = new CancellationTokenSource();
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new BlockingNativePromptContinuation(promptAttempt);
        var authentication = AuthenticationWithContinuation();
        await using var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        var session = new RecordingSession();
        var pinStore = new RecordingPinStore();
        var stampStore = new RecordingStampStore();
        var service = new ClientCertificatePinSetupService(
            authentication,
            pinStore,
            stampStore,
            new SuccessfulUnlocker(session),
            router);
        var pin = RandomPin();
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            var firstSelection = router.ReportCertificateSelectedAsync(selected.Attempt!);
            await continuation.ArmStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var queuedSelection = router.ReportCertificateSelectedAsync(
                selected.Attempt! with { }, queuedCancellation.Token);
            queuedCancellation.Cancel();
            try
            {
                queuedAuthorization = await queuedSelection.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception exception)
            {
                queuedException = exception;
            }

            continuation.AllowArm.TrySetResult();
            firstAuthorization = await firstSelection;
            if (firstAuthorization is not null)
            {
                await router.ReportTlsFailureAsync(selected.Attempt!);
            }
        };

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.Null(queuedException);
        Assert.Null(firstAuthorization);
        Assert.Null(queuedAuthorization);
        Assert.Equal(ClientCertificateSetupValidationStatus.Cancelled, result.Status);
        Assert.Equal(1, continuation.ArmCount);
        Assert.Equal(1, promptAttempt.DisposeCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, pinStore.SaveCount);
        Assert.Equal(0, stampStore.SaveCount);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task RouterDisposalDuringSetupArm_ReturnsNoAuthorizationAndDisposesReturnedAttempt()
    {
        ClientCertificateSelectionAuthorization? authorization = null;
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new BlockingNativePromptContinuation(promptAttempt);
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            var selection = router.ReportCertificateSelectedAsync(selected.Attempt!);
            await continuation.ArmStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var disposal = router.DisposeAsync().AsTask();
            continuation.AllowArm.TrySetResult();
            authorization = await selection;
            await disposal;
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Null(authorization);
        Assert.Equal(ClientCertificateSetupValidationStatus.Cancelled, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Theory]
    [InlineData(true, ClientCertificateSetupValidationStatus.TlsFailed)]
    [InlineData(false, ClientCertificateSetupValidationStatus.TimedOut)]
    public async Task SetupTlsTerminalFailureAfterArm_DisposesAttempt(
        bool reportTlsFailure,
        ClientCertificateSetupValidationStatus expectedStatus)
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            if (reportTlsFailure)
            {
                await router.ReportTlsFailureAsync(selected.Attempt!, 73);
            }
            else
            {
                await router.ReportTimeoutAsync(selected.Attempt!);
            }
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task SetupTlsSuccessBeforePromptCompletion_WaitsForPromptThenSucceeds()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var tlsReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            tlsReported.TrySetResult();
            await router.ReportTlsCompletedAsync(selected.Attempt!);
        };
        using var pin = RandomPin();

        var verification = router.VerifyAsync(authentication, new RecordingSession(), pin);
        await tlsReported.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(verification.IsCompleted);
        promptAttempt.Complete(new(ClientCertificateNativePromptStatus.Success));
        var result = await verification;

        Assert.Equal(ClientCertificateSetupValidationStatus.Success, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task SetupCancellationAfterArm_DisposesAttemptAndReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            cancellation.Cancel();
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin, cancellation.Token);

        Assert.Equal(ClientCertificateSetupValidationStatus.Cancelled, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task RouterDisposalAfterArm_CancelsSetupAndDisposesAttempt()
    {
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            await router.DisposeAsync();
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.Cancelled, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentSetupTerminalCallbacks_AwaitTheSameAttemptDisposal()
    {
        bool? timeoutCompletedBeforeDisposal = null;
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new BlockingDisposeNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            var tlsFailure = router.ReportTlsFailureAsync(selected.Attempt!, 79);
            await promptAttempt.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var timeout = router.ReportTimeoutAsync(selected.Attempt!);
            timeoutCompletedBeforeDisposal = timeout.IsCompleted;
            promptAttempt.AllowDispose.TrySetResult();
            await Task.WhenAll(tlsFailure, timeout);
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.TlsFailed, result.Status);
        Assert.Equal(1, promptAttempt.DisposeCount);
        Assert.False(timeoutCompletedBeforeDisposal);
        Assert.Equal(0, fallback.TimeoutCount);
    }

    [Fact]
    public async Task ConcurrentTlsSuccessAfterFailure_AwaitsTheSameAttemptDisposal()
    {
        bool? tlsSuccessCompletedBeforeDisposal = null;
        var browser = new CallbackBrowserHost();
        var fallback = new RejectingFallback();
        var promptAttempt = new BlockingDisposeNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var authentication = AuthenticationWithContinuation();
        var router = new ClientCertificateAuthenticationFlowRouter(
            authentication,
            browser,
            new Uri("https://example.test/"),
            fallback,
            fallback,
            continuation);
        browser.OnNavigate = async (_, operationId) =>
        {
            var selected = await router.HandleAsync(new(
                new Uri("https://example.test/"), false, [MatchingCandidate()], operationId));
            await router.ReportCertificateSelectedAsync(selected.Attempt!);
            var tlsFailure = router.ReportTlsFailureAsync(selected.Attempt!, 83);
            await promptAttempt.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var tlsSuccess = router.ReportTlsCompletedAsync(selected.Attempt!);
            tlsSuccessCompletedBeforeDisposal = tlsSuccess.IsCompleted;
            promptAttempt.AllowDispose.TrySetResult();
            await Task.WhenAll(tlsFailure, tlsSuccess);
        };
        using var pin = RandomPin();

        var result = await router.VerifyAsync(authentication, new RecordingSession(), pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.TlsFailed, result.Status);
        Assert.False(tlsSuccessCompletedBeforeDisposal);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    private static ClientCertificateAuthenticationOptions Authentication() => new(
        "test-auth", "example.test", 443,
        new("00112233445566778899AABBCCDDEEFF00112233", "CN=Test", "CN=Issuer"),
        new("Test Provider", 1, "Test Container", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/test-auth");

    private static ClientCertificateAuthenticationOptions AuthenticationWithContinuation() =>
        Authentication() with
        {
            NativePromptContinuation = new(
                true,
                "test-pipe",
                "test-policy",
                @"C:\Program Files\Test Vendor\Prompt.exe",
                new string('A', 64),
                new string('B', 40),
                "Test prompt",
                "#32770",
                ["Edit"],
                10_000)
        };

    private static ClientCertificatePin RandomPin() =>
        ClientCertificatePin.CopyOf($"test-{Guid.NewGuid():N}".AsSpan());

    private static ClientCertificateCandidate MatchingCandidate() => new(
        "00112233445566778899AABBCCDDEEFF00112233", "CN=Test", "CN=Issuer");

    private sealed class CallbackBrowserHost : ICorrelatedBrowserHost
    {
        public Func<Uri, long, Task>? OnNavigate { get; set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NavigateAsync(Uri target, CancellationToken cancellationToken = default) =>
            NavigateAsync(target, BrowserOperationIdentity.Create(), cancellationToken);
        public Task NavigateAsync(Uri target, long browserOperationId, CancellationToken cancellationToken = default) =>
            OnNavigate?.Invoke(target, browserOperationId) ?? Task.CompletedTask;
    }

    private sealed class RecordingSession : IClientCertificateTokenUnlockSession
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingPinStore : IClientCertificatePinStore
    {
        public int SaveCount { get; private set; }

        public ValueTask<ClientCertificatePin?> ReadAsync(
            string targetName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificatePin?>(null);

        public ValueTask SaveAsync(
            string targetName,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string targetName,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingStampStore : IClientCertificateValidationStampStore
    {
        public int SaveCount { get; private set; }

        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(
            string authenticationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificateValidationStamp?>(null);

        public ValueTask SaveAsync(
            ClientCertificateValidationStamp stamp,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string authenticationId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class SuccessfulUnlocker(RecordingSession session) : IClientCertificateTokenUnlocker
    {
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ClientCertificateTokenUnlockResult.Succeeded(session));
    }

    private sealed class RejectingFallback :
        IBrowserClientCertificateRequestHandler,
        IBrowserClientCertificateAuthenticationReporter
    {
        public int HandleCount { get; private set; }
        public int NavigationStartedCount { get; private set; }
        public int NavigationWithoutCertificateCount { get; private set; }
        public int TimeoutCount { get; private set; }

        public ValueTask<ClientCertificateRequestMatchResult> HandleAsync(ClientCertificateRequestSnapshot request, CancellationToken cancellationToken = default)
        {
            HandleCount++;
            return ValueTask.FromResult(ClientCertificateRequestMatchResult.InvalidRequest());
        }

        public Task ReportNavigationStartedAsync(long browserOperationId, CancellationToken cancellationToken = default) { NavigationStartedCount++; return Task.CompletedTask; }
        public Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) => Task.FromResult<ClientCertificateSelectionAuthorization?>(null);
        public Task ReportTlsCompletedAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportTlsFailureAsync(AuthenticationAttempt attempt, int? publicErrorCode = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportTimeoutAsync(AuthenticationAttempt attempt, CancellationToken cancellationToken = default) { TimeoutCount++; return Task.CompletedTask; }
        public Task ReportNavigationCompletedWithoutCertificateAsync(long browserOperationId, bool isSuccess, CancellationToken cancellationToken = default) { NavigationWithoutCertificateCount++; return Task.CompletedTask; }
    }

    private sealed class RecordingNativePromptContinuation(ClientCertificateNativePromptArmResult armResult) :
        IClientCertificateNativePromptContinuation
    {
        public int ArmCount { get; private set; }
        public TaskCompletionSource CompletionObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
            ClientCertificateAuthenticationOptions authentication,
            AuthenticationAttempt attempt,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            ArmCount++;
            if (armResult.Attempt is { } promptAttempt)
            {
                _ = promptAttempt.Completion.ContinueWith(
                    _ => CompletionObserved.TrySetResult(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return ValueTask.FromResult(armResult);
        }
    }

    private sealed class RecordingNativePromptAttempt : IClientCertificateNativePromptAttempt
    {
        private readonly TaskCompletionSource<ClientCertificateNativePromptResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClientCertificateNativePromptResult> Completion => _completion.Task;
        public int DisposeCount { get; private set; }

        public void Complete(ClientCertificateNativePromptResult result) => _completion.TrySetResult(result);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingNativePromptContinuation(RecordingNativePromptAttempt promptAttempt) :
        IClientCertificateNativePromptContinuation
    {
        public int ArmCount { get; private set; }
        public TaskCompletionSource ArmStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowArm { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
            ClientCertificateAuthenticationOptions authentication,
            AuthenticationAttempt attempt,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            ArmCount++;
            ArmStarted.TrySetResult();
            await AllowArm.Task.WaitAsync(cancellationToken);
            return new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt);
        }
    }

    private sealed class BlockingDisposeNativePromptAttempt : IClientCertificateNativePromptAttempt
    {
        private readonly TaskCompletionSource<ClientCertificateNativePromptResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClientCertificateNativePromptResult> Completion => _completion.Task;
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await AllowDispose.Task;
        }
    }
}
