using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Authentication;

public sealed record ClientCertificateRequestSnapshot
{
    public ClientCertificateRequestSnapshot(
        Uri requestUri,
        bool isProxyRequest,
        IEnumerable<ClientCertificateCandidate> availableCertificates)
        : this(requestUri, isProxyRequest, availableCertificates, null)
    {
    }

    public ClientCertificateRequestSnapshot(
        Uri requestUri,
        bool isProxyRequest,
        IEnumerable<ClientCertificateCandidate> availableCertificates,
        long? browserOperationId)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(availableCertificates);

        RequestUri = requestUri;
        IsProxyRequest = isProxyRequest;
        AvailableCertificates = availableCertificates.ToArray();
        BrowserOperationId = browserOperationId;
    }

    public Uri RequestUri { get; }

    public bool IsProxyRequest { get; }

    public IReadOnlyList<ClientCertificateCandidate> AvailableCertificates { get; }

    public long? BrowserOperationId { get; }
}

public sealed record ClientCertificateCandidate
{
    public ClientCertificateCandidate(
        string sha1Fingerprint,
        string subject,
        string issuer)
    {
        Sha1Fingerprint = ClientCertificateAuthenticationOptions.NormalizeFingerprint(sha1Fingerprint);
        Subject = ClientCertificateAuthenticationOptions.Normalize(subject);
        Issuer = ClientCertificateAuthenticationOptions.Normalize(issuer);
    }

    public string Sha1Fingerprint { get; }

    public string Subject { get; }

    public string Issuer { get; }

    internal bool Matches(ClientCertificateIdentityOptions identity) =>
        Sha1Fingerprint == ClientCertificateAuthenticationOptions.NormalizeFingerprint(identity.Sha1Fingerprint) &&
        Subject == ClientCertificateAuthenticationOptions.Normalize(identity.Subject) &&
        Issuer == ClientCertificateAuthenticationOptions.Normalize(identity.Issuer);
}

public enum ClientCertificateRequestMatchStatus
{
    Selected,
    UnauthorizedTarget,
    TokenNotUnlocked,
    CertificateMissing,
    CertificateConflict,
    InvalidRequest
}

public sealed record ClientCertificateRequestMatchResult(
    ClientCertificateRequestMatchStatus Status,
    ClientCertificateCandidate? SelectedCertificate = null,
    AuthenticationAttempt? Attempt = null)
{
    public static ClientCertificateRequestMatchResult Selected(ClientCertificateCandidate certificate) =>
        new(ClientCertificateRequestMatchStatus.Selected, certificate);

    public static ClientCertificateRequestMatchResult UnauthorizedTarget() =>
        new(ClientCertificateRequestMatchStatus.UnauthorizedTarget);

    public static ClientCertificateRequestMatchResult TokenNotUnlocked() =>
        new(ClientCertificateRequestMatchStatus.TokenNotUnlocked);

    public static ClientCertificateRequestMatchResult CertificateMissing() =>
        new(ClientCertificateRequestMatchStatus.CertificateMissing);

    public static ClientCertificateRequestMatchResult CertificateConflict() =>
        new(ClientCertificateRequestMatchStatus.CertificateConflict);

    public static ClientCertificateRequestMatchResult InvalidRequest() =>
        new(ClientCertificateRequestMatchStatus.InvalidRequest);
}
