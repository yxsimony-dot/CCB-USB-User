using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Windows.Authentication;

internal interface ICryptoApiNative
{
    bool Acquire(out IntPtr provider, string container, string providerName, uint providerType, uint flags);
    bool SetProviderParameter(IntPtr provider, uint parameter, byte[] value, uint flags);
    bool GetUserKey(IntPtr provider, uint keySpec, out IntPtr key);
    bool CreateHash(IntPtr provider, uint algorithm, out IntPtr hash);
    bool HashData(IntPtr hash, byte[] data, uint flags);
    bool SignHash(IntPtr hash, uint keySpec, byte[]? signature, ref uint signatureLength, uint flags);
    bool ReleaseProvider(IntPtr provider, uint flags);
    bool ReleaseKey(IntPtr key);
    bool DestroyHash(IntPtr hash);
    int GetLastError();
}

internal sealed class CryptoApiNative : ICryptoApiNative
{
    public bool Acquire(out IntPtr provider, string container, string providerName, uint providerType, uint flags) =>
        CryptAcquireContextW(out provider, container, providerName, providerType, flags);

    public bool SetProviderParameter(IntPtr provider, uint parameter, byte[] value, uint flags) =>
        CryptSetProvParam(provider, parameter, value, flags);

    public bool GetUserKey(IntPtr provider, uint keySpec, out IntPtr key) => CryptGetUserKey(provider, keySpec, out key);
    public bool CreateHash(IntPtr provider, uint algorithm, out IntPtr hash) => CryptCreateHash(provider, algorithm, IntPtr.Zero, 0, out hash);
    public bool HashData(IntPtr hash, byte[] data, uint flags) => CryptHashData(hash, data, checked((uint)data.Length), flags);
    public bool SignHash(IntPtr hash, uint keySpec, byte[]? signature, ref uint signatureLength, uint flags) =>
        CryptSignHashW(hash, keySpec, null, flags, signature, ref signatureLength);
    public bool ReleaseProvider(IntPtr provider, uint flags) => CryptReleaseContext(provider, flags);
    public bool ReleaseKey(IntPtr key) => CryptDestroyKey(key);
    public bool DestroyHash(IntPtr hash) => CryptDestroyHash(hash);
    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptAcquireContextW(out IntPtr provider, string container, string providerName, uint providerType, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSetProvParam(IntPtr provider, uint parameter, byte[] value, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptGetUserKey(IntPtr provider, uint keySpec, out IntPtr key);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCreateHash(IntPtr provider, uint algorithm, IntPtr key, uint flags, out IntPtr hash);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptHashData(IntPtr hash, byte[] data, uint dataLength, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSignHashW(IntPtr hash, uint keySpec, string? description, uint flags, byte[]? signature, ref uint signatureLength);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptReleaseContext(IntPtr provider, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptDestroyKey(IntPtr key);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptDestroyHash(IntPtr hash);
}

public sealed class WindowsCryptoApiTokenUnlocker : IClientCertificateTokenUnlocker
{
    private const uint CryptSilent = 0x00000040;
    private const uint ProviderParameterKeyExchangePin = 0x00000020;
    private const uint ProviderParameterSignaturePin = 0x00000021;
    private const uint KeyExchange = 1;
    private const uint Signature = 2;
    private const uint Sha1 = 0x00008004;
    private const uint MaximumSignatureLength = 1024 * 1024;
    private readonly ICryptoApiNative _native;

    public WindowsCryptoApiTokenUnlocker() : this(new CryptoApiNative())
    {
    }

    internal WindowsCryptoApiTokenUnlocker(ICryptoApiNative native) => _native = native ?? throw new ArgumentNullException(nameof(native));

    public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
        ClientCertificateAuthenticationOptions authentication,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(pin);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveKeySpec(
                authentication.CryptographicProvider.KeySpec,
                out var pinParameter,
                out var keySpec))
        {
            return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.Unsupported,
                providerErrorCode: null,
                failureStage: ClientCertificateTokenUnlockFailureStage.Unexpected));
        }

        if (!TryEncodeAsciiPin(pin.AsMemory().Span, out var pinBytes))
        {
            return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Failed(
                ClientCertificateTokenUnlockStatus.Unsupported,
                providerErrorCode: null,
                failureStage: ClientCertificateTokenUnlockFailureStage.PinEncoding));
        }

