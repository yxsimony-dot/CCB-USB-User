using System.Security.Cryptography.X509Certificates;

namespace UsbWebLauncher.App.Browser;

internal readonly record struct WebClientCertificateIdentity(
    string Sha1Fingerprint,
    string Subject,
    string Issuer)
{
    public static WebClientCertificateIdentity FromCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return new(
            certificate.Thumbprint ?? string.Empty,
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
    }
}
