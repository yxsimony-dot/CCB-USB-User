// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsCryptoApiTokenUnlockerTests
{
    [Fact]
    public async Task UnlockAsync_UsesConfiguredSilentCryptoApiSequenceAndKeepsLeaseAlive()
    {
        var native = new RecordingCryptoApiNative();
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal(
            ["Acquire", "SetPin", "GetKey", "CreateHash", "HashData", "Sign", "Sign"],
            native.Calls);
        Assert.Equal("SAMPLE_KEY_CONTAINER_1_RSA", native.ContainerName);
        Assert.Equal("CCB HD CSP V3.0", native.ProviderName);
        Assert.Equal(1u, native.ProviderType);
        Assert.Equal(0x00000040u, native.AcquireFlags);
        Assert.Equal(0x00000020u, native.SetParameter);
        Assert.Equal(1u, native.KeySpec);
        Assert.Equal(0x00008004u, native.HashAlgorithm);
        Assert.True(native.PinWasAsciiAndNullTerminated);
        Assert.Equal(0, native.ReleaseProviderCount);
        Assert.Equal(0, native.ReleaseKeyCount);

        result.Session!.Dispose();
        result.Session.Dispose();

        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.Equal(1, native.ReleaseKeyCount);
        Assert.Equal(1, native.ReleaseHashCount);
        Assert.True(native.PinBufferWasZeroedAfterCall);
        Assert.True(native.ChallengeAndSignatureBuffersWereZeroed);
    }

    [Fact]
    public async Task UnlockAsync_UsesSignaturePinAndKeyForAtSignatureConfiguration()
    {
        var native = new RecordingCryptoApiNative();
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();
        var authentication = CreateAuthentication() with
        {
            CryptographicProvider = new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                "{00000000-0000-0003}",
                "AT_SIGNATURE")
        };

        var result = await unlocker.UnlockAsync(authentication, pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        Assert.Equal(0x00000021u, native.SetParameter);
        Assert.Equal(2u, native.KeySpec);
        Assert.Equal([2u, 2u], native.SignKeySpecs);
        result.Session!.Dispose();
    }

    [Fact]
    public async Task UnlockAsync_WhenNativePromptContinuationIsEnabled_DefersSignatureProbeAndKeepsLeaseAlive()
    {
        var native = new RecordingCryptoApiNative();
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(
            CreateAuthentication() with
            {
                NativePromptContinuation = CreateNativePromptContinuation()
            },
            pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal(["Acquire", "SetPin", "GetKey"], native.Calls);
        Assert.Equal(0, native.SignCount);
        Assert.Equal(0, native.ReleaseProviderCount);
        Assert.Equal(0, native.ReleaseKeyCount);
        Assert.Equal(0, native.ReleaseHashCount);

        result.Session!.Dispose();
        result.Session.Dispose();

        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.Equal(1, native.ReleaseKeyCount);
        Assert.Equal(0, native.ReleaseHashCount);
        Assert.True(native.PinBufferWasZeroedAfterCall);
    }

    [Fact]
    public void Preflight_AcceptsCurrentlyConnectedTendyronSignatureContainerWithoutSubmittingPin()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);
        var authentication = CreateAuthentication() with
        {
            CryptographicProvider = new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                "{00000000-0000-0001}",
                "AT_SIGNATURE")
        };

        var result = preflight.Check(authentication);

        Assert.True(result.IsAvailable);
        Assert.Equal("{00000000-0000-0001}", native.ContainerName);
        Assert.Equal(2u, native.KeySpec);
        Assert.Equal(0, native.SetPinCount);
        Assert.Equal(0, native.SignCount);
    }

    [Fact]
    public void Preflight_AcceptsCurrentlyConnectedTendyronRsaSignatureContainerWithoutSubmittingPin()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);
        var authentication = CreateAuthentication() with
        {
            CryptographicProvider = new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                @"\\.\Tendyron OnKeyToken 0\{00000000-0000-0002}",
                "AT_SIGNATURE")
        };

        var result = preflight.Check(authentication);

        Assert.True(result.IsAvailable);
        Assert.Equal(
            @"\\.\Tendyron OnKeyToken 0\{00000000-0000-0002}",
            native.ContainerName);
        Assert.Equal(2u, native.KeySpec);
        Assert.Equal(0, native.SetPinCount);
        Assert.Equal(0, native.SignCount);
    }

    [Fact]
    public async Task UnlockAsync_NonAsciiPinReturnsUnsupportedBeforeCallingCsp()
    {
        var native = new RecordingCryptoApiNative();
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = ClientCertificatePin.CopyOf(['\u5bc6', '\u7801']);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Unsupported, result.Status);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task UnlockAsync_UnknownNativeFailureDoesNotGuessPinRejectedAndReleasesProvider()
    {
        var native = new RecordingCryptoApiNative { FailAt = "SetPin", LastError = unchecked((int)0x80090020) };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.NotEqual(ClientCertificateTokenUnlockStatus.PinRejected, result.Status);
        Assert.Equal(unchecked((int)0x80090020), result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.SetPin, result.FailureStage);
        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.True(native.PinBufferWasZeroedAfterCall);
    }

    [Theory]
    [InlineData("Acquire", ClientCertificateTokenUnlockFailureStage.AcquireProvider)]
    [InlineData("SetPin", ClientCertificateTokenUnlockFailureStage.SetPin)]
    [InlineData("GetKey", ClientCertificateTokenUnlockFailureStage.GetUserKey)]
    [InlineData("CreateHash", ClientCertificateTokenUnlockFailureStage.CreateHash)]
    [InlineData("HashData", ClientCertificateTokenUnlockFailureStage.HashData)]
    [InlineData("SignLength", ClientCertificateTokenUnlockFailureStage.QuerySignatureLength)]
    [InlineData("SignValue", ClientCertificateTokenUnlockFailureStage.SignHash)]
    public async Task UnlockAsync_NativeFailureReportsTheOperationThatFailed(
        string nativeOperation,
        ClientCertificateTokenUnlockFailureStage expectedStage)
    {
        var native = new RecordingCryptoApiNative { FailAt = nativeOperation, LastError = 5 };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Equal(expectedStage, result.FailureStage);
        Assert.Equal(5, result.ProviderErrorCode);
    }

    [Theory]
    [InlineData(1167, ClientCertificateTokenUnlockStatus.DeviceUnavailable)]
    [InlineData(5, ClientCertificateTokenUnlockStatus.UnknownFailure)]
    [InlineData(unchecked((int)0x80090016), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x80090017), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x80090018), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x80090019), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x8009001B), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x8009001D), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x8009001E), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    [InlineData(unchecked((int)0x8009001F), ClientCertificateTokenUnlockStatus.ProviderUnavailable)]
    public async Task UnlockAsync_AcquireFailuresAreNeverGuessedAsPinRejected(
        int errorCode,
        ClientCertificateTokenUnlockStatus expectedStatus)
    {
        var native = new RecordingCryptoApiNative { FailAt = "Acquire", LastError = errorCode };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(expectedStatus, result.Status);
        Assert.NotEqual(ClientCertificateTokenUnlockStatus.PinRejected, result.Status);
        Assert.Equal(errorCode, result.ProviderErrorCode);
        Assert.Equal(0, native.ReleaseProviderCount);
    }

    [Fact]
    public async Task UnlockAsync_CancellationAfterProviderAcquisitionReleasesEveryHandleExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new RecordingCryptoApiNative { CancelAfter = "GetKey", Cancellation = cancellation };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await unlocker.UnlockAsync(CreateAuthentication(), pin, cancellation.Token));

        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.Equal(1, native.ReleaseKeyCount);
        Assert.Equal(0, native.ReleaseHashCount);
        Assert.True(native.PinBufferWasZeroedAfterCall);
    }

    [Fact]
    public void Preflight_IsExplicitAndDoesNotSubmitPinOrSign()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);

        var result = preflight.Check(CreateAuthentication());

        Assert.True(result.IsAvailable);
        Assert.Equal(["Acquire", "GetKey"], native.Calls);
        Assert.Equal(0, native.SetPinCount);
        Assert.Equal(0, native.SignCount);
        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.Equal(1, native.ReleaseKeyCount);
    }

    [Fact]
    public void Preflight_AcceptsObservedTendyronSignatureProviderWithoutSubmittingPin()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);
        var authentication = CreateAuthentication() with
        {
            CryptographicProvider = new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                "{00000000-0000-0003}",
                "AT_SIGNATURE")
        };

        var result = preflight.Check(authentication);

        Assert.True(result.IsAvailable);
        Assert.Equal(2u, native.KeySpec);
        Assert.Equal(0, native.SetPinCount);
        Assert.Equal(0, native.SignCount);
    }

    [Fact]
    public void Preflight_AcceptsCurrentlyConnectedTendyronKeyExchangeProviderWithoutSubmittingPin()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);
        var authentication = CreateAuthentication() with
        {
            CryptographicProvider = new(
                "Tendyron OnKey CSP CCB v1.0",
                1,
                "SAMPLE_KEY_CONTAINER_3",
                "AT_KEYEXCHANGE")
        };

        var result = preflight.Check(authentication);

        Assert.True(result.IsAvailable);
        Assert.Equal(1u, native.KeySpec);
        Assert.Equal(0, native.SetPinCount);
        Assert.Equal(0, native.SignCount);
    }

    [Fact]
    public void Preflight_RejectsAnyConfigurationOutsideTheExplicitCcbCspShapeWithoutNativeCalls()
    {
        var native = new RecordingCryptoApiNative();
        var preflight = new WindowsCryptoApiTokenPreflight(native);
        var unsupported = CreateAuthentication() with
        {
            CryptographicProvider = new("other provider", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE")
        };

        var result = preflight.Check(unsupported);

        Assert.False(result.IsAvailable);
        Assert.Equal(ClientCertificateTokenUnlockStatus.Unsupported, result.FailureStatus);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task UnlockAsync_WhenSignatureProbeFails_ReleasesHashKeyAndProviderWithoutPersistingProbeMaterial()
    {
        var native = new RecordingCryptoApiNative { FailAt = "Sign" };
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        using var pin = CreatePin();

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Equal(1, native.ReleaseHashCount);
        Assert.Equal(1, native.ReleaseKeyCount);
        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.True(native.ChallengeAndSignatureBuffersWereZeroed);
    }

    [Fact]
    public async Task Coordinator_ReleasesCryptoApiLeaseWhenDeviceIsRemoved()
    {
        var native = new RecordingCryptoApiNative();
        var unlocker = new WindowsCryptoApiTokenUnlocker(native);
        var authentication = CreateAuthentication();
        using var storedPin = CreatePin();
        var pinStore = new TestPinStore(storedPin);
        var stampStore = new TestStampStore(authentication.GetNormalizedHash());
        await using var coordinator = new AuthenticationCoordinator(authentication, pinStore, stampStore, unlocker);

        await coordinator.HandleDevicePresenceAsync(true);
        await coordinator.HandleDevicePresenceAsync(false);

        Assert.Equal(1, native.ReleaseProviderCount);
        Assert.Equal(1, native.ReleaseKeyCount);
    }

    private static ClientCertificateAuthenticationOptions CreateAuthentication() => new(
        "ccb-usb-key",
        "b2b.ccb.com",
        443,
        new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
        new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-usb-key");

    private static ClientCertificateNativePromptOptions CreateNativePromptContinuation() => new(
        true,
        "UsbWebLauncher.NativePromptBroker.test",
        "test-policy",
        "",
        "",
        new string('A', 40),
        "",
        "#32770",
        [],
        10000,
        NativePromptWindowScopes.TrustedClientDescendant,
        "msedgewebview2.exe",
        "USB Web Launcher",
        "HwndWrapper[UsbWebLauncher.App;",
        "Edit",
        1010,
        "Button",
        1);

    private static ClientCertificatePin CreatePin()
    {
        var chars = RandomNumberGenerator.GetBytes(8).Select(value => (char)('0' + value % 10)).ToArray();
        try { return ClientCertificatePin.CopyOf(chars.AsMemory()); }
        finally { Array.Clear(chars, 0, chars.Length); }
    }

    private sealed class TestPinStore(ClientCertificatePin pin) : IClientCertificatePinStore
    {
        private readonly ClientCertificatePin _pin = pin;
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificatePin?>(ClientCertificatePin.CopyOf(_pin.AsMemory()));
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestStampStore(string hash) : IClientCertificateValidationStampStore
    {
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificateValidationStamp?>(new(authenticationId, hash));
        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingCryptoApiNative : ICryptoApiNative
    {
        public List<string> Calls { get; } = [];
        public string? FailAt { get; init; }
        public int LastError { get; init; } = 5;
        public string? CancelAfter { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public string? ContainerName { get; private set; }
        public string? ProviderName { get; private set; }
        public uint ProviderType { get; private set; }
        public uint AcquireFlags { get; private set; }
        public uint SetParameter { get; private set; }
        public int SetPinCount { get; private set; }
        public int SignCount { get; private set; }
        public int ReleaseProviderCount { get; private set; }
        public int ReleaseKeyCount { get; private set; }
        public int ReleaseHashCount { get; private set; }
        public uint KeySpec { get; private set; }
        public List<uint> SignKeySpecs { get; } = [];
        public uint HashAlgorithm { get; private set; }
        public bool PinWasAsciiAndNullTerminated { get; private set; }
        public bool PinBufferWasZeroedAfterCall { get; private set; }
        public bool ChallengeAndSignatureBuffersWereZeroed { get; private set; }
        private byte[]? _pin;
        private byte[]? _challenge;
        private byte[]? _signature;

        public bool Acquire(out IntPtr provider, string container, string providerName, uint providerType, uint flags)
        {
            Calls.Add("Acquire"); ContainerName = container; ProviderName = providerName; ProviderType = providerType; AcquireFlags = flags;
            provider = (IntPtr)11; return Succeeds("Acquire");
        }
        public bool SetProviderParameter(IntPtr provider, uint parameter, byte[] value, uint flags)
        {
            Calls.Add("SetPin"); SetParameter = parameter; SetPinCount++; _pin = value; PinWasAsciiAndNullTerminated = value.Length > 1 && value[^1] == 0 && value[..^1].All(x => x is >= 0x20 and <= 0x7e);
            return Succeeds("SetPin");
        }
        public bool GetUserKey(IntPtr provider, uint keySpec, out IntPtr key)
        {
            Calls.Add("GetKey"); KeySpec = keySpec; key = (IntPtr)12; return Succeeds("GetKey");
        }
        public bool CreateHash(IntPtr provider, uint algorithm, out IntPtr hash)
        {
            Calls.Add("CreateHash"); HashAlgorithm = algorithm; hash = (IntPtr)13; return Succeeds("CreateHash");
        }
        public bool HashData(IntPtr hash, byte[] data, uint flags)
        {
            Calls.Add("HashData"); _challenge = data; return Succeeds("HashData");
        }
        public bool SignHash(IntPtr hash, uint keySpec, byte[]? signature, ref uint signatureLength, uint flags)
        {
            Calls.Add("Sign"); SignCount++; SignKeySpecs.Add(keySpec);
            if (signature is null) { signatureLength = 32; return Succeeds("SignLength"); }
            _signature = signature; Array.Fill(signature, (byte)0x3a); return Succeeds("SignValue");
        }
        public bool ReleaseProvider(IntPtr provider, uint flags) { ReleaseProviderCount++; PinBufferWasZeroedAfterCall = _pin is not null && _pin.All(x => x == 0); ChallengeAndSignatureBuffersWereZeroed = (_challenge is null || _challenge.All(x => x == 0)) && (_signature is null || _signature.All(x => x == 0)); return true; }
        public bool ReleaseKey(IntPtr key) { ReleaseKeyCount++; return true; }
        public bool DestroyHash(IntPtr hash) { ReleaseHashCount++; return true; }
        public int GetLastError() => LastError;
        private bool Succeeds(string operation)
        {
            if (CancelAfter == operation)
            {
                Cancellation!.Cancel();
            }

            return FailAt != operation &&
                   !(FailAt == "Sign" && operation.StartsWith("Sign", StringComparison.Ordinal));
        }
    }
}