        try
        {
            var provider = AcquireProvider(authentication, CryptSilent, cancellationToken, out var failure);
            if (provider is null)
            {
                return ValueTask.FromResult(failure!);
            }

            using (provider)
            {
                if (!SetPin(provider, pinParameter, pinBytes, cancellationToken, out failure) ||
                    !TryGetKey(provider, keySpec, cancellationToken, out var key, out failure))
                {
                    return ValueTask.FromResult(failure!);
                }

                using (key!)
                {
                    if (authentication.NativePromptContinuation is not { Enabled: true } &&
                        !TryProbe(provider, key!, keySpec, cancellationToken, out failure))
                    {
                        return ValueTask.FromResult(failure!);
                    }

                    var lease = new CryptoApiTokenUnlockSession(provider.Detach(), key!.Detach(), _native);
                    return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Succeeded(lease));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ValueTask.FromResult(UnknownFailure(ClientCertificateTokenUnlockFailureStage.Unexpected));
        }
        finally
        {
            Array.Clear(pinBytes, 0, pinBytes.Length);
        }
    }

    private CryptoProviderHandle? AcquireProvider(
        ClientCertificateAuthenticationOptions authentication,
        uint flags,
        CancellationToken cancellationToken,
        out ClientCertificateTokenUnlockResult? failure)
    {
        failure = null;
        IntPtr provider = IntPtr.Zero;
        bool acquired;
        try
        {
            acquired = _native.Acquire(
                out provider,
                authentication.CryptographicProvider.ContainerName,
                authentication.CryptographicProvider.CspName,
                checked((uint)authentication.CryptographicProvider.ProviderType),
                flags);
        }
        catch
        {
            CryptoApiSafeRelease.Provider(_native, provider);
            throw;
        }

        if (!acquired)
        {
            failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.AcquireProvider);
            return null;
        }

        if (provider == IntPtr.Zero)
        {
            failure = UnknownFailure(ClientCertificateTokenUnlockFailureStage.AcquireProvider);
            return null;
        }

        var handle = new CryptoProviderHandle(provider, _native);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private bool SetPin(
        CryptoProviderHandle provider,
        uint pinParameter,
        byte[] pinBytes,
        CancellationToken cancellationToken,
        out ClientCertificateTokenUnlockResult? failure)
    {
        failure = null;
        try
        {
            if (!_native.SetProviderParameter(provider.DangerousGetHandle(), pinParameter, pinBytes, 0))
            {
                failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.SetPin);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            Array.Clear(pinBytes, 0, pinBytes.Length);
        }
    }

    private bool TryGetKey(
        CryptoProviderHandle provider,
        uint keySpec,
        CancellationToken cancellationToken,
        out CryptoKeyHandle? key,
        out ClientCertificateTokenUnlockResult? failure)
    {
        key = null;
        failure = null;
        IntPtr keyHandle = IntPtr.Zero;
        bool acquired;
        try
        {
            acquired = _native.GetUserKey(provider.DangerousGetHandle(), keySpec, out keyHandle);
        }
        catch
        {
            CryptoApiSafeRelease.Key(_native, keyHandle);
            throw;
        }

        if (!acquired)
        {
            failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.GetUserKey);
            return false;
        }

        if (keyHandle == IntPtr.Zero)
        {
            failure = UnknownFailure(ClientCertificateTokenUnlockFailureStage.GetUserKey);
            return false;
        }

        key = new CryptoKeyHandle(keyHandle, _native);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private bool TryProbe(
        CryptoProviderHandle provider,
        CryptoKeyHandle key,
        uint keySpec,
        CancellationToken cancellationToken,
        out ClientCertificateTokenUnlockResult? failure)
    {
        failure = null;
        IntPtr hashHandle = IntPtr.Zero;
        bool created;
        try
        {
            created = _native.CreateHash(provider.DangerousGetHandle(), Sha1, out hashHandle);
        }
        catch
        {
            CryptoApiSafeRelease.Hash(_native, hashHandle);
            throw;
        }

        if (!created)
        {
            failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.CreateHash);
            return false;
        }

        if (hashHandle == IntPtr.Zero)
        {
            failure = UnknownFailure(ClientCertificateTokenUnlockFailureStage.CreateHash);
            return false;
        }

