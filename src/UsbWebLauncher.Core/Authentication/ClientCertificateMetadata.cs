namespace UsbWebLauncher.Core.Authentication;

public enum ClientCertificateProviderKind
{
    Unknown,
    Csp,
    Ksp
}

public enum ClientCertificateProviderMetadataSource
{
    None,
    WindowsCertificateKeyProviderInfoProperty,
    WindowsCurrentUserMyThumbprintMatch
}

public enum ClientCertificateProviderMetadataFailure
{
    None,
    PropertyNotFound,
    AccessDenied,
    InvalidPropertyData,
    NativeReadFailed,
    UnsupportedPlatform,
    StoreCertificateNotFound,
    StoreCertificateConflict,
    StoreReadFailed
}

public sealed record ClientCertificateEnhancedKeyUsage(
    string Oid,
    string FriendlyName);

public sealed record ClientCertificateProviderMetadata(
    ClientCertificateProviderKind ProviderKind,
    string ProviderName,
    uint? ProviderType,
    string ContainerOrKeyName,
    string KeySpec,
    ClientCertificateProviderMetadataSource Source,
    ClientCertificateProviderMetadataFailure Failure)
{
    public static ClientCertificateProviderMetadata Unavailable(
        ClientCertificateProviderMetadataFailure failure) =>
        new(
            ClientCertificateProviderKind.Unknown,
            string.Empty,
            null,
            string.Empty,
            string.Empty,
            ClientCertificateProviderMetadataSource.None,
            failure);
}
