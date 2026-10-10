// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

[Collection(UsbHardwareCollection.Name)]
public sealed class WindowsPkcs11TokenUnlockerTests
{
    [Fact]
    public async Task BrowserNative_FactoryNeverLoadsOrUnlocksALocalKey()
    {
        var authentication = CreateAuthentication() with
        {
            AuthenticationMode = "BrowserNative",
            TokenUnlock = new("UnusedProvider"),
            CryptographicProvider = null!
        };
        var unlocker = WindowsClientCertificateTokenUnlockerFactory.Create(authentication);
        using var pin = CreatePin(8);
        var result = await unlocker.UnlockAsync(authentication, pin);
        Assert.Equal(ClientCertificateTokenUnlockStatus.Unsupported, result.Status);
        Assert.Null(result.Session);
    }

    [Fact]
    public void Factory_SelectsPkcs11ForTheConfiguredProfileAndKeepsCryptoApiAsTheLegacyDefault()
    {
        var configured = WindowsClientCertificateTokenUnlockerFactory.Create(CreateAuthentication());
        var legacy = WindowsClientCertificateTokenUnlockerFactory.Create(
            CreateAuthentication() with { TokenUnlock = null });

        Assert.IsType<WindowsPkcs11TokenUnlocker>(configured);
        Assert.IsType<WindowsCryptoApiTokenUnlocker>(legacy);
    }