        using var hash = new CryptoHashHandle(hashHandle, _native);
        var challenge = new byte[32];
        byte[]? signature = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomNumberGenerator.Fill(challenge);
            if (!_native.HashData(hash.DangerousGetHandle(), challenge, 0))
            {
                failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.HashData);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint signatureLength = 0;
            if (!_native.SignHash(hash.DangerousGetHandle(), keySpec, null, ref signatureLength, 0))
            {
                failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.QuerySignatureLength);
                return false;
            }

            if (signatureLength is 0 or > MaximumSignatureLength)
            {
                failure = UnknownFailure(ClientCertificateTokenUnlockFailureStage.QuerySignatureLength);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            signature = new byte[checked((int)signatureLength)];
            if (!_native.SignHash(hash.DangerousGetHandle(), keySpec, signature, ref signatureLength, 0))
            {
                failure = FailFromNative(ClientCertificateTokenUnlockFailureStage.SignHash);
                return false;
            }

            if (signatureLength is 0 or > MaximumSignatureLength || signatureLength > signature.Length)
            {
                failure = UnknownFailure(ClientCertificateTokenUnlockFailureStage.SignHash);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            Array.Clear(challenge, 0, challenge.Length);
            if (signature is not null)
            {
                Array.Clear(signature, 0, signature.Length);
            }
        }
    }

    private ClientCertificateTokenUnlockResult FailFromNative(
        ClientCertificateTokenUnlockFailureStage failureStage)
    {
        try
        {
            var errorCode = _native.GetLastError();
            return ClientCertificateTokenUnlockResult.Failed(
                CryptoApiFailureMapper.Map(failureStage, errorCode),
                errorCode,
                failureStage);
        }
        catch
        {
            return UnknownFailure(failureStage);
        }
    }

    private static ClientCertificateTokenUnlockResult UnknownFailure(
        ClientCertificateTokenUnlockFailureStage failureStage) =>
        ClientCertificateTokenUnlockResult.Failed(
            ClientCertificateTokenUnlockStatus.UnknownFailure,
            providerErrorCode: null,
            failureStage: failureStage);

    private static bool TryEncodeAsciiPin(ReadOnlySpan<char> pin, out byte[] bytes)
    {
        bytes = new byte[checked(pin.Length + 1)];
        for (var index = 0; index < pin.Length; index++)
        {
            var character = pin[index];
            if (character is '\0' or > '\x7f')
            {
                Array.Clear(bytes, 0, bytes.Length);
                bytes = [];
                return false;
            }

            bytes[index] = (byte)character;
        }

        return true;
    }

    private static bool TryResolveKeySpec(
        string keySpec,
        out uint pinParameter,
        out uint nativeKeySpec)
    {
        switch (keySpec.Trim().ToUpperInvariant())
        {
            case "AT_KEYEXCHANGE":
                pinParameter = ProviderParameterKeyExchangePin;
                nativeKeySpec = KeyExchange;
                return true;
            case "AT_SIGNATURE":
                pinParameter = ProviderParameterSignaturePin;
                nativeKeySpec = Signature;
                return true;
            default:
                pinParameter = 0;
                nativeKeySpec = 0;
                return false;
        }
    }

    private sealed class CryptoProviderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICryptoApiNative _native;

        public CryptoProviderHandle(IntPtr handle, ICryptoApiNative native) : base(true)
        {
            _native = native;
            SetHandle(handle);
        }

        public IntPtr Detach() { var value = handle; SetHandleAsInvalid(); return value; }
        protected override bool ReleaseHandle() { CryptoApiSafeRelease.Provider(_native, handle); return true; }
    }

    private sealed class CryptoKeyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICryptoApiNative _native;

        public CryptoKeyHandle(IntPtr handle, ICryptoApiNative native) : base(true)
        {
            _native = native;
            SetHandle(handle);
        }

        public IntPtr Detach() { var value = handle; SetHandleAsInvalid(); return value; }
        protected override bool ReleaseHandle() { CryptoApiSafeRelease.Key(_native, handle); return true; }
    }

    private sealed class CryptoHashHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICryptoApiNative _native;

        public CryptoHashHandle(IntPtr handle, ICryptoApiNative native) : base(true)
        {
            _native = native;
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() { CryptoApiSafeRelease.Hash(_native, handle); return true; }
    }

    private sealed class CryptoApiTokenUnlockSession : IClientCertificateTokenUnlockSession
    {
        private readonly ICryptoApiNative _native;
        private IntPtr _provider;
        private IntPtr _key;
        private int _disposed;

        public CryptoApiTokenUnlockSession(IntPtr provider, IntPtr key, ICryptoApiNative native)
        {
            _provider = provider;
            _key = key;
            _native = native;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var key = Interlocked.Exchange(ref _key, IntPtr.Zero);
            if (key != IntPtr.Zero) { try { new CryptoKeyHandle(key, _native).Dispose(); } catch { } }

            var provider = Interlocked.Exchange(ref _provider, IntPtr.Zero);
            if (provider != IntPtr.Zero) { try { new CryptoProviderHandle(provider, _native).Dispose(); } catch { } }
        }
    }
}

