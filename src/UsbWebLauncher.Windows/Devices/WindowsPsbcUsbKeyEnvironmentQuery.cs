using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Windows.Devices;

internal interface IWindowsServiceStateReader
{
    PsbcServiceState GetServiceState(string serviceName);
}

internal interface IWindowsCryptographicProviderReader
{
    bool IsRegistered(string providerName);

    bool TryGetProviderType(string providerName, out uint providerType);
}

internal interface IWindowsRegistryMarkerReader
{
    bool IsPresent(PsbcRegistryMarker marker);
}

internal interface IWindowsPsbcTokenEvidenceReader
{
    PsbcTokenEvidence Query(PsbcTokenQuery query);
}

public sealed class WindowsPsbcUsbKeyEnvironmentQuery : IPsbcUsbKeyEnvironmentQuery
{
    private readonly IWindowsServiceStateReader _serviceReader;
    private readonly IWindowsCryptographicProviderReader _providerReader;
    private readonly IWindowsRegistryMarkerReader _registryReader;
    private readonly IWindowsPsbcTokenEvidenceReader _tokenReader;

    public WindowsPsbcUsbKeyEnvironmentQuery()
    {
        var providerReader = new WindowsCryptographicProviderReader();
        _serviceReader = new WindowsServiceStateReader();
        _providerReader = providerReader;
        _registryReader = new WindowsRegistryMarkerReader();
        _tokenReader = new WindowsCspTokenEvidenceReader(providerReader);
    }

    internal WindowsPsbcUsbKeyEnvironmentQuery(
        IWindowsServiceStateReader serviceReader,
        IWindowsCryptographicProviderReader providerReader,
        IWindowsRegistryMarkerReader registryReader,
        IWindowsPsbcTokenEvidenceReader tokenReader)
    {
        _serviceReader = serviceReader ?? throw new ArgumentNullException(nameof(serviceReader));
        _providerReader = providerReader ?? throw new ArgumentNullException(nameof(providerReader));
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _tokenReader = tokenReader ?? throw new ArgumentNullException(nameof(tokenReader));
    }

    public Task<PsbcServiceState> GetServiceStateAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return RunReadAsync(() => _serviceReader.GetServiceState(serviceName), cancellationToken);
    }

    public Task<bool> IsCryptographicProviderRegisteredAsync(
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return RunReadAsync(() => _providerReader.IsRegistered(providerName), cancellationToken);
    }

    public Task<bool> IsRegistryMarkerPresentAsync(
        PsbcRegistryMarker marker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return RunReadAsync(() => _registryReader.IsPresent(marker), cancellationToken);
    }

    public Task<PsbcTokenEvidence> QueryTokenAsync(
        PsbcTokenQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunReadAsync(() => _tokenReader.Query(query), cancellationToken);
    }

    private static Task<T> RunReadAsync<T>(Func<T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(read, cancellationToken);
    }
}

