using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

public sealed class WindowsCertificateProviderMetadataResolver
{
    private readonly Func<X509Certificate2, ClientCertificateProviderMetadata> readDirect;
    private readonly Func<string, IReadOnlyList<ClientCertificateProviderMetadata>> readStoreMatches;

    public static WindowsCertificateProviderMetadataResolver Instance { get; } = new(
        WindowsCertificateProviderMetadataReader.Instance.Read,
        ReadCurrentUserMyMatches);

    internal WindowsCertificateProviderMetadataResolver(
        Func<X509Certificate2, ClientCertificateProviderMetadata> readDirect,
        Func<string, IReadOnlyList<ClientCertificateProviderMetadata>> readStoreMatches)
    {
        this.readDirect = readDirect ?? throw new ArgumentNullException(nameof(readDirect));
        this.readStoreMatches = readStoreMatches ??
            throw new ArgumentNullException(nameof(readStoreMatches));
    }

    public ClientCertificateProviderMetadata Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var direct = readDirect(certificate);
        if (direct.Failure != ClientCertificateProviderMetadataFailure.PropertyNotFound)
        {
            return direct;
        }

        var fingerprint = NormalizeFingerprint(certificate.Thumbprint);
        if (fingerprint.Length == 0)
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.StoreCertificateNotFound);
        }

        IReadOnlyList<ClientCertificateProviderMetadata> matches;
        try
        {
            matches = readStoreMatches(fingerprint);
        }
        catch
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.StoreReadFailed);
        }

        if (matches.Count == 0)
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.StoreCertificateNotFound);
        }

        if (matches.Count != 1)
        {
            return ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.StoreCertificateConflict);
        }

        return matches[0] with
        {
            Source = ClientCertificateProviderMetadataSource.WindowsCurrentUserMyThumbprintMatch
        };
    }

    private static IReadOnlyList<ClientCertificateProviderMetadata> ReadCurrentUserMyMatches(
        string fingerprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates
            .Cast<X509Certificate2>()
            .Where(certificate => string.Equals(
                NormalizeFingerprint(certificate.Thumbprint),
                fingerprint,
                StringComparison.Ordinal))
            .Select(WindowsCertificateProviderMetadataReader.Instance.Read)
            .ToArray();
    }

    private static string NormalizeFingerprint(string? fingerprint) =>
        string.Concat((fingerprint ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character))).ToUpperInvariant();
}