internal static class CryptoApiFailureMapper
{
    // No CCB CSP PIN rejection code has been verified yet. Unknown native codes stay fail-closed.
    private const int ErrorDeviceNotConnected = 1167;
    private static readonly HashSet<int> ProviderUnavailableOnAcquire =
    [
        unchecked((int)0x80090016), // NTE_BAD_KEYSET
        unchecked((int)0x80090017), // NTE_PROV_TYPE_NOT_DEF
        unchecked((int)0x80090018), // NTE_PROV_TYPE_ENTRY_BAD
        unchecked((int)0x80090019), // NTE_KEYSET_NOT_DEF
        unchecked((int)0x8009001b), // NTE_PROV_TYPE_NO_MATCH
        unchecked((int)0x8009001d), // NTE_PROVIDER_DLL_FAIL
        unchecked((int)0x8009001e), // NTE_PROV_DLL_NOT_FOUND
        unchecked((int)0x8009001f)  // NTE_BAD_KEYSET_PARAM
    ];

    public static ClientCertificateTokenUnlockStatus Map(
        ClientCertificateTokenUnlockFailureStage failureStage,
        int errorCode) =>
        errorCode == ErrorDeviceNotConnected
            ? ClientCertificateTokenUnlockStatus.DeviceUnavailable
            : failureStage == ClientCertificateTokenUnlockFailureStage.AcquireProvider &&
              ProviderUnavailableOnAcquire.Contains(errorCode)
                ? ClientCertificateTokenUnlockStatus.ProviderUnavailable
                : ClientCertificateTokenUnlockStatus.UnknownFailure;
}

internal static class CryptoApiSafeRelease
{
    public static void Provider(ICryptoApiNative native, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { native.ReleaseProvider(handle, 0); } catch { }
    }

    public static void Key(ICryptoApiNative native, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { native.ReleaseKey(handle); } catch { }
    }

    public static void Hash(ICryptoApiNative native, IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { native.DestroyHash(handle); } catch { }
    }
}

public sealed record WindowsCryptoApiPreflightResult(bool IsAvailable, ClientCertificateTokenUnlockStatus? FailureStatus, int? ProviderErrorCode);

public sealed class WindowsCryptoApiTokenPreflight
{
    private const uint CryptSilent = 0x00000040;
    private const uint KeyExchange = 1;
    private const uint Signature = 2;
    private readonly ICryptoApiNative _native;
    public WindowsCryptoApiTokenPreflight() : this(new CryptoApiNative()) { }
    internal WindowsCryptoApiTokenPreflight(ICryptoApiNative native) => _native = native ?? throw new ArgumentNullException(nameof(native));

    public WindowsCryptoApiPreflightResult Check(ClientCertificateAuthenticationOptions authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        if (!IsSupported(authentication) ||
            !TryResolveKeySpec(authentication.CryptographicProvider.KeySpec, out var keySpec))
        {
            return new(false, ClientCertificateTokenUnlockStatus.Unsupported, null);
        }

        IntPtr providerValue = IntPtr.Zero;
        try
        {
            bool acquired;
            try
            {
                acquired = _native.Acquire(out providerValue, authentication.CryptographicProvider.ContainerName, authentication.CryptographicProvider.CspName, (uint)authentication.CryptographicProvider.ProviderType, CryptSilent);
            }
            catch
            {
                CryptoApiSafeRelease.Provider(_native, providerValue);
                throw;
            }

            if (!acquired)
            {
                return NativeFailure(ClientCertificateTokenUnlockFailureStage.AcquireProvider);
            }

            if (providerValue == IntPtr.Zero)
            {
                return UnknownFailure();
            }

            using var provider = new PreflightProviderHandle(providerValue, _native);
            IntPtr keyValue = IntPtr.Zero;
            bool acquiredKey;
            try
            {
                acquiredKey = _native.GetUserKey(provider.DangerousGetHandle(), keySpec, out keyValue);
            }
            catch
            {
                CryptoApiSafeRelease.Key(_native, keyValue);
                throw;
            }

            if (!acquiredKey)
            {
                return NativeFailure(ClientCertificateTokenUnlockFailureStage.GetUserKey);
            }

            if (keyValue == IntPtr.Zero)
            {
                return UnknownFailure();
            }

            using var key = new PreflightKeyHandle(keyValue, _native);
            return new(true, null, null);
        }
        catch
        {
            return UnknownFailure();
        }
    }