    [Fact]
    public async Task UnlockAsync_LogsIntoTheExactlyConfiguredTokenAndKeepsSessionAlive()
    {
        var native = new RecordingPkcs11Native(
            [4, 9],
            new Dictionary<uint, Pkcs11TokenInfo>
            {
                [4] = new("OTHER", 6, 8, 0),
                [9] = new("SAMPLE_KEY_CONTAINER_1", 6, 8, 0)
            });
        var factory = new RecordingPkcs11NativeFactory(native);
        var unlocker = new WindowsPkcs11TokenUnlocker(factory);
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal(ModulePath, factory.ModulePath);
        Assert.Equal(
            ["Initialize", "GetSlotListCount", "GetSlotList", "GetTokenInfo:4", "GetTokenInfo:9", "OpenSession:9", "Login"],
            native.Calls);
        Assert.Equal(4u, native.OpenSessionFlags);
        Assert.Equal(1u, native.LoginUserType);
        Assert.NotNull(native.LoginBuffer);
        Assert.All(native.LoginBuffer!, value => Assert.Equal(0, value));
        Assert.Equal(0, native.CloseSessionCount);
        Assert.Equal(0, native.FinalizeCount);

        result.Session!.Dispose();
        result.Session.Dispose();

        Assert.Equal(1, native.LogoutCount);
        Assert.Equal(1, native.CloseSessionCount);
        Assert.Equal(1, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnlockAsync_UsesTheSameTrimmedMethodNormalizationAsConfigurationValidation()
    {
        var native = MatchingNative();
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        var authentication = CreateAuthentication() with
        {
            TokenUnlock = CreateAuthentication().TokenUnlock! with { Method = " Pkcs11 " }
        };
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(authentication, pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        result.Session!.Dispose();
    }

    [Fact]
    public async Task UnlockAsync_WhenPinIsRejected_MapsTheStandardCodeAndReleasesEverything()
    {
        var native = MatchingNative() with { LoginResult = 0x000000A0 };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.PinRejected, result.Status);
        Assert.Equal(0x000000A0, result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.VerifyPin, result.FailureStage);
        Assert.Equal(0, native.LogoutCount);
        Assert.Equal(1, native.CloseSessionCount);
        Assert.Equal(1, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
        Assert.NotNull(native.LoginBuffer);
        Assert.All(native.LoginBuffer!, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0x00000032, ClientCertificateTokenUnlockStatus.DeviceUnavailable)]
    [InlineData(0x000000E0, ClientCertificateTokenUnlockStatus.DeviceUnavailable)]
    [InlineData(0x00000054, ClientCertificateTokenUnlockStatus.Unsupported)]
    [InlineData(0x00000005, ClientCertificateTokenUnlockStatus.UnknownFailure)]
    public async Task UnlockAsync_MapsStandardLoginFailuresWithoutGuessing(
        int errorCode,
        ClientCertificateTokenUnlockStatus expectedStatus)
    {
        var native = MatchingNative() with { LoginResult = checked((uint)errorCode) };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(errorCode, result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.VerifyPin, result.FailureStage);
    }

    [Fact]
    public async Task UnlockAsync_WhenUserIsAlreadyLoggedIn_DoesNotClaimTheSubmittedPinWasVerified()
    {
        var native = MatchingNative() with { LoginResult = 0x00000100 };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Equal(0x00000100, result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.VerifyPin, result.FailureStage);
        Assert.Null(result.Session);
        Assert.Equal(0, native.LogoutCount);
        Assert.Equal(1, native.CloseSessionCount);
        Assert.Equal(1, native.FinalizeCount);
    }

    [Fact]
    public async Task UnlockAsync_WhenConfiguredTokenIsAbsent_DoesNotSubmitPin()
    {
        var native = new RecordingPkcs11Native(
            [4],
            new Dictionary<uint, Pkcs11TokenInfo> { [4] = new("OTHER", 6, 8, 0) });
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.DeviceUnavailable, result.Status);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.EnumerateToken, result.FailureStage);
        Assert.DoesNotContain("Login", native.Calls);
        Assert.Equal(1, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnlockAsync_WhenOpenSessionFails_DoesNotCloseAnUnownedOutputHandle()
    {
        var native = MatchingNative() with
        {
            OpenSessionResult = 0x00000005,
            OpenSessionValue = 88
        };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.UnknownFailure, result.Status);
        Assert.Equal(0x00000005, result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.OpenTokenSession, result.FailureStage);
        Assert.Equal(0, native.CloseSessionCount);
        Assert.Equal(1, native.FinalizeCount);
    }

    [Fact]
    public async Task UnlockAsync_SerializesModuleLifecycleAcrossUnlockerInstances()
    {
        var modulePath = $@"C:\test\pkcs11-{Guid.NewGuid():N}.dll";
        var authentication = CreateAuthentication() with
        {
            TokenUnlock = CreateAuthentication().TokenUnlock! with { ModulePath = modulePath }
        };
        var firstNative = MatchingNative();
        var secondNative = MatchingNative();
        var firstUnlocker = new WindowsPkcs11TokenUnlocker(
            new RecordingPkcs11NativeFactory(firstNative));
        var secondUnlocker = new WindowsPkcs11TokenUnlocker(
            new RecordingPkcs11NativeFactory(secondNative));
        using var firstPin = CreatePin(8);
        using var secondPin = CreatePin(8);

        var firstResult = await firstUnlocker.UnlockAsync(authentication, firstPin);
        var secondTask = secondUnlocker.UnlockAsync(authentication, secondPin).AsTask();
        await Task.Yield();

        Assert.False(secondTask.IsCompleted);
        Assert.Empty(secondNative.Calls);

        firstResult.Session!.Dispose();
        var secondResult = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, secondResult.Status);
        secondResult.Session!.Dispose();
        Assert.Equal(1, firstNative.FinalizeCount);
        Assert.Equal(1, secondNative.FinalizeCount);
    }

    [Fact]
    public async Task UnlockAsync_WhenModuleCannotBeLoaded_ReturnsProviderUnavailable()
    {
        var factory = new RecordingPkcs11NativeFactory(new DllNotFoundException());
        var unlocker = new WindowsPkcs11TokenUnlocker(factory);
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.ProviderUnavailable, result.Status);
        Assert.Null(result.ProviderErrorCode);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.LoadProviderModule, result.FailureStage);
    }

    [Fact]
    public async Task UnlockAsync_WhenInitializationFails_DoesNotFinalizeUnownedInitialization()
    {
        var native = MatchingNative() with { InitializeResult = 0x00000054 };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.Unsupported, result.Status);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.InitializeProviderModule, result.FailureStage);
        Assert.Equal(0, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnlockAsync_WhenModuleWasAlreadyInitialized_DoesNotFinalizeExternalOwnership()
    {
        var native = MatchingNative() with { InitializeResult = 0x00000191 };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);
        result.Session!.Dispose();

        Assert.Equal(ClientCertificateTokenUnlockStatus.Success, result.Status);
        Assert.Equal(1, native.LogoutCount);
        Assert.Equal(1, native.CloseSessionCount);
        Assert.Equal(0, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnlockAsync_WhenCancelledAfterLogin_LogsOutAndReleasesAllOwnedResources()
    {
        using var cancellation = new CancellationTokenSource();
        var native = MatchingNative() with { CancelAfterLogin = cancellation };
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(8);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await unlocker.UnlockAsync(CreateAuthentication(), pin, cancellation.Token));

        Assert.Equal(1, native.LogoutCount);
        Assert.Equal(1, native.CloseSessionCount);
        Assert.Equal(1, native.FinalizeCount);
        Assert.Equal(1, native.DisposeCount);
        Assert.NotNull(native.LoginBuffer);
        Assert.All(native.LoginBuffer!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UnlockAsync_RejectsPinOutsideTokenLengthRangeWithoutCallingLogin()
    {
        var native = MatchingNative();
        var unlocker = new WindowsPkcs11TokenUnlocker(new RecordingPkcs11NativeFactory(native));
        using var pin = CreatePin(5);

        var result = await unlocker.UnlockAsync(CreateAuthentication(), pin);

        Assert.Equal(ClientCertificateTokenUnlockStatus.PinRejected, result.Status);
        Assert.Equal(ClientCertificateTokenUnlockFailureStage.VerifyPin, result.FailureStage);
        Assert.DoesNotContain("Login", native.Calls);
        Assert.Equal(0, native.CloseSessionCount);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void NativeModule_WhenExplicitHardwareTestIsEnabled_FindsTheConfiguredTokenWithoutSubmittingPin()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        using var native = new Pkcs11NativeModule(ModulePath);
        var initializeResult = native.Initialize();
        var ownsInitialization = initializeResult == 0;
        Assert.True(
            initializeResult is 0 or 0x00000191,
            $"PKCS#11 initialization failed: 0x{initializeResult:X8}");

        try
        {
            uint count = 0;
            Assert.Equal(0u, native.GetSlotList(true, null, ref count));
            Assert.InRange(count, 1u, 64u);

            var slots = new uint[count];
            Assert.Equal(0u, native.GetSlotList(true, slots, ref count));
            var matchingSlot = slots
                .Take(checked((int)count))
                .Single(slot => native.GetTokenInfo(slot, out var info) == 0 &&
                                info.SerialNumber == "SAMPLE_KEY_CONTAINER_1");
            Assert.Equal(0u, native.OpenSession(matchingSlot, 0x00000004, out var session));
            Assert.NotEqual(0u, session);
            Assert.Equal(0u, native.CloseSession(session));
        }
        finally
        {
            if (ownsInitialization)
            {
                native.FinalizeModule();
            }
        }
    }

    private const string ModulePath = @"C:\Windows\System32\CCB_HDZB_2G_P11.dll";

    private static RecordingPkcs11Native MatchingNative() =>
        new(
            [9],
            new Dictionary<uint, Pkcs11TokenInfo>
            {
                [9] = new("SAMPLE_KEY_CONTAINER_1", 6, 8, 0)
            });

    private static ClientCertificateAuthenticationOptions CreateAuthentication() => new(
        "ccb-usb-key",
        "b2b.ccb.com",
        443,
        new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
        new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-usb-key",
        new("Pkcs11", ModulePath, "SAMPLE_KEY_CONTAINER_1"));

    private static ClientCertificatePin CreatePin(int length)
    {
        var chars = RandomNumberGenerator.GetBytes(length)
            .Select(value => (char)('0' + value % 10))
            .ToArray();
        try
        {
            return ClientCertificatePin.CopyOf(chars.AsMemory());
        }
        finally
        {
            Array.Clear(chars, 0, chars.Length);
        }
    }

    private sealed class RecordingPkcs11NativeFactory : IPkcs11NativeFactory
    {
        private readonly IPkcs11Native? _native;
        private readonly Exception? _exception;

        public RecordingPkcs11NativeFactory(IPkcs11Native native) => _native = native;
        public RecordingPkcs11NativeFactory(Exception exception) => _exception = exception;
        public string? ModulePath { get; private set; }

        public IPkcs11Native Load(string modulePath)
        {
            ModulePath = modulePath;
            if (_exception is not null)
            {
                throw _exception;
            }

            return _native!;
        }
    }

    private sealed record RecordingPkcs11Native(
        uint[] Slots,
        IReadOnlyDictionary<uint, Pkcs11TokenInfo> TokenInfos) : IPkcs11Native
    {
        public List<string> Calls { get; } = [];
        public uint InitializeResult { get; init; }
        public uint OpenSessionResult { get; init; }
        public uint OpenSessionValue { get; init; } = 27;
        public uint LoginResult { get; init; }
        public CancellationTokenSource? CancelAfterLogin { get; init; }
        public byte[]? LoginBuffer { get; private set; }
        public uint OpenSessionFlags { get; private set; }
        public uint LoginUserType { get; private set; }
        public int LogoutCount { get; private set; }
        public int CloseSessionCount { get; private set; }
        public int FinalizeCount { get; private set; }
        public int DisposeCount { get; private set; }

        public uint Initialize()
        {
            Calls.Add("Initialize");
            return InitializeResult;
        }

        public uint GetSlotList(bool tokenPresent, uint[]? slots, ref uint count)
        {
            Calls.Add(slots is null ? "GetSlotListCount" : "GetSlotList");
            if (slots is null)
            {
                count = checked((uint)Slots.Length);
                return 0;
            }

            Array.Copy(Slots, slots, Slots.Length);
            count = checked((uint)Slots.Length);
            return 0;
        }

        public uint GetTokenInfo(uint slot, out Pkcs11TokenInfo tokenInfo)
        {
            Calls.Add($"GetTokenInfo:{slot}");
            if (TokenInfos.TryGetValue(slot, out var value))
            {
                tokenInfo = value;
                return 0;
            }

            tokenInfo = new(string.Empty, 0, 0, 0);
            return 3;
        }

        public uint OpenSession(uint slot, uint flags, out uint session)
        {
            Calls.Add($"OpenSession:{slot}");
            OpenSessionFlags = flags;
            session = OpenSessionValue;
            return OpenSessionResult;
        }

        public uint Login(uint session, uint userType, byte[] value, uint valueLength)
        {
            Calls.Add("Login");
            LoginUserType = userType;
            LoginBuffer = value;
            Assert.Equal(value.Length, checked((int)valueLength));
            CancelAfterLogin?.Cancel();
            return LoginResult;
        }

        public uint Logout(uint session)
        {
            Calls.Add("Logout");
            LogoutCount++;
            return 0;
        }

        public uint CloseSession(uint session)
        {
            Calls.Add("CloseSession");
            CloseSessionCount++;
            return 0;
        }

        public uint FinalizeModule()
        {
            Calls.Add("Finalize");
            FinalizeCount++;
            return 0;
        }

        public void Dispose()
        {
            Calls.Add("Dispose");
            DisposeCount++;
        }
    }
}
