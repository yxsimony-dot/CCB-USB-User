// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsCryptoApiTokenUnlockerSafetyTests
{
    [Theory]
    [InlineData(CryptoStage.Acquire, 0, 0, 0)]
    [InlineData(CryptoStage.SetPin, 1, 0, 0)]
    [InlineData(CryptoStage.GetKey, 1, 0, 0)]
    [InlineData(CryptoStage.CreateHash, 1, 1, 0)]
    [InlineData(CryptoStage.HashData, 1, 1, 1)]
    [InlineData(CryptoStage.SignQuery, 1, 1, 1)]
    [InlineData(CryptoStage.SignWrite, 1, 1, 1)]
    public async Task UnlockAsync_WhenNativeStageFails_ReleasesEveryAcquiredHandleExactlyOnce(
        CryptoStage stage,
        int expectedProviders,
        int expectedKeys,
        int expectedHashes)
    {
        var native = new ScriptedCryptoApiNative { FailAt = stage };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        AssertReleaseCounts(native, expectedProviders, expectedKeys, expectedHashes);
        AssertKeyBeforeProvider(native);
        Assert.True(native.AllSecretProbeBuffersWereZeroed);
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 1, 0, 0)]
    [InlineData(CryptoStage.SetPin, 1, 0, 0)]
    [InlineData(CryptoStage.GetKey, 1, 1, 0)]
    [InlineData(CryptoStage.CreateHash, 1, 1, 1)]
    [InlineData(CryptoStage.HashData, 1, 1, 1)]
    [InlineData(CryptoStage.SignQuery, 1, 1, 1)]
    [InlineData(CryptoStage.SignWrite, 1, 1, 1)]
    public async Task UnlockAsync_WhenCanceledAtAnyNativeStage_ReleasesEveryAcquiredHandleExactlyOnce(
        CryptoStage stage,
        int expectedProviders,
        int expectedKeys,
        int expectedHashes)
    {
        using var cancellation = new CancellationTokenSource();
        var native = new ScriptedCryptoApiNative { CancelAfter = stage, Cancellation = cancellation };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await unlocker.UnlockAsync(CreateAuthentication(), pin, cancellation.Token));

        AssertReleaseCounts(native, expectedProviders, expectedKeys, expectedHashes);
        AssertKeyBeforeProvider(native);
    }

    [Theory]
    [InlineData(CryptoStage.Acquire)]
    [InlineData(CryptoStage.SetPin)]
    [InlineData(CryptoStage.GetKey)]
    [InlineData(CryptoStage.CreateHash)]
    [InlineData(CryptoStage.HashData)]
    [InlineData(CryptoStage.SignQuery)]
    [InlineData(CryptoStage.SignWrite)]
    public async Task UnlockAsync_WhenNativeThrows_ReturnsSanitizedUnknownFailure(CryptoStage stage)
    {
        var native = new ScriptedCryptoApiNative { ThrowAt = stage };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Null(result.ProviderErrorCode);
        Assert.Null(result.Session);
        Assert.True(native.AllSecretProbeBuffersWereZeroed);
    }

    [Theory]
    [InlineData(CryptoStage.Acquire)]
    [InlineData(CryptoStage.GetKey)]
    [InlineData(CryptoStage.CreateHash)]
    public async Task UnlockAsync_WhenNativeReturnsZeroHandle_FailsWithoutContinuing(CryptoStage stage)
    {
        var native = new ScriptedCryptoApiNative { ZeroHandleAt = stage };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.DoesNotContain(native.Calls, call => call == stage.NextStage());
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 1167)]
    [InlineData(CryptoStage.Acquire, unchecked((int)0x80090016))]
    [InlineData(CryptoStage.GetKey, 1167)]
    [InlineData(CryptoStage.CreateHash, unchecked((int)0x80090016))]
    public async Task UnlockAsync_TrueZeroHandleNeverReadsStaleLastErrorOrContinues(CryptoStage stage, int staleError)
    {
        var native = new ScriptedCryptoApiNative { ZeroHandleAt = stage, LastError = staleError };
        using var pin = CreatePin();

        var result = await new WindowsCryptoApiTokenUnlocker(native).UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(0, native.GetLastErrorCalls);
        Assert.DoesNotContain(native.Calls, call => call == stage.NextStage());
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 1, 0, 0)]
    [InlineData(CryptoStage.GetKey, 1, 1, 0)]
    [InlineData(CryptoStage.CreateHash, 1, 1, 1)]
    public async Task UnlockAsync_WhenNativeAssignsHandleThenThrows_ReleasesAssignedHandles(
        CryptoStage stage, int providers, int keys, int hashes)
    {
        var native = new ScriptedCryptoApiNative { AssignHandleThenThrowAt = stage };
        using var pin = CreatePin();

        var result = await new WindowsCryptoApiTokenUnlocker(native).UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Null(result.ProviderErrorCode);
        AssertReleaseCounts(native, providers, keys, hashes);
        AssertKeyBeforeProvider(native);
        if (stage == CryptoStage.CreateHash)
        {
            Assert.Equal(["DestroyHash", "ReleaseKey", "ReleaseProvider"], native.ReleaseCalls);
        }
    }

    [Fact]
    public async Task UnlockAsync_WhenGetLastErrorThrows_ReturnsUnknownAndReleasesExistingHandles()
    {
        var native = new ScriptedCryptoApiNative { FailAt = CryptoStage.SetPin, ThrowOnGetLastError = true };
        using var pin = CreatePin();

        var result = await new WindowsCryptoApiTokenUnlocker(native).UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Null(result.ProviderErrorCode);
        AssertReleaseCounts(native, 1, 0, 0);
    }

    [Theory]
    [InlineData(0u, 1167)]
    [InlineData(1048577u, unchecked((int)0x80090016))]
    public async Task UnlockAsync_SignQueryTrueWithInvalidLengthDoesNotReadStaleLastError(uint queriedLength, int staleError)
    {
        var native = new ScriptedCryptoApiNative { QueriedSignatureLength = queriedLength, LastError = staleError };
        using var pin = CreatePin();

        var result = await new WindowsCryptoApiTokenUnlocker(native).UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(0, native.GetLastErrorCalls);
        Assert.DoesNotContain("SignWrite", native.Calls);
        AssertReleaseCounts(native, 1, 1, 1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1048577, 0)]
    [InlineData(32, 0)]
    [InlineData(32, 33)]
    public async Task UnlockAsync_InvalidSignatureLengthsReturnUnknownAndClearBuffers(uint queriedLength, uint returnedLength)
    {
        var native = new ScriptedCryptoApiNative
        {
            QueriedSignatureLength = queriedLength,
            ReturnedSignatureLength = returnedLength
        };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.True(native.AllSecretProbeBuffersWereZeroed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlockSession_ConcurrentAndRepeatedDispose_ReleasesKeyBeforeProviderExactlyOnceWhenReleaseFails(bool releaseThrows)
    {
        var native = new ScriptedCryptoApiNative { ThrowOnRelease = releaseThrows, ReturnFalseOnRelease = !releaseThrows };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();
        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);
        Assert.NotNull(result.Session);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(result.Session.Dispose)));
        result.Session.Dispose();

        AssertReleaseCounts(native, 1, 1, 1);
        AssertKeyBeforeProvider(native);
    }

    [Theory]
    [InlineData(CryptoStage.Acquire)]
    [InlineData(CryptoStage.GetKey)]
    public void Preflight_NativeExceptionsAndZeroHandlesAreUnknownWithoutPrivateKeyOperations(CryptoStage stage)
    {
        var throwing = new ScriptedCryptoApiNative { ThrowAt = stage };
        var zero = new ScriptedCryptoApiNative { ZeroHandleAt = stage };

        foreach (var native in new[] { throwing, zero })
        {
            var result = new WindowsCryptoApiTokenPreflight(native).Check(CreateAuthentication());
            Assert.False(result.IsAvailable);
            Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.FailureStatus);
            Assert.Null(result.ProviderErrorCode);
            Assert.DoesNotContain(native.Calls, call => call is "SetPin" or "CreateHash" or "HashData" or "SignQuery" or "SignWrite");
            Assert.Equal(0x00000040u, native.AcquireFlags);
        }
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 1167)]
    [InlineData(CryptoStage.GetKey, unchecked((int)0x80090016))]
    public void Preflight_TrueZeroHandleNeverReadsStaleLastError(CryptoStage stage, int staleError)
    {
        var native = new ScriptedCryptoApiNative { ZeroHandleAt = stage, LastError = staleError };

        var result = new WindowsCryptoApiTokenPreflight(native).Check(CreateAuthentication());

        Assert.False(result.IsAvailable);
        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.FailureStatus);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(0, native.GetLastErrorCalls);
        Assert.DoesNotContain(native.Calls, call => call == stage.NextStage());
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 1, 0)]
    [InlineData(CryptoStage.GetKey, 1, 1)]
    public void Preflight_WhenNativeAssignsHandleThenThrows_ReleasesAssignedHandles(CryptoStage stage, int providers, int keys)
    {
        var native = new ScriptedCryptoApiNative { AssignHandleThenThrowAt = stage };

        var result = new WindowsCryptoApiTokenPreflight(native).Check(CreateAuthentication());

        Assert.False(result.IsAvailable);
        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.FailureStatus);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(providers, native.ReleaseProviderCount);
        Assert.Equal(keys, native.ReleaseKeyCount);
        AssertKeyBeforeProvider(native);
    }

    [Theory]
    [InlineData(CryptoStage.Acquire, 0, 0)]
    [InlineData(CryptoStage.GetKey, 1, 0)]
    public void Preflight_NativeFalseWhenGetLastErrorThrows_ReturnsUnknownAndReleasesExistingHandles(
        CryptoStage stage, int providers, int keys)
    {
        var native = new ScriptedCryptoApiNative { FailAt = stage, ThrowOnGetLastError = true };

        var result = new WindowsCryptoApiTokenPreflight(native).Check(CreateAuthentication());

        Assert.False(result.IsAvailable);
        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.FailureStatus);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(providers, native.ReleaseProviderCount);
        Assert.Equal(keys, native.ReleaseKeyCount);
    }

    [Theory]
    [InlineData(Terminal.TlsSuccess)]
    [InlineData(Terminal.TlsFailure)]
    [InlineData(Terminal.Timeout)]
    [InlineData(Terminal.Removal)]
    public async Task AuthenticationCoordinator_ReleasesRealCryptoLeaseAtEveryTerminalState(Terminal terminal)
    {
        var native = new ScriptedCryptoApiNative();
        var authentication = CreateAuthentication();
        using var storedPin = CreatePin();
        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            new PinStore(storedPin),
            new StampStore(authentication.GetNormalizedHash()),
            new WindowsCryptoApiTokenUnlocker(native));

        await coordinator.HandleDevicePresenceAsync(true);
        if (terminal == Terminal.Removal)
        {
            await coordinator.HandleDevicePresenceAsync(false);
        }
        else
        {
            await coordinator.ReportNavigationStartedAsync(1);
            await coordinator.HandleCertificateRequestAsync(new(
                new Uri("https://b2b.ccb.com:443"), false,
                [new(authentication.Certificate.Sha1Fingerprint, authentication.Certificate.Subject, authentication.Certificate.Issuer)],
                1));
            var attempt = Assert.IsType<AuthenticationAttempt>(coordinator.CurrentState.Attempt);
            if (terminal == Terminal.TlsSuccess) await coordinator.ReportTlsCompletedAsync(attempt);
            if (terminal == Terminal.TlsFailure) await coordinator.ReportTlsFailureAsync(attempt);
            if (terminal == Terminal.Timeout) await coordinator.ReportTimeoutAsync(attempt);
        }

        AssertReleaseCounts(native, 1, 1, 1);
        AssertKeyBeforeProvider(native);
        Assert.Equal(1, native.SetPinCalls);
    }

    private static void AssertReleaseCounts(ScriptedCryptoApiNative native, int provider, int key, int hash) =>
        Assert.Equal((provider, key, hash), (native.ReleaseProviderCount, native.ReleaseKeyCount, native.ReleaseHashCount));

    private static void AssertKeyBeforeProvider(ScriptedCryptoApiNative native)
    {
        var keyIndex = native.ReleaseCalls.IndexOf("ReleaseKey");
        var providerIndex = native.ReleaseCalls.IndexOf("ReleaseProvider");
        Assert.True(keyIndex < 0 || providerIndex < 0 || keyIndex < providerIndex);
    }

    private static ClientCertificateAuthenticationOptions CreateAuthentication() => new(
        "ccb-usb-key", "b2b.ccb.com", 443,
        new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
        new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-usb-key");

    private static ClientCertificatePin CreatePin()
    {
        var value = RandomNumberGenerator.GetBytes(8).Select(value => (char)('0' + value % 10)).ToArray();
        try { return ClientCertificatePin.CopyOf(value.AsMemory()); }
        finally { Array.Clear(value, 0, value.Length); }
    }

    public enum CryptoStage { Acquire, SetPin, GetKey, CreateHash, HashData, SignQuery, SignWrite }
    public enum Terminal { TlsSuccess, TlsFailure, Timeout, Removal }

    private sealed class PinStore(ClientCertificatePin pin) : IClientCertificatePinStore
    {
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default) => ValueTask.FromResult<ClientCertificatePin?>(ClientCertificatePin.CopyOf(pin.AsMemory()));
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    private sealed class StampStore(string hash) : IClientCertificateValidationStampStore
    {
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string id, CancellationToken cancellationToken = default) => ValueTask.FromResult<ClientCertificateValidationStamp?>(new(id, hash));
        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ScriptedCryptoApiNative : ICryptoApiNative
    {
        public List<string> Calls { get; } = [];
        public List<string> ReleaseCalls { get; } = [];
        public CryptoStage? FailAt { get; init; }
        public CryptoStage? ThrowAt { get; init; }
        public CryptoStage? AssignHandleThenThrowAt { get; init; }
        public CryptoStage? ZeroHandleAt { get; init; }
        public CryptoStage? CancelAfter { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public uint QueriedSignatureLength { get; init; } = 32;
        public uint? ReturnedSignatureLength { get; init; }
        public bool ThrowOnRelease { get; init; }
        public bool ReturnFalseOnRelease { get; init; }
        public bool ThrowOnGetLastError { get; init; }
        public int LastError { get; init; } = 5;
        public uint AcquireFlags { get; private set; }
        public int SetPinCalls { get; private set; }
        public int ReleaseProviderCount { get; private set; }
        public int ReleaseKeyCount { get; private set; }
        public int ReleaseHashCount { get; private set; }
        public int GetLastErrorCalls { get; private set; }
        public bool AllSecretProbeBuffersWereZeroed => (_pin is null || _pin.All(value => value == 0)) && (_challenge is null || _challenge.All(value => value == 0)) && (_signature is null || _signature.All(value => value == 0));
        private byte[]? _pin; private byte[]? _challenge; private byte[]? _signature;
        public bool Acquire(out IntPtr provider, string container, string providerName, uint providerType, uint flags) { Calls.Add("Acquire"); AcquireFlags = flags; provider = ZeroHandleAt == CryptoStage.Acquire ? IntPtr.Zero : (IntPtr)1; return Run(CryptoStage.Acquire); }
        public bool SetProviderParameter(IntPtr provider, uint parameter, byte[] value, uint flags) { Calls.Add("SetPin"); SetPinCalls++; _pin = value; return Run(CryptoStage.SetPin); }
        public bool GetUserKey(IntPtr provider, uint keySpec, out IntPtr key) { Calls.Add("GetKey"); key = ZeroHandleAt == CryptoStage.GetKey ? IntPtr.Zero : (IntPtr)2; return Run(CryptoStage.GetKey); }
        public bool CreateHash(IntPtr provider, uint algorithm, out IntPtr hash) { Calls.Add("CreateHash"); hash = ZeroHandleAt == CryptoStage.CreateHash ? IntPtr.Zero : (IntPtr)3; return Run(CryptoStage.CreateHash); }
        public bool HashData(IntPtr hash, byte[] data, uint flags) { Calls.Add("HashData"); _challenge = data; return Run(CryptoStage.HashData); }
        public bool SignHash(IntPtr hash, uint keySpec, byte[]? signature, ref uint signatureLength, uint flags) { var stage = signature is null ? CryptoStage.SignQuery : CryptoStage.SignWrite; Calls.Add(stage.ToString()); if (signature is null) signatureLength = QueriedSignatureLength; else { _signature = signature; Array.Fill(signature, (byte)1); signatureLength = ReturnedSignatureLength ?? (uint)signature.Length; } return Run(stage); }
        public bool ReleaseProvider(IntPtr provider, uint flags) { ReleaseProviderCount++; ReleaseCalls.Add("ReleaseProvider"); if (ThrowOnRelease) throw new InvalidOperationException(); return !ReturnFalseOnRelease; }
        public bool ReleaseKey(IntPtr key) { ReleaseKeyCount++; ReleaseCalls.Add("ReleaseKey"); if (ThrowOnRelease) throw new InvalidOperationException(); return !ReturnFalseOnRelease; }
        public bool DestroyHash(IntPtr hash) { ReleaseHashCount++; ReleaseCalls.Add("DestroyHash"); if (ThrowOnRelease) throw new InvalidOperationException(); return !ReturnFalseOnRelease; }
        public int GetLastError() { GetLastErrorCalls++; if (ThrowOnGetLastError) throw new InvalidOperationException(); return LastError; }
        private bool Run(CryptoStage stage) { if (CancelAfter == stage) Cancellation!.Cancel(); if (ThrowAt == stage || AssignHandleThenThrowAt == stage) throw new InvalidOperationException(); return FailAt != stage; }
    }
}

internal static class CryptoStageExtensions
{
    public static string NextStage(this WindowsCryptoApiTokenUnlockerSafetyTests.CryptoStage stage) => stage switch
    {
        WindowsCryptoApiTokenUnlockerSafetyTests.CryptoStage.Acquire => "SetPin",
        WindowsCryptoApiTokenUnlockerSafetyTests.CryptoStage.GetKey => "CreateHash",
        WindowsCryptoApiTokenUnlockerSafetyTests.CryptoStage.CreateHash => "HashData",
        _ => string.Empty
    };
}