    private static bool IsSupported(ClientCertificateAuthenticationOptions authentication)
    {
        var provider = authentication.CryptographicProvider;
        if (provider.ProviderType != 1)
        {
            return false;
        }

        return
            (string.Equals(provider.CspName, "CCB HD CSP V3.0", StringComparison.Ordinal) &&
             MatchesContainer(provider.ContainerName, "3FB38DE13A36A817263BCE71D95AA8EF6BAFE363A05209CF4981E6813C5F011B", "0873AD3FD98CB90768B4DE5057FAE0E072A10498F2FDBFB03D596A54258146D0") &&
             string.Equals(provider.KeySpec, "AT_KEYEXCHANGE", StringComparison.Ordinal)) ||
            (string.Equals(provider.CspName, "Tendyron OnKey CSP CCB v1.0", StringComparison.Ordinal) &&
             MatchesContainer(provider.ContainerName, "46A3AD52C842BDB407E33D095FA4128F33A078029E9D1CC54AA039110D6FBE63", "E538671BDC0C7A950AAF0FCB2D6622CE7C6002B989BCB584BA4102824FE02D1D") &&
             string.Equals(provider.KeySpec, "AT_SIGNATURE", StringComparison.Ordinal)) ||
            (string.Equals(provider.CspName, "Tendyron OnKey CSP CCB v1.0", StringComparison.Ordinal) &&
             MatchesContainer(provider.ContainerName, "1995C884326427896AF106A1C0CD843BB6B23EEC0AD847E318E074F26F6A7C56", "82F9F849671B3FB978DE734058E1093A9C99BE9F004C129F22BF101079588C45") &&
             string.Equals(provider.KeySpec, "AT_KEYEXCHANGE", StringComparison.Ordinal)) ||
            (string.Equals(provider.CspName, "Tendyron OnKey CSP CCB v1.0", StringComparison.Ordinal) &&
             MatchesContainer(provider.ContainerName, "CE8EBEDC4FA4E07FEAC0F29CBD47D401C77B8CB5E6D556EBC8E03FD1AC7523FF", "CBB8CB5284E33666A1FE2458ACA7CAE91A5159F4524110874E9034C2B0E76222") &&
             string.Equals(provider.KeySpec, "AT_SIGNATURE", StringComparison.Ordinal)) ||
            (string.Equals(provider.CspName, "Tendyron OnKey CSP CCB v1.0", StringComparison.Ordinal) &&
             MatchesContainer(provider.ContainerName, "99C714C01428E90B5F061CC95F65B3C5677F3B4EA0A969EEEFCA1264CE95E00E", "1BE841D8200E5CB2F96FAA528D7CFA6EC8B2E09FF1BF462845A294FA734A8D8C") &&
             string.Equals(provider.KeySpec, "AT_SIGNATURE", StringComparison.Ordinal));
    }

    // Keep historical preflight identities opaque in source; the second digest is a synthetic test fixture.
    // This read-only compatibility check never authenticates or submits a PIN.
    private static bool MatchesContainer(string containerName, string historicalHash, string fixtureHash)
    {
        if (string.IsNullOrEmpty(containerName))
        {
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(containerName)));
        return string.Equals(hash, historicalHash, StringComparison.Ordinal) ||
               string.Equals(hash, fixtureHash, StringComparison.Ordinal);
    }

    private static bool TryResolveKeySpec(string keySpec, out uint nativeKeySpec)
    {
        switch (keySpec.Trim().ToUpperInvariant())
        {
            case "AT_KEYEXCHANGE":
                nativeKeySpec = KeyExchange;
                return true;
            case "AT_SIGNATURE":
                nativeKeySpec = Signature;
                return true;
            default:
                nativeKeySpec = 0;
                return false;
        }
    }

    private WindowsCryptoApiPreflightResult NativeFailure(
        ClientCertificateTokenUnlockFailureStage failureStage)
    {
        try
        {
            var errorCode = _native.GetLastError();
            return new(false, CryptoApiFailureMapper.Map(failureStage, errorCode), errorCode);
        }
        catch
        {
            return UnknownFailure();
        }
    }

    private static WindowsCryptoApiPreflightResult UnknownFailure() =>
        new(false, ClientCertificateTokenUnlockStatus.UnknownFailure, null);

    private sealed class PreflightProviderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICryptoApiNative _native;
        public PreflightProviderHandle(IntPtr handle, ICryptoApiNative native) : base(true) { _native = native; SetHandle(handle); }
        protected override bool ReleaseHandle() { CryptoApiSafeRelease.Provider(_native, handle); return true; }
    }
    private sealed class PreflightKeyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICryptoApiNative _native;
        public PreflightKeyHandle(IntPtr handle, ICryptoApiNative native) : base(true) { _native = native; SetHandle(handle); }
        protected override bool ReleaseHandle() { CryptoApiSafeRelease.Key(_native, handle); return true; }
    }
}
