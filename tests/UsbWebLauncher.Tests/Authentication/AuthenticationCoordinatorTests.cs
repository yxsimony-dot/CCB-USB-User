// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Reflection;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class AuthenticationCoordinatorTests
{
    [Fact]
    public async Task BrowserNative_PauseRejectsOldPageAndExplicitStartUsesNewSavedPin()
    {
        var authentication = AuthenticationWithContinuation() with { AuthenticationMode = "BrowserNative" };
        var pins = new RecordingPinStore("old-Test-Pin".ToCharArray());
        var stamps = new RecordingStampStore(null);
        var unlocker = new RecordingUnlocker();
        var continuation = new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.Armed, new RecordingNativePromptAttempt()))
        {
            InspectPin = pin =>
            {
                Assert.Equal("New-Test-Pin", new string(pin.AsMemory().Span));
                return ValueTask.CompletedTask;
            }
        };
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker, continuation, authentication);
        var gate = new ManualAuthenticationGate(new AuthenticationLauncherGate(coordinator), coordinator);
        await gate.AllowBusinessAsync();
        Assert.True(await gate.HandleDevicePresenceAsync(true));
        await coordinator.ReportNavigationStartedAsync(501);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(501, true);
        await gate.PauseAsync();
        await coordinator.ReportNavigationStartedAsync(502);
        Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest,
            (await coordinator.HandleAsync(CreateAuthorizedRequest(502))).Status);
        Assert.Equal(0, continuation.ArmCount);
        var setup = new ClientCertificatePinSetupService(authentication, pins, stamps, unlocker, new UnusedSetupVerifier(), gate);
        Assert.True(await setup.SaveFixedAsync(ClientCertificatePin.CopyOf("New-Test-Pin".AsMemory())));
        await coordinator.ReportNavigationStartedAsync(503);
        Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest,
            (await coordinator.HandleAsync(CreateAuthorizedRequest(503))).Status);
        await gate.AllowBusinessAsync();
        Assert.True(await gate.HandleDevicePresenceAsync(true));
        await coordinator.ReportNavigationStartedAsync(504);
        var selection = await coordinator.HandleAsync(CreateAuthorizedRequest(504));
        Assert.NotNull(await coordinator.ReportCertificateSelectedAsync(selection.Attempt!));
        Assert.Equal(1, continuation.ArmCount);
        Assert.Equal(0, unlocker.AttemptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserNative_UsesSharedPinWithoutStampOrLocalUnlock(bool oldStamp)
    {
        var authentication = AuthenticationWithContinuation() with { AuthenticationMode = "BrowserNative" };
        var pins = new RecordingPinStore("AbCd-test-123".ToCharArray());
        var stamps = new RecordingStampStore(oldStamp ? new(authentication.Id, "old-key-hash") : null);
        var unlocker = new RecordingUnlocker();
        var prompt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.Armed, prompt))
        {
            InspectPin = pin =>
            {
                Assert.Equal("AbCd-test-123", new string(pin.AsMemory().Span));
                return ValueTask.CompletedTask;
            }
        };
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker, continuation, authentication);
        await coordinator.HandleDevicePresenceAsync(true);
        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(0, stamps.ReadCount);
        Assert.Equal(0, unlocker.AttemptCount);
        await coordinator.ReportNavigationStartedAsync(201);
        var result = await coordinator.HandleAsync(CreateAuthorizedRequest(201));
        Assert.NotNull(result.Attempt);
        Assert.NotNull(await coordinator.ReportCertificateSelectedAsync(result.Attempt!));
        Assert.Equal(1, continuation.ArmCount);
        prompt.Complete(new(ClientCertificateNativePromptStatus.Success));
        await coordinator.ReportTlsCompletedAsync(result.Attempt!);
        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(0, stamps.SaveCount);
        Assert.All(continuation.PinPassedToArm!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task BrowserNative_MissingPinRequestsSetupWithoutLocalUnlock()
    {
        var authentication = AuthenticationWithContinuation() with { AuthenticationMode = "BrowserNative" };
        var unlocker = new RecordingUnlocker();
        var pins = new RecordingPinStore();
        await using var coordinator = CreateCoordinator(pins, new RecordingStampStore(null), unlocker,
            new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.BrokerUnavailable)), authentication);
        await coordinator.HandleDevicePresenceAsync(true);
        Assert.Equal(AuthenticationStage.RequiresPinSetup, coordinator.CurrentState.Stage);
        Assert.Equal(1, pins.ReadCount);
        Assert.Equal(0, unlocker.AttemptCount);
    }

    [Fact]
    public async Task BrowserNative_PageWithoutCertificateAllowsLoginAndLaterCertificateRequest()
    {
        var authentication = AuthenticationWithContinuation() with { AuthenticationMode = "BrowserNative" };
        var prompt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.Armed, prompt));
        await using var coordinator = CreateCoordinator(new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(null), new RecordingUnlocker(), continuation, authentication);
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(301);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(301, true);
        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        await coordinator.ReportNavigationStartedAsync(302);
        Assert.Equal(ClientCertificateRequestMatchStatus.InvalidRequest,
            (await coordinator.HandleAsync(CreateAuthorizedRequest(301))).Status);
        var result = await coordinator.HandleAsync(CreateAuthorizedRequest(302));
        Assert.Equal(ClientCertificateRequestMatchStatus.Selected, result.Status);
        Assert.NotNull(await coordinator.ReportCertificateSelectedAsync(result.Attempt!));
        Assert.Equal(1, continuation.ArmCount);
    }

    [Fact]
    public async Task BrowserNative_MetadataLossRevokesPromptAndDoesNotRetryUntilPhysicalRemoval()
    {
        var authentication = AuthenticationWithContinuation() with { AuthenticationMode = "BrowserNative" };
        var pins = new RecordingPinStore(RandomPinCharacters());
        var prompt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.Armed, prompt));
        await using var coordinator = CreateCoordinator(pins, new RecordingStampStore(null),
            new RecordingUnlocker(), continuation, authentication);
        var attempt = await StartAuthenticatingAsync(coordinator, 401);
        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);
        await coordinator.InvalidateDeviceMetadataAsync();
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportTlsCompletedAsync(attempt);
        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.False(authorization!.TryCommit(() => { }));
        Assert.Equal(1, prompt.DisposeCount);
        Assert.Equal(1, pins.ReadCount);
        await coordinator.HandleDevicePresenceAsync(false);
        await coordinator.HandleDevicePresenceAsync(true);
        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(2, pins.ReadCount);
    }

    [Fact]
    public async Task MetadataInvalidation_RevokesSelectionAndPromptWithoutResettingInsertionCycle()
    {
        var prompt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(new(ClientCertificateNativePromptArmStatus.Armed, prompt));
        var authentication = AuthenticationWithContinuation();
        var pins = new RecordingPinStore(RandomPinCharacters());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session));
        await using var coordinator = CreateCoordinator(pins, new RecordingStampStore(CreateValidStamp(authentication)),
            unlocker, continuation, authentication);
        var attempt = await StartAuthenticatingAsync(coordinator, 83);
        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);
        var cycle = coordinator.CurrentState.InsertionCycleId;

        await coordinator.InvalidateDeviceMetadataAsync();
        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportTlsCompletedAsync(attempt);

        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.DeviceUnavailable, coordinator.CurrentState.ErrorCategory);
        Assert.Equal(cycle, coordinator.CurrentState.InsertionCycleId);
        Assert.Null(coordinator.CurrentState.Attempt);
        Assert.False(authorization!.TryCommit(() => { }));
        Assert.Equal(1, prompt.DisposeCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, pins.ReadCount);
        Assert.Equal(1, unlocker.AttemptCount);
    }

    [Fact]
    public async Task DeviceArrival_WhenNoCredentialExists_RequiresPinSetupWithoutAttemptingUnlock()
    {
        var pins = new RecordingPinStore();
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker();
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("RequiresPinSetup", GetStage(coordinator));
        Assert.Equal("CredentialsUnavailable", GetErrorCategory(coordinator));
        Assert.Equal(0, unlocker.AttemptCount);
    }

    [Fact]
    public async Task DeviceArrival_WithPendingCredentialMarker_UsesStoredPinForOneValidationAttempt()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(ClientCertificateValidationStamp.CreatePending(
            Authentication.Id,
            Authentication.GetNormalizedHash()));
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("AwaitingCertificate", GetStage(coordinator));
        Assert.Equal(1, pins.ReadCount);
        Assert.Equal(1, unlocker.AttemptCount);
        Assert.True(stamps.StoredStamp?.IsPendingFor(
            Authentication.Id,
            Authentication.GetNormalizedHash()));
    }

    [Fact]
    public async Task DeviceArrival_WhenValidationStampHashChanged_DoesNotReadStoredPin()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(new(
            Authentication.Id,
            "A-DIFFERENT-HASH"));
        var unlocker = new RecordingUnlocker();
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("RequiresPinSetup", GetStage(coordinator));
        Assert.Equal("ValidationStampInvalid", GetErrorCategory(coordinator));
        Assert.Equal(0, pins.ReadCount);
        Assert.Equal(0, unlocker.AttemptCount);
    }

    [Fact]
    public async Task TlsSuccess_ForUnvalidatedStoredPin_WritesMatchingValidationStamp()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(ClientCertificateValidationStamp.CreatePending(
            Authentication.Id,
            Authentication.GetNormalizedHash()));
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        var attempt = await StartAuthenticatingAsync(coordinator, 7);
        await coordinator.ReportTlsCompletedAsync(attempt);

        Assert.Equal("Ready", GetStage(coordinator));
        Assert.Equal(CreateValidStamp(), stamps.StoredStamp);
        Assert.Equal(1, stamps.SaveCount);
    }

    [Fact]
    public async Task DeviceArrival_WithValidatedCredentialUnlocksOnceAndDisposesReadSecret()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("AwaitingCertificate", GetStage(coordinator));
        Assert.Equal(1, unlocker.AttemptCount);
        Assert.NotNull(pins.LastReadPin);
        Assert.All(pins.LastReadPin!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task DuplicateDeviceAndCertificateEvents_KeepOneUnlockAttemptInTheSameCycle()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);
        var request = CreateAuthorizedRequest();

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "ReportNavigationStartedAsync", 1L);
        await InvokeAsync(coordinator, "HandleCertificateRequestAsync", request);
        await InvokeAsync(coordinator, "HandleCertificateRequestAsync", request);

        Assert.Equal("Authenticating", GetStage(coordinator));
        Assert.Equal(1, unlocker.AttemptCount);
        Assert.Equal(1, pins.ReadCount);
    }

    [Fact]
    public async Task ExplicitPinRejection_DeletesCredentialAndStampThenRequiresReset()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Failed(
            ClientCertificateTokenUnlockStatus.PinRejected,
            87));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("RequiresPinSetup", GetStage(coordinator));
        Assert.Equal("PinRejected", GetErrorCategory(coordinator));
        Assert.Equal(87, GetPublicErrorCode(coordinator));
        Assert.False(pins.HasStoredPin);
        Assert.Null(stamps.StoredStamp);
        Assert.All(pins.LastReadPin!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task SuccessfulPinReset_AfterExplicitRejection_AllowsTheReplacementOnTheNextInsertionCycle()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.PinRejected),
            ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        using (var replacement = ClientCertificatePin.CopyOf(RandomPinCharacters().AsSpan()))
        {
            await pins.SaveAsync(Authentication.CredentialTargetName, replacement);
        }
        await stamps.SaveAsync(CreateValidStamp());

        await InvokeAsync(coordinator, "ReportPinSetupSucceededAsync");
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("AwaitingCertificate", GetStage(coordinator));
        Assert.Equal(2, unlocker.AttemptCount);
        Assert.Equal(2, pins.ReadCount);
    }

    [Fact]
    public async Task TransientFailure_EndsOnlyCurrentCycleAndAllowsAReinsertedCycle()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.DeviceUnavailable, 41),
            ClientCertificateTokenUnlockResult.Succeeded(session));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("Failed", GetStage(coordinator));
        Assert.Equal("DeviceUnavailable", GetErrorCategory(coordinator));
        Assert.Equal(1, unlocker.AttemptCount);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("AwaitingCertificate", GetStage(coordinator));
        Assert.Equal(2, unlocker.AttemptCount);
        Assert.Equal(2L, GetCycleId(coordinator));
    }

    [Fact]
    public async Task DeviceRemovalAndTlsCompletion_DisposeTheUnlockLeaseExactlyOnce()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Succeeded(firstSession),
            ClientCertificateTokenUnlockResult.Succeeded(secondSession));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "ReportNavigationStartedAsync", 1L);
        await InvokeAsync(coordinator, "HandleCertificateRequestAsync", CreateAuthorizedRequest());
        var activeAttempt = GetAttempt(coordinator);
        await InvokeAsync(coordinator, "ReportTlsCompletedAsync", activeAttempt);
        await InvokeAsync(coordinator, "ReportTlsCompletedAsync", activeAttempt);

        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(1, secondSession.DisposeCount);
        Assert.Equal("Ready", GetStage(coordinator));
    }

    [Fact]
    public async Task TimeoutDuringAuthentication_DisposesTheUnlockLeaseAndEndsTheCycle()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "ReportNavigationStartedAsync", 1L);
        await InvokeAsync(coordinator, "HandleCertificateRequestAsync", CreateAuthorizedRequest());
        var activeAttempt = GetAttempt(coordinator);
        await InvokeAsync(coordinator, "ReportTimeoutAsync", activeAttempt);
        await InvokeAsync(coordinator, "ReportTimeoutAsync", activeAttempt);

        Assert.Equal(1, session.DisposeCount);
        Assert.Equal("Failed", GetStage(coordinator));
        Assert.Equal("TimedOut", GetErrorCategory(coordinator));
    }

    [Fact]
    public async Task SuccessfulLandingNavigationWithoutCertificate_KeepsSessionForLaterWebNavigation()
    {
        var session = new RecordingUnlockSession();
        await using var coordinator = CreateCoordinator(
            new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(CreateValidStamp()),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)));

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(101);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(101, isSuccess: true);

        Assert.Equal(AuthenticationStage.AwaitingCertificate, coordinator.CurrentState.Stage);
        Assert.Equal(0, session.DisposeCount);

        await coordinator.ReportNavigationStartedAsync(202);
        var result = await coordinator.HandleAsync(CreateAuthorizedRequest(202));

        Assert.Equal(ClientCertificateRequestMatchStatus.Selected, result.Status);
        Assert.NotNull(result.Attempt);

        await coordinator.ReportTlsCompletedAsync(result.Attempt!);

        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData(ClientCertificateRequestMatchStatus.CertificateMissing)]
    [InlineData(ClientCertificateRequestMatchStatus.CertificateConflict)]
    public async Task AuthorizedUnmatchedCertificateRequest_CreatesAttemptForManualBrowserSelection(
        ClientCertificateRequestMatchStatus expectedStatus)
    {
        var session = new RecordingUnlockSession();
        await using var coordinator = CreateCoordinator(
            new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(CreateValidStamp()),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)));

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(202);

        var certificates = expectedStatus == ClientCertificateRequestMatchStatus.CertificateConflict
            ? new[]
            {
                new ClientCertificateCandidate(
                    "1111111111111111111111111111111111111111",
                    "Test Subject",
                    "Test Issuer"),
                new ClientCertificateCandidate(
                    "1111111111111111111111111111111111111111",
                    "Test Subject",
                    "Test Issuer")
            }
            : new[]
            {
                new ClientCertificateCandidate(
                    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    "Other Subject",
                    "Other Issuer")
            };
        var result = await coordinator.HandleAsync(new ClientCertificateRequestSnapshot(
            new Uri("https://bank.example.test/"),
            false,
            certificates,
            202));

        Assert.Equal(expectedStatus, result.Status);
        Assert.NotNull(result.Attempt);
        Assert.Equal(AuthenticationStage.Authenticating, coordinator.CurrentState.Stage);

        await coordinator.ReportCertificateSelectedAsync(result.Attempt!);
        await coordinator.ReportTlsCompletedAsync(result.Attempt!);

        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task StaleSuccessfulNavigationTerminal_DoesNotClearNewNavigationBinding()
    {
        var session = new RecordingUnlockSession();
        await using var coordinator = CreateCoordinator(
            new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(CreateValidStamp()),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)));

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(301);
        await coordinator.ReportNavigationStartedAsync(302);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(301, isSuccess: true);

        var result = await coordinator.HandleAsync(CreateAuthorizedRequest(302));

        Assert.Equal(ClientCertificateRequestMatchStatus.Selected, result.Status);
        Assert.Equal(AuthenticationStage.Authenticating, coordinator.CurrentState.Stage);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task FailedNavigationWithoutCertificate_EndsCurrentCycle()
    {
        var session = new RecordingUnlockSession();
        await using var coordinator = CreateCoordinator(
            new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(CreateValidStamp()),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)));

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.ReportNavigationStartedAsync(401);
        await coordinator.ReportNavigationCompletedWithoutCertificateAsync(401, isSuccess: false);

        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.TlsFailed, coordinator.CurrentState.ErrorCategory);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task CoordinatorDisposal_DuringAuthenticationDisposesTheUnlockLeaseOnce()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session));
        var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task CertificateSelection_ArmsContinuationOnceBeforeAuthorizationWithoutWaitingForCompletion()
    {
        var secret = $"test-{Guid.NewGuid():N}";
        var pins = new RecordingPinStore(secret.ToCharArray());
        var session = new RecordingUnlockSession();
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateCoordinator(
            pins,
            new RecordingStampStore(CreateValidStamp(AuthenticationWithContinuation())),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)),
            continuation,
            AuthenticationWithContinuation());

        var attempt = await StartAuthenticatingAsync(coordinator, 41);
        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(authorization);
        Assert.Equal(1, continuation.ArmCount);
        Assert.True(continuation.SawNonEmptyPinDuringArm);
        Assert.NotNull(continuation.PinPassedToArm);
        Assert.All(continuation.PinPassedToArm!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
        Assert.Equal(0, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task RepeatedCertificateAcknowledgement_ReusesAuthorizationWithoutArmingTwice()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 42);

        var first = await coordinator.ReportCertificateSelectedAsync(attempt);
        var duplicate = await coordinator.ReportCertificateSelectedAsync(attempt with { });

        Assert.Same(first, duplicate);
        Assert.Equal(1, continuation.ArmCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartThenSaveSamePin_PreservesExactUnlockAndContinuationCharactersDespiteDisplayReads(
        bool validatedBeforeRestart)
    {
        var expected = RandomPinCharacters();
        var authentication = AuthenticationWithContinuation();
        var pins = new RecordingPinStore(expected);
        var stamps = new RecordingStampStore(validatedBeforeRestart
            ? CreateValidStamp(authentication)
            : ClientCertificateValidationStamp.CreatePending(authentication.Id, authentication.GetNormalizedHash()));
        var unlockMatches = new List<bool>();
        var continuationMatches = new List<bool>();
        var displayMatches = new List<bool>();

        async ValueTask ReadAndDisposeDisplayPinAsync()
        {
            using var displayed = await pins.ReadAsync(authentication.CredentialTargetName);
            displayMatches.Add(displayed is not null && displayed.AsMemory().Span.SequenceEqual(expected));
        }

        RecordingUnlocker CreateInspectingUnlocker(int attempts) => new(
            Enumerable.Range(0, attempts).Select(_ =>
                ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession())).ToArray())
        {
            InspectPin = async pin =>
            {
                await ReadAndDisposeDisplayPinAsync();
                unlockMatches.Add(pin.AsMemory().Span.SequenceEqual(expected));
            }
        };

        RecordingNativePromptContinuation CreateInspectingContinuation() => new(
            new(ClientCertificateNativePromptArmStatus.Armed, new RecordingNativePromptAttempt()))
        {
            InspectPin = async pin =>
            {
                await ReadAndDisposeDisplayPinAsync();
                continuationMatches.Add(pin.AsMemory().Span.SequenceEqual(expected));
            }
        };

        // Finish one runtime without deleting either persisted store, then construct a new runtime.
        await using (var previous = CreateCoordinator(
            pins, stamps, CreateInspectingUnlocker(1), CreateInspectingContinuation(), authentication))
        {
            await ReadAndDisposeDisplayPinAsync();
            var attempt = await StartAuthenticatingAsync(previous, 71);
            await ReadAndDisposeDisplayPinAsync();
            Assert.NotNull(await previous.ReportCertificateSelectedAsync(attempt));
        }

        var restartedUnlocker = CreateInspectingUnlocker(2);
        var restartedContinuation = CreateInspectingContinuation();
        await using var restarted = CreateCoordinator(
            pins, stamps, restartedUnlocker, restartedContinuation, authentication);
        await ReadAndDisposeDisplayPinAsync();
        var restartedAttempt = await StartAuthenticatingAsync(restarted, 72);
        await ReadAndDisposeDisplayPinAsync();
        Assert.NotNull(await restarted.ReportCertificateSelectedAsync(restartedAttempt));

        // Re-saving invokes ReportPinProvisionedAsync while the device is already present.
        var setup = new ClientCertificatePinSetupService(
            authentication, pins, stamps, restartedUnlocker, new UnusedSetupVerifier(), restarted);
        Assert.True(await setup.SaveFixedAsync(ClientCertificatePin.CopyOf(expected.AsMemory())));
        Assert.True(stamps.StoredStamp!.IsPendingFor(authentication.Id, authentication.GetNormalizedHash()));
        var savedAttempt = await StartAuthenticatingAsync(restarted, 73);
        await ReadAndDisposeDisplayPinAsync();
        Assert.NotNull(await restarted.ReportCertificateSelectedAsync(savedAttempt));

        Assert.Equal(3, unlockMatches.Count);
        Assert.Equal(3, continuationMatches.Count);
        Assert.All(unlockMatches, Assert.True);
        Assert.All(continuationMatches, Assert.True);
        Assert.All(displayMatches, Assert.True);
        Array.Clear(expected);
    }

    [Fact]
    public async Task CertificateSelection_WhenContinuationArmFails_ReturnsNoAuthorizationAndFailsClosed()
    {
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 43);

        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);

        Assert.Null(authorization);
        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.UnknownFailure, coordinator.CurrentState.ErrorCategory);
        Assert.NotNull(continuation.PinPassedToArm);
        Assert.All(continuation.PinPassedToArm!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task CertificateSelection_WhenConfiguredContinuationIsMissing_ReturnsNoAuthorizationAndFailsClosed()
    {
        var authentication = AuthenticationWithContinuation();
        var pins = new RecordingPinStore($"test-{Guid.NewGuid():N}".ToCharArray());
        var session = new RecordingUnlockSession();
        await using var coordinator = CreateCoordinator(
            pins,
            new RecordingStampStore(CreateValidStamp(authentication)),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)),
            authentication: authentication);
        var attempt = await StartAuthenticatingAsync(coordinator, 53);

        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);

        Assert.Null(authorization);
        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.UnknownFailure, coordinator.CurrentState.ErrorCategory);
        Assert.Equal(1, session.DisposeCount);
        Assert.NotNull(pins.LastReadPin);
        Assert.All(pins.LastReadPin!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task CertificateSelection_WhenCallerIsAlreadyCancelled_ClearsTheActiveCycle()
    {
        using var cancellation = new CancellationTokenSource();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, new RecordingNativePromptAttempt()));
        var session = new RecordingUnlockSession();
        var authentication = AuthenticationWithContinuation();
        await using var coordinator = CreateCoordinator(
            new RecordingPinStore($"test-{Guid.NewGuid():N}".ToCharArray()),
            new RecordingStampStore(CreateValidStamp(authentication)),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session)),
            continuation,
            authentication);
        var attempt = await StartAuthenticatingAsync(coordinator, 51);
        cancellation.Cancel();

        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt, cancellation.Token);

        Assert.Null(authorization);
        Assert.Equal(AuthenticationStage.Failed, coordinator.CurrentState.Stage);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, continuation.ArmCount);
    }

    [Fact]
    public async Task DisabledContinuation_PreservesSelectionBehaviorWithoutArmingOrRetainingPin()
    {
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, new RecordingNativePromptAttempt()));
        var authentication = AuthenticationWithContinuation(enabled: false);
        var pins = new RecordingPinStore($"test-{Guid.NewGuid():N}".ToCharArray());
        await using var coordinator = CreateCoordinator(
            pins,
            new RecordingStampStore(CreateValidStamp(authentication)),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession())),
            continuation,
            authentication);
        var attempt = await StartAuthenticatingAsync(coordinator, 44);

        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);

        Assert.NotNull(authorization);
        Assert.Equal(0, continuation.ArmCount);
        Assert.Null(continuation.PinPassedToArm);
        Assert.All(pins.LastReadPin!.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData(ClientCertificateNativePromptStatus.TargetMismatch, AuthenticationErrorCategory.UnknownFailure)]
    [InlineData(ClientCertificateNativePromptStatus.TimedOut, AuthenticationErrorCategory.TimedOut)]
    [InlineData(ClientCertificateNativePromptStatus.Cancelled, AuthenticationErrorCategory.UnknownFailure)]
    public async Task ContinuationFailureBeforeTls_RevokesAuthorizationAndEndsTheCycle(
        ClientCertificateNativePromptStatus status,
        AuthenticationErrorCategory expectedCategory)
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 45);
        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);

        promptAttempt.Complete(new(status));
        await WaitUntilAsync(() => coordinator.CurrentState.Stage == AuthenticationStage.Failed);

        Assert.Equal(expectedCategory, coordinator.CurrentState.ErrorCategory);
        Assert.False(authorization!.TryCommit(() => { }));
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task TlsSuccessFirst_DisposesPromptAttemptAndIgnoresLaterCompletion()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 46);
        var authorization = await coordinator.ReportCertificateSelectedAsync(attempt);

        await coordinator.ReportTlsCompletedAsync(attempt);
        promptAttempt.Complete(new(ClientCertificateNativePromptStatus.TargetMismatch));
        await Task.Yield();

        Assert.Equal(AuthenticationStage.Ready, coordinator.CurrentState.Stage);
        Assert.False(authorization!.TryCommit(() => { }));
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task TlsFailureAfterArm_DisposesPromptAttemptExactlyOnce()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 47);
        _ = await coordinator.ReportCertificateSelectedAsync(attempt);

        await coordinator.ReportTlsFailureAsync(attempt, 71);
        await coordinator.ReportTlsFailureAsync(attempt, 71);

        Assert.Equal(1, promptAttempt.DisposeCount);
        Assert.Equal(AuthenticationErrorCategory.TlsFailed, coordinator.CurrentState.ErrorCategory);
    }

    [Fact]
    public async Task TimeoutAfterArm_DisposesPromptAttemptExactlyOnce()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 48);
        _ = await coordinator.ReportCertificateSelectedAsync(attempt);

        await coordinator.ReportTimeoutAsync(attempt);
        await coordinator.ReportTimeoutAsync(attempt);

        Assert.Equal(1, promptAttempt.DisposeCount);
        Assert.Equal(AuthenticationErrorCategory.TimedOut, coordinator.CurrentState.ErrorCategory);
    }

    [Fact]
    public async Task DeviceRemovalAfterArm_DisposesPromptAttemptAndIgnoresStaleCompletion()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        await using var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 49);
        _ = await coordinator.ReportCertificateSelectedAsync(attempt);

        await coordinator.HandleDevicePresenceAsync(false);
        promptAttempt.Complete(new(ClientCertificateNativePromptStatus.TargetMismatch));
        await Task.Yield();

        Assert.Equal(1, promptAttempt.DisposeCount);
        Assert.Equal(AuthenticationStage.WaitingForDevice, coordinator.CurrentState.Stage);
    }

    [Fact]
    public async Task CoordinatorDisposalAfterArm_DisposesPromptAttemptExactlyOnce()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new RecordingNativePromptContinuation(
            new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt));
        var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 50);
        _ = await coordinator.ReportCertificateSelectedAsync(attempt);

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task CoordinatorDisposalDuringArm_ReturnsNoAuthorizationAndDisposesReturnedAttempt()
    {
        var promptAttempt = new RecordingNativePromptAttempt();
        var continuation = new BlockingNativePromptContinuation(promptAttempt);
        var coordinator = CreateContinuationCoordinator(continuation);
        var attempt = await StartAuthenticatingAsync(coordinator, 52);
        var selection = coordinator.ReportCertificateSelectedAsync(attempt);
        await continuation.ArmStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposal = coordinator.DisposeAsync().AsTask();
        continuation.AllowArm.TrySetResult();
        var authorization = await selection;
        await disposal;

        Assert.Null(authorization);
        Assert.Equal(1, promptAttempt.DisposeCount);
    }

    [Fact]
    public async Task StaleTlsSuccessAfterReinsert_DoesNotTransitionOrReleaseTheCurrentAttempt()
    {
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        var coordinator = CreateAuthenticatedCoordinator(firstSession, secondSession);
        await using (coordinator)
        {
            var staleAttempt = await StartAuthenticatingAsync(coordinator, 1);
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
            var activeAttempt = await StartAuthenticatingAsync(coordinator, 2);

            await InvokeAsync(coordinator, "ReportTlsCompletedAsync", staleAttempt);

            Assert.NotEqual(activeAttempt, staleAttempt);
            Assert.Equal("Authenticating", GetStage(coordinator));
            Assert.Equal(0, secondSession.DisposeCount);
        }
    }

    [Fact]
    public async Task StaleTlsFailureAfterReinsert_DoesNotTransitionOrReleaseTheCurrentAttempt()
    {
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        var coordinator = CreateAuthenticatedCoordinator(firstSession, secondSession);
        await using (coordinator)
        {
            var staleAttempt = await StartAuthenticatingAsync(coordinator, 1);
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
            _ = await StartAuthenticatingAsync(coordinator, 2);

            await InvokeAsync(coordinator, "ReportTlsFailureAsync", staleAttempt, 23);

            Assert.Equal("Authenticating", GetStage(coordinator));
            Assert.Equal(0, secondSession.DisposeCount);
        }
    }

    [Fact]
    public async Task StaleTimeoutAfterReinsert_DoesNotTransitionOrReleaseTheCurrentAttempt()
    {
        var firstSession = new RecordingUnlockSession();
        var secondSession = new RecordingUnlockSession();
        var coordinator = CreateAuthenticatedCoordinator(firstSession, secondSession);
        await using (coordinator)
        {
            var staleAttempt = await StartAuthenticatingAsync(coordinator, 1);
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
            _ = await StartAuthenticatingAsync(coordinator, 2);

            await InvokeAsync(coordinator, "ReportTimeoutAsync", staleAttempt);

            Assert.Equal("Authenticating", GetStage(coordinator));
            Assert.Equal(0, secondSession.DisposeCount);
        }
    }

    [Fact]
    public async Task PinRejection_AfterCallerCancellationInvalidatesBothStoresWithoutReusingThePin()
    {
        var cancellation = new CancellationTokenSource();
        var pins = new RecordingPinStore(RandomPinCharacters()) { RejectCanceledDeletion = true };
        var stamps = new RecordingStampStore(CreateValidStamp()) { RejectCanceledDeletion = true };
        var unlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.PinRejected));
        unlocker.AfterUnlock = cancellation.Cancel;
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true, cancellation.Token);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.False(pins.HasStoredPin);
        Assert.Null(stamps.StoredStamp);
        Assert.Equal(1, pins.DeleteCount);
        Assert.Equal(1, stamps.DeleteCount);
        Assert.Equal(1, unlocker.AttemptCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PinRejection_WhenCredentialOrStampDeletionFails_RemainsFailClosedForThisProcessAndRestart(
        bool pinDeletionFails,
        bool stampDeletionFails)
    {
        var pins = new RecordingPinStore(RandomPinCharacters()) { DeleteException = pinDeletionFails ? new InvalidOperationException() : null };
        var stamps = new RecordingStampStore(CreateValidStamp()) { DeleteException = stampDeletionFails ? new InvalidOperationException() : null };
        var rejectionUnlocker = new RecordingUnlocker(
            ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.PinRejected));
        await using (var coordinator = CreateCoordinator(pins, stamps, rejectionUnlocker))
        {
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
            await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

            Assert.Equal("RequiresPinSetup", GetStage(coordinator));
            Assert.Equal(1, rejectionUnlocker.AttemptCount);
            Assert.Equal(1, pins.ReadCount);
        }

        var restartUnlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var restarted = CreateCoordinator(pins, stamps, restartUnlocker);
        await InvokeAsync(restarted, "HandleDevicePresenceAsync", true);

        Assert.Equal("RequiresPinSetup", GetStage(restarted));
        Assert.Equal(0, restartUnlocker.AttemptCount);
        Assert.Equal(1, pins.DeleteCount);
        Assert.Equal(1, stamps.DeleteCount);
    }

    [Fact]
    public async Task PinRejection_WhenBothDeletionAttemptsFail_BlocksCredentialReadAndUnlockAfterReinsertInThisProcess()
    {
        var pins = new RecordingPinStore(RandomPinCharacters()) { DeleteException = new InvalidOperationException() };
        var stamps = new RecordingStampStore(CreateValidStamp()) { DeleteException = new InvalidOperationException() };
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Failed(
            ClientCertificateTokenUnlockStatus.PinRejected));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", false);
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("RequiresPinSetup", GetStage(coordinator));
        Assert.Equal(1, pins.ReadCount);
        Assert.Equal(1, unlocker.AttemptCount);
        Assert.Equal(1, pins.DeleteCount);
        Assert.Equal(1, stamps.DeleteCount);
    }

    [Fact]
    public async Task BeginCompatibilityValidation_ExposesTheFutureSetupTransition()
    {
        var pins = new RecordingPinStore();
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker();
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "BeginCompatibilityValidationAsync");

        Assert.Equal("ValidatingCompatibility", GetStage(coordinator));
    }

    [Theory]
    [InlineData(ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(ClientCertificateTokenUnlockStatus.Unsupported)]
    public async Task NonReusableTokenProviderOutcome_TransitionsToIncompatible(ClientCertificateTokenUnlockStatus status)
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Failed(status));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("Incompatible", GetStage(coordinator));
        Assert.Equal(status.ToString(), GetErrorCategory(coordinator));
    }

    [Fact]
    public async Task ReentrantStateSubscriber_CompletesWithoutHoldingTheTransitionGate()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession()));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);
        Task? reentrantCall = null;
        var completedBeforeSubscriberReturned = false;

        Subscribe(coordinator, (_, eventArgs) =>
        {
            if (GetStage(eventArgs.State) != "AwaitingCertificate")
            {
                return;
            }

            reentrantCall = Task.Run(async () =>
            {
                await InvokeAsync(coordinator, "ReportNavigationStartedAsync", 1L);
                await InvokeAsync(coordinator, "HandleCertificateRequestAsync", CreateAuthorizedRequest());
            });
            completedBeforeSubscriberReturned = SpinWait.SpinUntil(
                () => reentrantCall.IsCompleted,
                TimeSpan.FromSeconds(1));
        });

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await reentrantCall!.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(completedBeforeSubscriberReturned);
        Assert.Equal("Authenticating", GetStage(coordinator));
    }

    [Fact]
    public async Task ThrowingStateSubscriber_DoesNotInterruptLeaseOrStateMutation()
    {
        var pins = new RecordingPinStore(RandomPinCharacters());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var session = new RecordingUnlockSession();
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(session));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);
        Subscribe(coordinator, (_, _) => throw new InvalidOperationException("subscriber failure"));

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        Assert.Equal("AwaitingCertificate", GetStage(coordinator));
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task VisibleFailureState_RedactsSecretAndOnlyExposesCategoryAndPublicCode()
    {
        var secretMarker = $"pin-marker-{Guid.NewGuid():N}";
        var pins = new RecordingPinStore(secretMarker.ToCharArray());
        var stamps = new RecordingStampStore(CreateValidStamp());
        var unlocker = new RecordingUnlocker(ClientCertificateTokenUnlockResult.Failed(
            ClientCertificateTokenUnlockStatus.UnknownFailure,
            404));
        await using var coordinator = CreateCoordinator(pins, stamps, unlocker);

        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);

        var state = GetPropertyValue(coordinator, "CurrentState")!;
        var visibleText = string.Join(
            " ",
            state.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.GetValue(state) as string));

        Assert.Equal("UnknownFailure", GetErrorCategory(coordinator));
        Assert.Equal(404, GetPublicErrorCode(coordinator));
        Assert.DoesNotContain(secretMarker, visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientCertificatePin", visibleText, StringComparison.Ordinal);
    }

    private static readonly ClientCertificateAuthenticationOptions Authentication = new(
        "authentication-id",
        "bank.example.test",
        443,
        new("1111111111111111111111111111111111111111", "Test Subject", "Test Issuer"),
        new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"),
        "test-credential-target");

    private static AuthenticationCoordinator CreateCoordinator(
        RecordingPinStore pins,
        RecordingStampStore stamps,
        RecordingUnlocker unlocker,
        IClientCertificateNativePromptContinuation? continuation = null,
        ClientCertificateAuthenticationOptions? authentication = null) =>
        new(authentication ?? Authentication, pins, stamps, unlocker, continuation);

    private static ClientCertificateValidationStamp CreateValidStamp(
        ClientCertificateAuthenticationOptions? authentication = null)
    {
        authentication ??= Authentication;
        return new(authentication.Id, authentication.GetNormalizedHash());
    }

    private static ClientCertificateAuthenticationOptions AuthenticationWithContinuation(bool enabled = true) =>
        Authentication with
        {
            NativePromptContinuation = new(
                enabled,
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

    private static AuthenticationCoordinator CreateContinuationCoordinator(
        IClientCertificateNativePromptContinuation continuation)
    {
        var authentication = AuthenticationWithContinuation();
        return CreateCoordinator(
            new RecordingPinStore($"test-{Guid.NewGuid():N}".ToCharArray()),
            new RecordingStampStore(CreateValidStamp(authentication)),
            new RecordingUnlocker(ClientCertificateTokenUnlockResult.Succeeded(new RecordingUnlockSession())),
            continuation,
            authentication);
    }

    private static ClientCertificateRequestSnapshot CreateAuthorizedRequest(
        long browserOperationId = 1) => new(
        new Uri("https://bank.example.test/"),
        false,
        [new("1111111111111111111111111111111111111111", "Test Subject", "Test Issuer")],
        browserOperationId);

    private static AuthenticationCoordinator CreateAuthenticatedCoordinator(
        RecordingUnlockSession firstSession,
        RecordingUnlockSession secondSession) =>
        CreateCoordinator(
            new RecordingPinStore(RandomPinCharacters()),
            new RecordingStampStore(CreateValidStamp()),
            new RecordingUnlocker(
                ClientCertificateTokenUnlockResult.Succeeded(firstSession),
                ClientCertificateTokenUnlockResult.Succeeded(secondSession)));

    private static async Task<AuthenticationAttempt> StartAuthenticatingAsync(
        AuthenticationCoordinator coordinator,
        long browserOperationId)
    {
        await InvokeAsync(coordinator, "HandleDevicePresenceAsync", true);
        await InvokeAsync(coordinator, "ReportNavigationStartedAsync", browserOperationId);
        await InvokeAsync(coordinator, "HandleCertificateRequestAsync", new ClientCertificateRequestSnapshot(
            new Uri("https://bank.example.test/"),
            false,
            [new("1111111111111111111111111111111111111111", "Test Subject", "Test Issuer")],
            browserOperationId));
        return GetAttempt(coordinator);
    }

    private static void Subscribe(IAsyncDisposable coordinator, EventHandler<AuthenticationStateChangedEventArgs> handler) =>
        coordinator.GetType().GetEvent("StateChanged")!.AddEventHandler(coordinator, handler);

    private static async Task InvokeAsync(object instance, string methodName, params object[] arguments)
    {
        var method = instance.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length >= arguments.Length &&
                candidate.GetParameters().Skip(arguments.Length).All(parameter => parameter.IsOptional));
        Assert.NotNull(method);
        var invocationArguments = arguments
            .Concat(Enumerable.Repeat((object)Type.Missing, method!.GetParameters().Length - arguments.Length))
            .ToArray();
        var operation = method.Invoke(instance, invocationArguments);
        if (operation is Task task)
        {
            await task;
            return;
        }

        Assert.NotNull(operation);
        var asTask = operation!.GetType().GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(asTask);
        await Assert.IsAssignableFrom<Task>(asTask!.Invoke(operation, null));
    }

    private static object? GetPropertyValue(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);

    private static string GetStage(object coordinator) =>
        GetPropertyValue(GetPropertyValue(coordinator, "CurrentState")!, "Stage")!.ToString()!;

    private static string GetErrorCategory(object coordinator) =>
        GetPropertyValue(GetPropertyValue(coordinator, "CurrentState")!, "ErrorCategory")!.ToString()!;

    private static int? GetPublicErrorCode(object coordinator) =>
        (int?)GetPropertyValue(GetPropertyValue(coordinator, "CurrentState")!, "PublicErrorCode");

    private static long? GetCycleId(object coordinator) =>
        (long?)GetPropertyValue(GetPropertyValue(coordinator, "CurrentState")!, "InsertionCycleId");

    private static AuthenticationAttempt GetAttempt(object coordinator) =>
        Assert.IsType<AuthenticationAttempt>(
            GetPropertyValue(GetPropertyValue(coordinator, "CurrentState")!, "Attempt"));

    private static string GetStage(AuthenticationState state) => state.Stage.ToString();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static char[] RandomPinCharacters() =>
        $"test-{Guid.NewGuid():N}".ToCharArray();

    private sealed class RecordingPinStore(params char[] storedPin) : IClientCertificatePinStore
    {
        private char[]? _storedPin = storedPin.Length == 0 ? null : storedPin;

        public int ReadCount { get; private set; }

        public ClientCertificatePin? LastReadPin { get; private set; }

        public bool HasStoredPin => _storedPin is not null;

        public int DeleteCount { get; private set; }

        public Exception? DeleteException { get; init; }

        public bool RejectCanceledDeletion { get; init; }

        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastReadPin = _storedPin is null ? null : ClientCertificatePin.CopyOf(_storedPin.AsMemory());
            return ValueTask.FromResult(LastReadPin);
        }

        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _storedPin = pin.AsMemory().ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (RejectCanceledDeletion && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            _storedPin = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStampStore(ClientCertificateValidationStamp? storedStamp) : IClientCertificateValidationStampStore
    {
        public int ReadCount { get; private set; }
        public ClientCertificateValidationStamp? StoredStamp { get; private set; } = storedStamp;

        public int SaveCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Exception? DeleteException { get; init; }

        public bool RejectCanceledDeletion { get; init; }

        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult(StoredStamp);
        }

        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            StoredStamp = stamp;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (RejectCanceledDeletion && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            StoredStamp = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingUnlocker(params ClientCertificateTokenUnlockResult[] results) : IClientCertificateTokenUnlocker
    {
        private readonly Queue<ClientCertificateTokenUnlockResult> _results = new(results);

        public int AttemptCount { get; private set; }

        public Action? AfterUnlock { get; set; }

        public Func<ClientCertificatePin, ValueTask>? InspectPin { get; init; }

        public async ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            if (InspectPin is not null)
            {
                await InspectPin(pin);
            }
            var result = _results.Count == 0
                ? ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.UnknownFailure)
                : _results.Dequeue();
            AfterUnlock?.Invoke();
            return result;
        }
    }

    private sealed class RecordingUnlockSession : IClientCertificateTokenUnlockSession
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingNativePromptContinuation(ClientCertificateNativePromptArmResult armResult) :
        IClientCertificateNativePromptContinuation
    {
        public int ArmCount { get; private set; }
        public ClientCertificatePin? PinPassedToArm { get; private set; }
        public bool SawNonEmptyPinDuringArm { get; private set; }
        public Func<ClientCertificatePin, ValueTask>? InspectPin { get; init; }

        public async ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
            ClientCertificateAuthenticationOptions authentication,
            AuthenticationAttempt attempt,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            ArmCount++;
            PinPassedToArm = pin;
            SawNonEmptyPinDuringArm = !pin.AsMemory().IsEmpty;
            if (InspectPin is not null)
            {
                await InspectPin(pin);
            }
            return armResult;
        }
    }

    private sealed class UnusedSetupVerifier : IClientCertificateSetupVerifier
    {
        public ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
            ClientCertificateAuthenticationOptions authentication,
            IClientCertificateTokenUnlockSession session,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Saving a fixed PIN must not run setup verification.");
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
            ArmStarted.TrySetResult();
            await AllowArm.Task.WaitAsync(cancellationToken);
            return new(ClientCertificateNativePromptArmStatus.Armed, promptAttempt);
        }
    }
}
