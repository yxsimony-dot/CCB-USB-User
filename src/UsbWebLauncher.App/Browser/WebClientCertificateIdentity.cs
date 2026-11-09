using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.App.Browser;

internal readonly record struct WebClientCertificateIdentity(
    string Sha1Fingerprint,
    string Subject,
    string Issuer,
    string SubjectDistinguishedName,
    string IssuerDistinguishedName,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc,
    string SerialNumber,
    IReadOnlyList<ClientCertificateEnhancedKeyUsage> EnhancedKeyUsages,
    ClientCertificateProviderMetadata ProviderMetadata)
{
    public static WebClientCertificateIdentity FromCertificate(
        X509Certificate2 certificate) =>
        FromCertificate(
            certificate,
            WindowsCertificateProviderMetadataResolver.Instance.Read(certificate));

    public static WebClientCertificateIdentity FromCertificate(
        X509Certificate2 certificate,
        ClientCertificateProviderMetadata providerMetadata)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(providerMetadata);

        return new(
            certificate.Thumbprint ?? string.Empty,
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            certificate.SubjectName.Name ?? certificate.Subject,
            certificate.IssuerName.Name ?? certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            certificate.SerialNumber ?? string.Empty,
            ReadEnhancedKeyUsages(certificate),
            providerMetadata);
    }

    public ClientCertificateCandidate ToCandidate() => new(
        Sha1Fingerprint,
        Subject,
        Issuer,
        SubjectDistinguishedName,
        IssuerDistinguishedName,
        ValidFromUtc,
        ValidToUtc,
        SerialNumber,
        EnhancedKeyUsages,
        ProviderMetadata);

    private static IReadOnlyList<ClientCertificateEnhancedKeyUsage> ReadEnhancedKeyUsages(
        X509Certificate2 certificate)
    {
        const string enhancedKeyUsageOid = "2.5.29.37";
        var extension = certificate.Extensions
            .FirstOrDefault(item => string.Equals(
                item.Oid?.Value,
                enhancedKeyUsageOid,
                StringComparison.Ordinal));
        if (extension is null)
        {
            return [];
        }

        try
        {
            var usages = extension as X509EnhancedKeyUsageExtension ??
                new X509EnhancedKeyUsageExtension(extension, extension.Critical);
            return usages.EnhancedKeyUsages
                .Cast<Oid>()
                .Select(usage => new ClientCertificateEnhancedKeyUsage(
                    usage.Value ?? string.Empty,
                    usage.FriendlyName ?? string.Empty))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
