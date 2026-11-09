using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UsbWebLauncher.Core.Devices;

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
    private const uint CryptFirst = 1;
    private const int MaximumContainerNameBytes = 16 * 1024;

    private readonly IWindowsCryptographicProviderReader _providerReader;
    private readonly IWindowsCryptoProviderProbeNative _native;

    public WindowsCspTokenEvidenceReader(
        IWindowsCryptographicProviderReader providerReader)
        : this(providerReader, new WindowsCryptoProviderProbeNative())
    {
    }

    internal WindowsCspTokenEvidenceReader(
        IWindowsCryptographicProviderReader providerReader,
        IWindowsCryptoProviderProbeNative native)
    {
        _providerReader = providerReader ??
            throw new ArgumentNullException(nameof(providerReader));
        _native = native ?? throw new ArgumentNullException(nameof(native));
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

                uint containerLength = 0;
                if (!_native.GetProviderParameter(
                        provider,
                        ProviderParameterEnumerateContainers,
                        data: null,
                        ref containerLength,
                        CryptFirst) ||
                    containerLength is 0 or > MaximumContainerNameBytes)
                {
                    continue;
                }

                var container = new byte[checked((int)containerLength)];
                if (!_native.GetProviderParameter(
                        provider,
                        ProviderParameterEnumerateContainers,
                        container,
                        ref containerLength,
                        CryptFirst))
                {
                    continue;
                }

                var usedLength = Math.Min(container.Length, checked((int)containerLength));
                var terminator = Array.IndexOf(container, (byte)0, 0, usedLength);
                var identityLength = terminator >= 0 ? terminator : usedLength;
                var identity = Encoding.Default.GetString(container, 0, identityLength);
                return new PsbcTokenEvidence(
                    IsPresent: true,
                    TokenIdentity: string.IsNullOrWhiteSpace(identity) ? null : identity);
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
}
