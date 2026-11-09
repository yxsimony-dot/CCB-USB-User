using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

public sealed class WindowsCertificateProviderMetadataReader
{
    private const uint CertificateKeyProviderInfoPropertyId = 2;
    private const uint CertificateNCryptKeySpec = uint.MaxValue;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotFound = 1168;
    private const int CryptENotFound = unchecked((int)0x80092004);
    private const int MaximumPropertyBytes = 64 * 1024;
    private const int MaximumStringCharacters = 32 * 1024;

    public static WindowsCertificateProviderMetadataReader Instance { get; } = new();

    public ClientCertificateProviderMetadata Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!OperatingSystem.IsWindows())
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.UnsupportedPlatform);
        }

        try
        {
            return ReadProperty(certificate.Handle);
        }
        catch
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.NativeReadFailed);
        }
    }

    private static ClientCertificateProviderMetadata ReadProperty(IntPtr certificateContext)
    {
        uint propertySize = 0;
        if (!CertGetCertificateContextProperty(
                certificateContext,
                CertificateKeyProviderInfoPropertyId,
                IntPtr.Zero,
                ref propertySize))
        {
            return Failure(Marshal.GetLastWin32Error());
        }

        var structureSize = Marshal.SizeOf<CryptKeyProviderInfo>();
        if (propertySize < structureSize || propertySize > MaximumPropertyBytes)
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.InvalidPropertyData);
        }

        var buffer = Marshal.AllocHGlobal(checked((int)propertySize));
        try
        {
            if (!CertGetCertificateContextProperty(
                    certificateContext,
                    CertificateKeyProviderInfoPropertyId,
                    buffer,
                    ref propertySize))
            {
                return Failure(Marshal.GetLastWin32Error());
            }

            if (propertySize < structureSize || propertySize > MaximumPropertyBytes)
            {
                return ClientCertificateProviderMetadata.Unavailable(
                    ClientCertificateProviderMetadataFailure.InvalidPropertyData);
            }

            var info = Marshal.PtrToStructure<CryptKeyProviderInfo>(buffer);
            if (!TryReadPropertyString(
                    buffer,
                    propertySize,
                    info.ContainerName,
                    out var containerName) ||
                !TryReadPropertyString(
                    buffer,
                    propertySize,
                    info.ProviderName,
                    out var providerName))
            {
                return ClientCertificateProviderMetadata.Unavailable(
                    ClientCertificateProviderMetadataFailure.InvalidPropertyData);
            }

            return CreateProviderMetadata(
                providerName,
                info.ProviderType,
                containerName,
                info.KeySpec);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static ClientCertificateProviderMetadata CreateProviderMetadata(
        string providerName,
        uint providerType,
        string containerName,
        uint keySpec)
    {
        var providerKind = providerType == 0 || keySpec == CertificateNCryptKeySpec
            ? ClientCertificateProviderKind.Ksp
            : ClientCertificateProviderKind.Csp;
        return new ClientCertificateProviderMetadata(
            providerKind,
            providerName,
            providerType,
            containerName,
            FormatKeySpec(keySpec),
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
            ClientCertificateProviderMetadataFailure.None);
    }

    private static ClientCertificateProviderMetadata Failure(int error) =>
        ClientCertificateProviderMetadata.Unavailable(error switch
        {
            CryptENotFound or ErrorNotFound => ClientCertificateProviderMetadataFailure.PropertyNotFound,
            ErrorAccessDenied => ClientCertificateProviderMetadataFailure.AccessDenied,
            _ => ClientCertificateProviderMetadataFailure.NativeReadFailed
        });

    private static string FormatKeySpec(uint keySpec) => keySpec switch
    {
        1 => "AT_KEYEXCHANGE",
        2 => "AT_SIGNATURE",
        CertificateNCryptKeySpec => "CERT_NCRYPT_KEY_SPEC",
        _ => keySpec.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static bool TryReadPropertyString(
        IntPtr buffer,
        uint propertySize,
        IntPtr value,
        out string result)
    {
        result = string.Empty;
        if (value == IntPtr.Zero)
        {
            return true;
        }

        var start = buffer.ToInt64();
        var end = checked(start + propertySize);
        var current = value.ToInt64();
        if (current < start || current >= end || (current - start) % sizeof(char) != 0)
        {
            return false;
        }

        var availableCharacters = Math.Min(
            (end - current) / sizeof(char),
            MaximumStringCharacters);
        var length = 0;
        while (length < availableCharacters)
        {
            if (Marshal.ReadInt16(value, checked(length * sizeof(char))) == 0)
            {
                result = Marshal.PtrToStringUni(value, length) ?? string.Empty;
                return true;
            }

            length++;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptKeyProviderInfo
    {
        public IntPtr ContainerName;
        public IntPtr ProviderName;
        public uint ProviderType;
        public uint Flags;
        public uint ProviderParameterCount;
        public IntPtr ProviderParameters;
        public uint KeySpec;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertGetCertificateContextProperty(
        IntPtr certificateContext,
        uint propertyId,
        IntPtr data,
        ref uint dataSize);
}
