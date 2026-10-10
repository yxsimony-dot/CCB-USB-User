using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class ClientCertificatePinSetupServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserNative_SaveStoresSharedPinWithoutKeyValidationOrStamp(bool legacyEntryPoint)
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication() with { AuthenticationMode = "BrowserNative" },
            new RecordingPinStore(calls), new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.ProviderUnavailable)),
            new RecordingVerifier(calls, new(ClientCertificateSetupValidationStatus.UnknownFailure)),
            new RecordingLifecycleObserver(calls));
        var pin = RandomPin();
        var saved = legacyEntryPoint ? (await service.ValidateAndSaveAsync(pin)).IsSuccess : await service.SaveFixedAsync(pin);
        Assert.True(saved);
        Assert.Equal(new[] { "save-pin", "provisioned" }, calls);
        Assert.DoesNotContain("验证成功", service.CurrentState.Message);
        Assert.All(pin.AsMemory().ToArray(), c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task SaveFixedAsync_PersistsCredentialAndClearsStampWithoutUnlockingOrTls()
    {
        var calls = new List<string>();
        var observer = new RecordingLifecycleObserver(calls);
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.UnknownFailure)),
            new RecordingVerifier(calls, new(ClientCertificateSetupValidationStatus.UnknownFailure)),
            observer);
        var pin = RandomPin();

        var saved = await service.SaveFixedAsync(pin);

        Assert.True(saved);
        Assert.Equal(new[] { "delete-stamp", "save-pin", "save-stamp", "provisioned" }, calls);
        Assert.DoesNotContain("unlock", calls);
        Assert.DoesNotContain("verify", calls);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_SavesPinThenStampOnlyAfterTlsValidationSucceeds()
    {
        var calls = new List<string>();
        var pinStore = new RecordingPinStore(calls);
        var stampStore = new RecordingStampStore(calls);
        var session = new RecordingSession(calls);
        var unlocker = new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session));
        var verifier = new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded());
        var service = new ClientCertificatePinSetupService(Authentication(), pinStore, stampStore, unlocker, verifier);
        using var pin = RandomPin();

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "unlock", "verify", "save-pin", "save-stamp", "dispose-session" }, calls);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_PassesLiveUnpersistedPinToVerifier()
    {
        var calls = new List<string>();
        var secret = $"test-{Guid.NewGuid():N}";
        var verifier = new PinInspectingVerifier(calls, secret);
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(
                calls,
                ClientCertificateTokenUnlockResult.Succeeded(new RecordingSession(calls))),
            verifier);
        var pin = ClientCertificatePin.CopyOf(secret.AsSpan());

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.True(result.IsSuccess);
        Assert.True(verifier.SawExpectedPin);
        Assert.False(verifier.PinWasPersistedBeforeVerification);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData(ClientCertificateSetupValidationStatus.CertificateRejected)]
    [InlineData(ClientCertificateSetupValidationStatus.TlsFailed)]
    [InlineData(ClientCertificateSetupValidationStatus.TimedOut)]
    [InlineData(ClientCertificateSetupValidationStatus.Cancelled)]
    public async Task ValidateAndSaveAsync_WhenValidationDoesNotSucceed_DoesNotPersist(
        ClientCertificateSetupValidationStatus status)
    {
        var calls = new List<string>();
        var pinStore = new RecordingPinStore(calls);
        var stampStore = new RecordingStampStore(calls);
        var session = new RecordingSession(calls);
        var unlocker = new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session));
        var verifier = new RecordingVerifier(calls, new(status));
        var service = new ClientCertificatePinSetupService(Authentication(), pinStore, stampStore, unlocker, verifier);
        using var pin = RandomPin();

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("save-pin", calls);
        Assert.DoesNotContain("save-stamp", calls);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenUnlockerRejectsPin_RequiresResetWithoutCallingTlsVerifier()
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.PinRejected,
                87)),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        using var pin = RandomPin();

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.Equal(ClientCertificateSetupValidationStatus.PinRejected, result.Status);
        Assert.Equal(AuthenticationStage.RequiresPinSetup, service.CurrentState.Stage);
        Assert.Equal(AuthenticationErrorCategory.PinRejected, service.CurrentState.ErrorCategory);
        Assert.Equal(87, service.CurrentState.PublicErrorCode);
        Assert.Equal(new[] { "unlock" }, calls);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenTokenUnlockFails_ShowsSafeStageAndNativeErrorCode()
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.UnknownFailure,
                unchecked((int)0x80090020),
                ClientCertificateTokenUnlockFailureStage.SetPin)),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        var secret = RandomSecret();
        using var pin = ClientCertificatePin.CopyOf(secret.AsSpan());

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.Equal(ClientCertificateTokenUnlockFailureStage.SetPin, result.TokenUnlockFailureStage);
        Assert.Equal(unchecked((int)0x80090020), result.PublicErrorCode);
        Assert.Contains("设置 PIN", service.CurrentState.Message);
        Assert.Contains("0x80090020", service.CurrentState.Message);
        Assert.DoesNotContain(secret, service.CurrentState.Message);
        Assert.Equal(new[] { "unlock" }, calls);
    }

    [Theory]
    [InlineData(ClientCertificateSetupValidationStatus.CertificateRejected, AuthenticationErrorCategory.CertificateMissing)]
    [InlineData(ClientCertificateSetupValidationStatus.TlsFailed, AuthenticationErrorCategory.TlsFailed)]
    [InlineData(ClientCertificateSetupValidationStatus.TimedOut, AuthenticationErrorCategory.TimedOut)]
    public async Task ValidateAndSaveAsync_WhenBrowserCompatibilityFails_ReportsIncompatible(
        ClientCertificateSetupValidationStatus status,
        AuthenticationErrorCategory expectedCategory)
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(new RecordingSession(calls))),
            new RecordingVerifier(calls, new(status)));
        using var pin = RandomPin();

        await service.ValidateAndSaveAsync(pin);

        Assert.Equal(AuthenticationStage.Incompatible, service.CurrentState.Stage);
        Assert.Equal(expectedCategory, service.CurrentState.ErrorCategory);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenStampSaveFails_RollsBackPinAndStamp()
    {
        var calls = new List<string>();
        var pinStore = new RecordingPinStore(calls);
        var stampStore = new RecordingStampStore(calls) { ThrowOnSave = true };
        var session = new RecordingSession(calls);
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            pinStore,
            stampStore,
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session)),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        using var pin = RandomPin();

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[]
        {
            "unlock", "verify", "save-pin", "save-stamp", "delete-stamp", "delete-pin", "dispose-session"
        }, calls);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_ClearsPinAndKeepsSessionAliveThroughTlsVerification()
    {
        var calls = new List<string>();
        var session = new RecordingSession(calls);
        var verifier = new InspectingVerifier(calls, session);
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session)),
            verifier);
        var pin = RandomPin();

        await service.ValidateAndSaveAsync(pin);

        Assert.False(verifier.SessionWasDisposedDuringVerification);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_PublishesCompatibilityStagesWithoutSecretText()
    {
        var calls = new List<string>();
        var session = new RecordingSession(calls);
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session)),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        var states = new List<AuthenticationState>();
        service.StateChanged += (_, args) => states.Add(args.State);
        var secret = RandomSecret();
        using var pin = ClientCertificatePin.CopyOf(secret.AsSpan());

        await service.ValidateAndSaveAsync(pin);

        Assert.Equal(new[]
        {
            AuthenticationStage.ValidatingCompatibility,
            AuthenticationStage.UnlockingToken,
            AuthenticationStage.AwaitingCertificate,
            AuthenticationStage.Ready
        }, states.Select(state => state.Stage));
        Assert.All(states, state => Assert.DoesNotContain(secret, state.Message ?? string.Empty));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenSessionDisposeThrows_StillClearsPin()
    {
        var calls = new List<string>();
        var session = new ThrowingSession();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(session)),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        var pin = RandomPin();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAndSaveAsync(pin));

        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenStateSubscriberThrows_DoesNotLeakPinOrInterruptSetup()
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(new RecordingSession(calls))),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        service.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
        var pin = RandomPin();

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.True(result.IsSuccess);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WithEmptyPin_DoesNotCallUnlockerOrPersist()
    {
        var calls = new List<string>();
        var service = new ClientCertificatePinSetupService(
            Authentication(),
            new RecordingPinStore(calls),
            new RecordingStampStore(calls),
            new RecordingUnlocker(calls, ClientCertificateTokenUnlockResult.Succeeded(new RecordingSession(calls))),
            new RecordingVerifier(calls, ClientCertificateSetupValidationResult.Succeeded()));
        var pin = ClientCertificatePin.CopyOf(ReadOnlySpan<char>.Empty);

        var result = await service.ValidateAndSaveAsync(pin);

        Assert.False(result.IsSuccess);
        Assert.Empty(calls);
    }

    private static ClientCertificateAuthenticationOptions Authentication() => new(
        "test-auth",
        "example.test",
        443,
        new(
            "00112233445566778899AABBCCDDEEFF00112233",
            "CN=Test",
            "CN=Test Issuer"),
        new(
            "Test Provider",
            1,
            "Test Container",
            "AT_KEYEXCHANGE"),
        "UsbWebLauncher/test-auth");

    private static string RandomSecret() => $"test-{Guid.NewGuid():N}";

    private static ClientCertificatePin RandomPin() =>
        ClientCertificatePin.CopyOf(RandomSecret().AsSpan());

    private sealed class RecordingPinStore(List<string> calls) : IClientCertificatePinStore
    {
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default) => ValueTask.FromResult<ClientCertificatePin?>(null);
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) { calls.Add("save-pin"); return ValueTask.CompletedTask; }
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) { calls.Add("delete-pin"); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingStampStore(List<string> calls) : IClientCertificateValidationStampStore
    {
        public bool ThrowOnSave { get; init; }
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<ClientCertificateValidationStamp?>(null);
        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default) { calls.Add("save-stamp"); return ThrowOnSave ? ValueTask.FromException(new InvalidOperationException("test failure")) : ValueTask.CompletedTask; }
        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default) { calls.Add("delete-stamp"); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingUnlocker(List<string> calls, ClientCertificateTokenUnlockResult result) : IClientCertificateTokenUnlocker
    {
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(ClientCertificateAuthenticationOptions authentication, ClientCertificatePin pin, CancellationToken cancellationToken = default) { calls.Add("unlock"); return ValueTask.FromResult(result); }
    }

    private sealed class RecordingVerifier(List<string> calls, ClientCertificateSetupValidationResult result) : IClientCertificateSetupVerifier
    {
        public ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(ClientCertificateAuthenticationOptions authentication, IClientCertificateTokenUnlockSession session, ClientCertificatePin pin, CancellationToken cancellationToken = default) { calls.Add("verify"); return ValueTask.FromResult(result); }
    }

    private sealed class InspectingVerifier(List<string> calls, RecordingSession session) : IClientCertificateSetupVerifier
    {
        public bool SessionWasDisposedDuringVerification { get; private set; }

        public ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
            ClientCertificateAuthenticationOptions authentication,
            IClientCertificateTokenUnlockSession unlockSession,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            calls.Add("verify");
            SessionWasDisposedDuringVerification = session.DisposeCount != 0;
            return ValueTask.FromResult(ClientCertificateSetupValidationResult.Succeeded());
        }
    }

    private sealed class PinInspectingVerifier(List<string> calls, string expectedSecret) :
        IClientCertificateSetupVerifier
    {
        public bool SawExpectedPin { get; private set; }
        public bool PinWasPersistedBeforeVerification { get; private set; }

        public ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
            ClientCertificateAuthenticationOptions authentication,
            IClientCertificateTokenUnlockSession session,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            PinWasPersistedBeforeVerification = calls.Contains("save-pin", StringComparer.Ordinal);
            SawExpectedPin = pin.AsMemory().Span.SequenceEqual(expectedSecret.AsSpan());
            calls.Add("verify");
            return ValueTask.FromResult(ClientCertificateSetupValidationResult.Succeeded());
        }
    }

    private sealed class RecordingSession(List<string> calls) : IClientCertificateTokenUnlockSession
    {
        public int DisposeCount { get; private set; }
        public void Dispose() { DisposeCount++; calls.Add("dispose-session"); }
    }

    private sealed class ThrowingSession : IClientCertificateTokenUnlockSession
    {
        public void Dispose() => throw new InvalidOperationException("test dispose failure");
    }

    private sealed class RecordingLifecycleObserver(List<string> calls)
        : IClientCertificatePinSetupLifecycleObserver
    {
        public Task ReportPinProvisionedAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("provisioned");
            return Task.CompletedTask;
        }

        public Task ReportPinSetupSucceededAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReportPinSetupDeletedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
