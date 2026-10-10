using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Windows.Authentication;

internal sealed record Pkcs11TokenInfo(
    string SerialNumber,
    uint MinimumPinLength,
    uint MaximumPinLength,
    uint Flags);

internal interface IPkcs11Native : IDisposable
{
    uint Initialize();
    uint GetSlotList(bool tokenPresent, uint[]? slots, ref uint count);
    uint GetTokenInfo(uint slot, out Pkcs11TokenInfo tokenInfo);
    uint OpenSession(uint slot, uint flags, out uint session);
    uint Login(uint session, uint userType, byte[] value, uint valueLength);
    uint Logout(uint session);
    uint CloseSession(uint session);
    uint FinalizeModule();
}

internal interface IPkcs11NativeFactory
{
    IPkcs11Native Load(string modulePath);
}

internal sealed class Pkcs11NativeFactory : IPkcs11NativeFactory
{
    public IPkcs11Native Load(string modulePath) => new Pkcs11NativeModule(modulePath);
}

public static class WindowsClientCertificateTokenUnlockerFactory
{
    public static IClientCertificateTokenUnlocker Create(
        ClientCertificateAuthenticationOptions authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        if (authentication.UsesBrowserNativeAuthentication)
        {
            return new BrowserNativeLocalUnlockDisabled();
        }
        var method = authentication.TokenUnlock?.Method ?? "CryptoApi";
        return method.Trim().ToUpperInvariant() switch
        {
            "CRYPTOAPI" => new WindowsCryptoApiTokenUnlocker(),
            "PKCS11" => new WindowsPkcs11TokenUnlocker(),
            _ => throw new NotSupportedException("不支持配置的令牌解锁方式。")
        };
    }

    private sealed class BrowserNativeLocalUnlockDisabled : IClientCertificateTokenUnlocker
    {
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.Unsupported));
        }
    }
}

public sealed class WindowsPkcs11TokenUnlocker : IClientCertificateTokenUnlocker
{
    private const uint CkrOk = 0x00000000;
    private const uint CkrFunctionNotSupported = 0x00000054;
    private const uint CkrPinIncorrect = 0x000000A0;
    private const uint CkrPinInvalid = 0x000000A1;
    private const uint CkrPinLengthRange = 0x000000A2;
    private const uint CkrPinExpired = 0x000000A3;
    private const uint CkrPinLocked = 0x000000A4;
    private const uint CkrTokenNotPresent = 0x000000E0;
    private const uint CkrCryptokiAlreadyInitialized = 0x00000191;
    private const uint CkfSerialSession = 0x00000004;
    private const uint CkuUser = 0x00000001;
    private const uint MaximumSlotCount = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IPkcs11NativeFactory _factory;

    public WindowsPkcs11TokenUnlocker() : this(new Pkcs11NativeFactory())
    {
    }