internal sealed class WindowsServiceStateReader : IWindowsServiceStateReader
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ErrorServiceDoesNotExist = 1060;
    private const uint ServiceRunning = 0x00000004;

    public PsbcServiceState GetServiceState(string serviceName)
    {
        using var manager = NativeMethods.OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var service = NativeMethods.OpenService(
            manager,
            serviceName,
            ServiceQueryStatus);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return PsbcServiceState.NotInstalled;
            }

            throw new Win32Exception(error);
        }

        var status = new NativeMethods.ServiceStatusProcess();
        if (!NativeMethods.QueryServiceStatusEx(
                service,
                infoLevel: 0,
                ref status,
                checked((uint)Marshal.SizeOf<NativeMethods.ServiceStatusProcess>()),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return status.CurrentState == ServiceRunning
            ? PsbcServiceState.Running
            : PsbcServiceState.Stopped;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenSCManager(
            string? machineName,
            string? databaseName,
            uint desiredAccess);

        [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenService(
            ServiceHandle serviceControlManager,
            string serviceName,
            uint desiredAccess);

        [DllImport("Advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(
            ServiceHandle service,
            int infoLevel,
            ref ServiceStatusProcess buffer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("Advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }
}

internal sealed class WindowsCryptographicProviderReader : IWindowsCryptographicProviderReader
{
    private const string ProvidersPath =
        @"SOFTWARE\Microsoft\Cryptography\Defaults\Provider";

    public bool IsRegistered(string providerName) =>
        TryReadProviderType(providerName, out _);

    public bool TryGetProviderType(string providerName, out uint providerType) =>
        TryReadProviderType(providerName, out providerType);

    private static bool TryReadProviderType(string providerName, out uint providerType)
    {
        providerType = 0;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    view);
                using var provider = localMachine.OpenSubKey(
                    $@"{ProvidersPath}\{providerName}",
                    writable: false);
                if (provider?.GetValue("Type") is int signedType && signedType > 0)
                {
                    providerType = checked((uint)signedType);
                    return true;
                }

                if (provider?.GetValue("Type") is uint unsignedType && unsignedType > 0)
                {
                    providerType = unsignedType;
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return false;
    }
}

internal sealed class WindowsRegistryMarkerReader : IWindowsRegistryMarkerReader
{
    public bool IsPresent(PsbcRegistryMarker marker)
    {
        var (hive, subKeyPath) = ParsePath(marker.KeyPath);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
                if (key is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(marker.ValueName))
                {
                    return true;
                }

                var value = key.GetValue(
                    marker.ValueName,
                    defaultValue: null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null)
                {
                    continue;
                }

                if (marker.ExpectedValue is null)
                {
                    return true;
                }

                if (value is string[] values &&
                    values.Contains(marker.ExpectedValue, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        marker.ExpectedValue,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return false;
    }

    private static (RegistryHive Hive, string SubKeyPath) ParsePath(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        var normalized = keyPath.Replace('/', '\\').Trim('\\');
        foreach (var prefix in new[] { "HKEY_LOCAL_MACHINE\\", "HKLM\\" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (RegistryHive.LocalMachine, normalized[prefix.Length..]);
            }
        }

        foreach (var prefix in new[] { "HKEY_CURRENT_USER\\", "HKCU\\" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (RegistryHive.CurrentUser, normalized[prefix.Length..]);
            }
        }

        return (RegistryHive.LocalMachine, normalized);
    }
}

internal interface IWindowsCryptoProviderProbeNative
{
    bool Acquire(
        out IntPtr provider,
        string? containerName,
        string providerName,
        uint providerType,
        uint flags);

    bool GetProviderParameter(
        IntPtr provider,
        uint parameter,
        byte[]? data,
        ref uint dataLength,
        uint flags);

    bool Release(IntPtr provider, uint flags);

    int GetLastError();
}

internal sealed class WindowsCryptoProviderProbeNative : IWindowsCryptoProviderProbeNative
{
    public bool Acquire(
        out IntPtr provider,
        string? containerName,
        string providerName,
        uint providerType,
        uint flags) =>
        CryptAcquireContextW(
            out provider,
            containerName,
            providerName,
            providerType,
            flags);

    public bool GetProviderParameter(
        IntPtr provider,
        uint parameter,
        byte[]? data,
        ref uint dataLength,
        uint flags) =>
        CryptGetProvParam(provider, parameter, data, ref dataLength, flags);

    public bool Release(IntPtr provider, uint flags) =>
        CryptReleaseContext(provider, flags);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptAcquireContextW(
        out IntPtr provider,
        string? containerName,
        string providerName,
        uint providerType,
        uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptGetProvParam(
        IntPtr provider,
        uint parameter,
        byte[]? data,
        ref uint dataLength,
        uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptReleaseContext(IntPtr provider, uint flags);
}

internal sealed class WindowsCspTokenEvidenceReader : IWindowsPsbcTokenEvidenceReader
{
    private const uint CryptVerifyContext = 0xF0000000;
    private const uint CryptSilent = 0x00000040;
    private const uint ProviderParameterEnumerateContainers = 2;
    private const uint ProviderParameterContainer = 6;
    private const uint CryptFirst = 1;
    private const int MaximumContainerNameBytes = 16 * 1024;
    private const int MaximumContainers = 64;
    private const int ErrorNoMoreItems = 259;

    private readonly IWindowsCryptographicProviderReader _providerReader;
    private readonly IWindowsCryptoProviderProbeNative _native;
    private readonly Func<string, uint, IReadOnlyList<string>> _readCertificateContainers;

    public WindowsCspTokenEvidenceReader(
        IWindowsCryptographicProviderReader providerReader)
        : this(providerReader, new WindowsCryptoProviderProbeNative())
    {
    }

    internal WindowsCspTokenEvidenceReader(
        IWindowsCryptographicProviderReader providerReader,
        IWindowsCryptoProviderProbeNative native,
        Func<string, uint, IReadOnlyList<string>>? readCertificateContainers = null)
    {
        _providerReader = providerReader ??
            throw new ArgumentNullException(nameof(providerReader));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _readCertificateContainers = readCertificateContainers ?? ReadCertificateContainers;
    }

    public PsbcTokenEvidence Query(PsbcTokenQuery query)
    {
        foreach (var providerName in
                 PsbcUsbKeyEnvironmentCatalog.GetProviderNames(query.VendorFamily))
        {
            if (!_providerReader.TryGetProviderType(providerName, out var providerType))
            {
                continue;
            }

            IntPtr provider = IntPtr.Zero;
            try
            {
                if (!_native.Acquire(
                        out provider,
                        containerName: null,
                        providerName,
                        providerType,
                        CryptVerifyContext | CryptSilent) ||
                    provider == IntPtr.Zero)
                {
                    continue;
                }

                var containers = ReadContainers(provider);
                if (containers is null || containers.Count == 0)
                {
                    continue;
                }

                var identities = new HashSet<string>(containers, StringComparer.Ordinal);
                foreach (var qualifiedName in _readCertificateContainers(providerName, providerType)
                             .Distinct(StringComparer.Ordinal)
                             .Take(MaximumContainers))
                {
                    var shortName = GetQualifiedContainerShortName(qualifiedName);
                    if (shortName is not null &&
                        containers.Contains(shortName) &&
                        VerifyQualifiedContainer(providerName, providerType, qualifiedName, shortName))
                    {
                        identities.Add(qualifiedName);
                    }
                }

                var ordered = identities.Order(StringComparer.Ordinal).ToArray();
                // Include every container/alias in the binding so a changed set invalidates it.
                var identity = ordered.Length == 1
                    ? ordered[0]
                    : "csp-containers:" + Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(string.Concat(
                            ordered.Select(value => $"{value.Length}:{value}")))));
                return new PsbcTokenEvidence(
                    IsPresent: true,
                    TokenIdentity: identity,
                    ContainerIdentities: Array.AsReadOnly(ordered));
            }
            finally
            {
                if (provider != IntPtr.Zero)
                {
                    try
                    {
                        _native.Release(provider, flags: 0);
                    }
                    catch
                    {
                    }
                }
            }
        }

        return new PsbcTokenEvidence(IsPresent: false);
    }

    private HashSet<string>? ReadContainers(IntPtr provider)
    {
        var containers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index <= MaximumContainers; index++)
        {
            var data = new byte[MaximumContainerNameBytes];
            var length = checked((uint)data.Length);
            if (!_native.GetProviderParameter(
                    provider, ProviderParameterEnumerateContainers, data, ref length,
                    index == 0 ? CryptFirst : 0))
            {
                return _native.GetLastError() == ErrorNoMoreItems ? containers : null;
            }

            var name = ReadContainerName(data, length);
            if (index == MaximumContainers || name is null || !containers.Add(name))
            {
                return null;
            }
        }

        return null;
    }

    private bool VerifyQualifiedContainer(
        string providerName, uint providerType, string qualifiedName, string shortName)
    {
        IntPtr provider = IntPtr.Zero;
        try
        {
            // Resolve the reader-qualified alias through the CSP, never by suffix alone.
            if (!_native.Acquire(
                    out provider, qualifiedName, providerName, providerType,
                    CryptVerifyContext | CryptSilent) ||
                provider == IntPtr.Zero)
            {
                return false;
            }

            var data = new byte[MaximumContainerNameBytes];
            var length = checked((uint)data.Length);
            return _native.GetProviderParameter(
                       provider, ProviderParameterContainer, data, ref length, flags: 0) &&
                   string.Equals(ReadContainerName(data, length), shortName, StringComparison.Ordinal);
        }
        finally
        {
            if (provider != IntPtr.Zero)
            {
                _native.Release(provider, flags: 0);
            }
        }
    }

    private static string? ReadContainerName(byte[] data, uint length)
    {
        if (length == 0 || length > data.Length)
        {
            return null;
        }

        var terminator = Array.IndexOf(data, (byte)0, 0, checked((int)length));
        if (terminator <= 0)
        {
            return null;
        }

        // CSP string parameters use the Windows ANSI code page, not .NET's UTF-8 default.
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var name = Marshal.PtrToStringAnsi(pinned.AddrOfPinnedObject(), terminator);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static string? GetQualifiedContainerShortName(string name)
    {
        if (!name.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        var separator = name.IndexOf('\\', 4);
        return separator > 4 &&
               separator < name.Length - 1 &&
               name.IndexOf('\\', separator + 1) < 0
            ? name[(separator + 1)..]
            : null;
    }

    private static IReadOnlyList<string> ReadCertificateContainers(
        string providerName, uint providerType)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var names = new List<string>();
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    var metadata = WindowsCertificateProviderMetadataReader.Instance.Read(certificate);
                    if (metadata.Failure == ClientCertificateProviderMetadataFailure.None &&
                        metadata.ProviderKind == ClientCertificateProviderKind.Csp &&
                        metadata.ProviderType == providerType &&
                        string.Equals(metadata.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(metadata.ContainerOrKeyName);
                    }
                }
            }

            return names;
        }
        catch (CryptographicException)
        {
            return [];
        }
    }
}