    internal WindowsPkcs11TokenUnlocker(IPkcs11NativeFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
        ClientCertificateAuthenticationOptions authentication,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(pin);
        cancellationToken.ThrowIfCancellationRequested();

        var options = authentication.TokenUnlock;
        if (options is null ||
            !string.Equals(options.Method.Trim(), "Pkcs11", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(options.ModulePath) ||
            string.IsNullOrWhiteSpace(options.TokenSerialNumber))
        {
            return FailedWithoutCode(
                ClientCertificateTokenUnlockStatus.Unsupported,
                ClientCertificateTokenUnlockFailureStage.LoadProviderModule);
        }

        var moduleGate = Pkcs11ModuleSynchronization.GetGate(options.ModulePath);
        await moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sessionOwnsGate = false;
        try
        {
            var result = Unlock(
                options.ModulePath,
                options.TokenSerialNumber,
                pin.AsMemory().Span,
                cancellationToken);
            if (result.Status == ClientCertificateTokenUnlockStatus.Success &&
                result.Session is not null)
            {
                sessionOwnsGate = true;
                return result with
                {
                    Session = new SerializedPkcs11TokenUnlockSession(
                        result.Session,
                        moduleGate)
                };
            }

            return result;
        }
        finally
        {
            if (!sessionOwnsGate)
            {
                moduleGate.Release();
            }
        }
    }

    private ClientCertificateTokenUnlockResult Unlock(
        string modulePath,
        string tokenSerialNumber,
        ReadOnlySpan<char> pin,
        CancellationToken cancellationToken)
    {
        IPkcs11Native? native = null;
        var ownsInitialization = false;
        var ownsLogin = false;
        uint session = 0;
        byte[] pinBytes = [];

        try
        {
            try
            {
                native = _factory.Load(modulePath);
            }
            catch
            {
                return FailedWithoutCode(
                    ClientCertificateTokenUnlockStatus.ProviderUnavailable,
                    ClientCertificateTokenUnlockFailureStage.LoadProviderModule);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = native.Initialize();
            if (result is not (CkrOk or CkrCryptokiAlreadyInitialized))
            {
                return FailedFromCode(result, ClientCertificateTokenUnlockFailureStage.InitializeProviderModule);
            }

            ownsInitialization = result == CkrOk;
            cancellationToken.ThrowIfCancellationRequested();

            var tokenResult = FindToken(
                native,
                tokenSerialNumber,
                cancellationToken,
                out var slot,
                out var tokenInfo);
            if (tokenResult is not null)
            {
                return tokenResult;
            }

            if (!TryEncodePin(pin, out pinBytes))
            {
                return FailedWithoutCode(
                    ClientCertificateTokenUnlockStatus.Unsupported,
                    ClientCertificateTokenUnlockFailureStage.PinEncoding);
            }

            if (pinBytes.Length < tokenInfo!.MinimumPinLength ||
                pinBytes.Length > tokenInfo.MaximumPinLength)
            {
                return FailedFromCode(CkrPinLengthRange, ClientCertificateTokenUnlockFailureStage.VerifyPin);
            }

            result = native.OpenSession(slot, CkfSerialSession, out var openedSession);
            if (result != CkrOk || openedSession == 0)
            {
                return result == CkrOk
                    ? FailedWithoutCode(
                        ClientCertificateTokenUnlockStatus.UnknownFailure,
                        ClientCertificateTokenUnlockFailureStage.OpenTokenSession)
                    : FailedFromCode(result, ClientCertificateTokenUnlockFailureStage.OpenTokenSession);
            }

            session = openedSession;

            cancellationToken.ThrowIfCancellationRequested();
            result = native.Login(session, CkuUser, pinBytes, checked((uint)pinBytes.Length));
            Array.Clear(pinBytes, 0, pinBytes.Length);
            if (result != CkrOk)
            {
                return FailedFromCode(result, ClientCertificateTokenUnlockFailureStage.VerifyPin);
            }

            ownsLogin = true;
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new Pkcs11TokenUnlockSession(
                native,
                session,
                ownsInitialization,
                ownsLogin);
            native = null;
            session = 0;
            ownsInitialization = false;
            ownsLogin = false;
            return ClientCertificateTokenUnlockResult.Succeeded(lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FailedWithoutCode(
                ClientCertificateTokenUnlockStatus.UnknownFailure,
                ClientCertificateTokenUnlockFailureStage.Unexpected);
        }
        finally
        {
            if (pinBytes.Length != 0)
            {
                Array.Clear(pinBytes, 0, pinBytes.Length);
            }

            if (native is not null)
            {
                Pkcs11SafeRelease.All(native, session, ownsInitialization, ownsLogin);
            }
        }
    }

    private static ClientCertificateTokenUnlockResult? FindToken(
        IPkcs11Native native,
        string expectedSerialNumber,
        CancellationToken cancellationToken,
        out uint slot,
        out Pkcs11TokenInfo? tokenInfo)
    {
        slot = 0;
        tokenInfo = null;
        uint count = 0;
        var result = native.GetSlotList(true, null, ref count);
        if (result != CkrOk)
        {
            return FailedFromCode(result, ClientCertificateTokenUnlockFailureStage.EnumerateToken);
        }

        if (count is 0 or > MaximumSlotCount)
        {
            return FailedFromCode(CkrTokenNotPresent, ClientCertificateTokenUnlockFailureStage.EnumerateToken);
        }

        var slots = new uint[count];
        try
        {
            result = native.GetSlotList(true, slots, ref count);
            if (result != CkrOk)
            {
                return FailedFromCode(result, ClientCertificateTokenUnlockFailureStage.EnumerateToken);
            }

            var expected = expectedSerialNumber.Trim();
            var limit = Math.Min(count, checked((uint)slots.Length));
            for (var index = 0u; index < limit; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateSlot = slots[index];
                result = native.GetTokenInfo(candidateSlot, out var candidate);
                if (result != CkrOk)
                {
                    continue;
                }

                if (!string.Equals(
                        candidate.SerialNumber.Trim(),
                        expected,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                slot = candidateSlot;
                tokenInfo = candidate;
                return null;
            }

            return FailedFromCode(CkrTokenNotPresent, ClientCertificateTokenUnlockFailureStage.EnumerateToken);
        }
        finally
        {
            Array.Clear(slots, 0, slots.Length);
        }
    }

    private static bool TryEncodePin(ReadOnlySpan<char> pin, out byte[] bytes)
    {
        bytes = [];
        if (pin.IndexOf('\0') >= 0)
        {
            return false;
        }

        try
        {
            bytes = new byte[StrictUtf8.GetByteCount(pin)];
            StrictUtf8.GetBytes(pin, bytes);
            return true;
        }
        catch (EncoderFallbackException)
        {
            if (bytes.Length != 0)
            {
                Array.Clear(bytes, 0, bytes.Length);
            }

            bytes = [];
            return false;
        }
    }

    private static ClientCertificateTokenUnlockResult FailedFromCode(
        uint errorCode,
        ClientCertificateTokenUnlockFailureStage failureStage) =>
        Failed(Pkcs11FailureMapper.Map(errorCode), errorCode, failureStage);

    private static ClientCertificateTokenUnlockResult Failed(
        ClientCertificateTokenUnlockStatus status,
        uint errorCode,
        ClientCertificateTokenUnlockFailureStage failureStage) =>
        ClientCertificateTokenUnlockResult.Failed(
            status,
            unchecked((int)errorCode),
            failureStage);

    private static ClientCertificateTokenUnlockResult FailedWithoutCode(
        ClientCertificateTokenUnlockStatus status,
        ClientCertificateTokenUnlockFailureStage failureStage) =>
        ClientCertificateTokenUnlockResult.Failed(
            status,
            providerErrorCode: null,
            failureStage);

    private sealed class Pkcs11TokenUnlockSession(
        IPkcs11Native native,
        uint session,
        bool ownsInitialization,
        bool ownsLogin) : IClientCertificateTokenUnlockSession
    {
        private IPkcs11Native? _native = native;
        private uint _session = session;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var current = Interlocked.Exchange(ref _native, null);
            var currentSession = Interlocked.Exchange(ref _session, 0);
            if (current is not null)
            {
                Pkcs11SafeRelease.All(
                    current,
                    currentSession,
                    ownsInitialization,
                    ownsLogin);
            }
        }
    }

    private sealed class SerializedPkcs11TokenUnlockSession(
        IClientCertificateTokenUnlockSession inner,
        SemaphoreSlim moduleGate) : IClientCertificateTokenUnlockSession
    {
        private IClientCertificateTokenUnlockSession? _inner = inner;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                Interlocked.Exchange(ref _inner, null)?.Dispose();
            }
            finally
            {
                moduleGate.Release();
            }
        }
    }
}

internal static class Pkcs11ModuleSynchronization
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim GetGate(string modulePath)
    {
        var normalizedPath = Path.GetFullPath(modulePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
    }
}

internal static class Pkcs11FailureMapper
{
    private static readonly HashSet<uint> PinFailures =
    [
        0x000000A0, // CKR_PIN_INCORRECT
        0x000000A1, // CKR_PIN_INVALID
        0x000000A2, // CKR_PIN_LEN_RANGE
        0x000000A3, // CKR_PIN_EXPIRED
        0x000000A4  // CKR_PIN_LOCKED
    ];

    private static readonly HashSet<uint> DeviceFailures =
    [
        0x00000030, // CKR_DEVICE_ERROR
        0x00000032, // CKR_DEVICE_REMOVED
        0x000000E0, // CKR_TOKEN_NOT_PRESENT
        0x000000E1  // CKR_TOKEN_NOT_RECOGNIZED
    ];

    public static ClientCertificateTokenUnlockStatus Map(uint errorCode) =>
        PinFailures.Contains(errorCode)
            ? ClientCertificateTokenUnlockStatus.PinRejected
            : DeviceFailures.Contains(errorCode)
                ? ClientCertificateTokenUnlockStatus.DeviceUnavailable
                : errorCode == 0x00000054
                    ? ClientCertificateTokenUnlockStatus.Unsupported
                    : ClientCertificateTokenUnlockStatus.UnknownFailure;
}

internal static class Pkcs11SafeRelease
{
    public static void All(
        IPkcs11Native native,
        uint session,
        bool ownsInitialization,
        bool ownsLogin)
    {
        if (session != 0 && ownsLogin)
        {
            try { native.Logout(session); } catch { }
        }

        if (session != 0)
        {
            try { native.CloseSession(session); } catch { }
        }

        if (ownsInitialization)
        {
            try { native.FinalizeModule(); } catch { }
        }

        try { native.Dispose(); } catch { }
    }
}

internal sealed class Pkcs11NativeModule : IPkcs11Native
{
    private readonly InitializeDelegate _initialize;
    private readonly GetSlotListDelegate _getSlotList;
    private readonly GetTokenInfoDelegate _getTokenInfo;
    private readonly OpenSessionDelegate _openSession;
    private readonly LoginDelegate _login;
    private readonly LogoutDelegate _logout;
    private readonly CloseSessionDelegate _closeSession;
    private readonly FinalizeDelegate _finalize;
    private IntPtr _module;
    private int _disposed;

    public Pkcs11NativeModule(string modulePath)
    {
        _module = NativeLibrary.Load(modulePath);
        try
        {
            _initialize = GetExport<InitializeDelegate>("C_Initialize");
            _getSlotList = GetExport<GetSlotListDelegate>("C_GetSlotList");
            _getTokenInfo = GetExport<GetTokenInfoDelegate>("C_GetTokenInfo");
            _openSession = GetExport<OpenSessionDelegate>("C_OpenSession");
            _login = GetExport<LoginDelegate>("C_Login");
            _logout = GetExport<LogoutDelegate>("C_Logout");
            _closeSession = GetExport<CloseSessionDelegate>("C_CloseSession");
            _finalize = GetExport<FinalizeDelegate>("C_Finalize");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public uint Initialize() => _initialize(IntPtr.Zero);

    public uint GetSlotList(bool tokenPresent, uint[]? slots, ref uint count)
    {
        if (slots is null)
        {
            return _getSlotList(tokenPresent ? (byte)1 : (byte)0, IntPtr.Zero, ref count);
        }

        var pinned = GCHandle.Alloc(slots, GCHandleType.Pinned);
        try
        {
            return _getSlotList(
                tokenPresent ? (byte)1 : (byte)0,
                pinned.AddrOfPinnedObject(),
                ref count);
        }
        finally
        {
            pinned.Free();
        }
    }

    public uint GetTokenInfo(uint slot, out Pkcs11TokenInfo tokenInfo)
    {
        var result = _getTokenInfo(slot, out var nativeInfo);
        tokenInfo = result == 0
            ? new(
                DecodePaddedAscii(nativeInfo.SerialNumber),
                nativeInfo.MinimumPinLength,
                nativeInfo.MaximumPinLength,
                nativeInfo.Flags)
            : new(string.Empty, 0, 0, 0);
        return result;
    }

    public uint OpenSession(uint slot, uint flags, out uint session) =>
        _openSession(slot, flags, IntPtr.Zero, IntPtr.Zero, out session);

    public uint Login(uint session, uint userType, byte[] value, uint valueLength)
    {
        var pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            return _login(session, userType, pinned.AddrOfPinnedObject(), valueLength);
        }
        finally
        {
            pinned.Free();
        }
    }

    public uint Logout(uint session) => _logout(session);
    public uint CloseSession(uint session) => _closeSession(session);
    public uint FinalizeModule() => _finalize(IntPtr.Zero);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var module = Interlocked.Exchange(ref _module, IntPtr.Zero);
        if (module != IntPtr.Zero)
        {
            NativeLibrary.Free(module);
        }
    }

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    private static string DecodePaddedAscii(byte[]? value) =>
        value is null
            ? string.Empty
            : Encoding.ASCII.GetString(value).TrimEnd(' ', '\0');

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint InitializeDelegate(IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetSlotListDelegate(byte tokenPresent, IntPtr slots, ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetTokenInfoDelegate(uint slot, out NativeTokenInfo tokenInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint OpenSessionDelegate(
        uint slot,
        uint flags,
        IntPtr application,
        IntPtr notify,
        out uint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint LoginDelegate(uint session, uint userType, IntPtr value, uint valueLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint LogoutDelegate(uint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CloseSessionDelegate(uint session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FinalizeDelegate(IntPtr reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTokenInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Label;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ManufacturerId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Model;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] SerialNumber;
        public uint Flags;
        public uint MaximumSessionCount;
        public uint SessionCount;
        public uint MaximumReadWriteSessionCount;
        public uint ReadWriteSessionCount;
        public uint MaximumPinLength;
        public uint MinimumPinLength;
        public uint TotalPublicMemory;
        public uint FreePublicMemory;
        public uint TotalPrivateMemory;
        public uint FreePrivateMemory;
        public NativeVersion HardwareVersion;
        public NativeVersion FirmwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] UtcTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVersion
    {
        public byte Major;
        public byte Minor;
    }
}
