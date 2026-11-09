using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Core.Authentication;

public enum PsbcClientCertificateSelectionStatus
{
    Selected,
    AuthenticationUnavailable,
    InvalidRequest,
    UnauthorizedEndpoint,
    WrongBank,
    StaleRuntime,
    KeyNotReady,
    CertificateMissing,
    CertificateConflict
}

public sealed record PsbcClientCertificateSelectionContext(
    bool IsPostalSavingsBank,
    long SessionId,
    long Revision,
    long CurrentSessionId,
    long CurrentRevision,
    UsbKeyDetectionResult? Key,
    DateTimeOffset UtcNow,
    bool IsSessionCurrent = true,
    bool OwnsCurrentRuntime = true);

public sealed record PsbcClientCertificateSelectionResult(
    PsbcClientCertificateSelectionStatus Status,
    ClientCertificateCandidate? SelectedCertificate = null)
{
    public bool IsSelected =>
        Status == PsbcClientCertificateSelectionStatus.Selected &&
        SelectedCertificate is not null;
}

public static class PsbcClientCertificateSelector
{
    private const string ClientAuthenticationEnhancedKeyUsageOid =
        "1.3.6.1.5.5.7.3.2";

    public static PsbcClientCertificateSelectionResult Select(
        ClientCertificateRequestSnapshot request,
        ClientCertificateAuthenticationOptions authentication,
        PsbcClientCertificateSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(context);

        if (!authentication.Enabled ||
            !authentication.UsesBrowserNativeAuthentication)
        {
            return Result(
                PsbcClientCertificateSelectionStatus.AuthenticationUnavailable);
        }

        if (!IsValidRequest(request))
        {
            return Result(PsbcClientCertificateSelectionStatus.InvalidRequest);
        }

        if (!IsAuthorizedEndpoint(request.RequestUri, authentication))
        {
            return Result(
                PsbcClientCertificateSelectionStatus.UnauthorizedEndpoint);
        }

        if (!context.IsPostalSavingsBank)
        {
            return Result(PsbcClientCertificateSelectionStatus.WrongBank);
        }

        if (!IsCurrentRuntime(context))
        {
            return Result(PsbcClientCertificateSelectionStatus.StaleRuntime);
        }

        if (context.Key is not
            {
                Status: UsbKeyDetectionStatus.Ready,
                VendorFamily: not null
            } key ||
            string.IsNullOrWhiteSpace(key.PhysicalInstanceId) ||
            string.IsNullOrWhiteSpace(key.TokenIdentity))
        {
            return Result(PsbcClientCertificateSelectionStatus.KeyNotReady);
        }

        var utcNow = context.UtcNow.ToUniversalTime();
        var matches = request.AvailableCertificates
            .Where(certificate =>
                IsValidClientAuthenticationCertificate(certificate, utcNow) &&
                PsbcClientCertificateCorrelationPolicy
                    .Correlate(key, certificate)
                    .IsMatch)
            .ToArray();

        return matches.Length switch
        {
            0 => Result(PsbcClientCertificateSelectionStatus.CertificateMissing),
            1 => new(
                PsbcClientCertificateSelectionStatus.Selected,
                matches[0]),
            _ => Result(PsbcClientCertificateSelectionStatus.CertificateConflict)
        };
    }

    private static bool IsValidRequest(
        ClientCertificateRequestSnapshot request) =>
        !request.IsProxyRequest &&
        request.RequestUri.IsAbsoluteUri &&
        string.Equals(
            request.RequestUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizedEndpoint(
        Uri requestUri,
        ClientCertificateAuthenticationOptions authentication)
    {
        var requestHost =
            ClientCertificateAuthenticationOptions.NormalizeHost(requestUri.Host);
        if (MatchesEndpoint(
                requestHost,
                requestUri.Port,
                authentication.Host,
                authentication.Port))
        {
            return true;
        }

        return authentication.AdditionalEndpoints?.Any(endpoint =>
            endpoint is not null &&
            MatchesEndpoint(
                requestHost,
                requestUri.Port,
                endpoint.Host,
                endpoint.Port)) == true;
    }

    private static bool MatchesEndpoint(
        string requestHost,
        int requestPort,
        string configuredHost,
        int configuredPort) =>
        string.Equals(
            requestHost,
            ClientCertificateAuthenticationOptions.NormalizeHost(configuredHost),
            StringComparison.Ordinal) &&
        requestPort == configuredPort;

    private static bool IsCurrentRuntime(
        PsbcClientCertificateSelectionContext context) =>
        context.IsSessionCurrent &&
        context.OwnsCurrentRuntime &&
        context.SessionId > 0 &&
        context.Revision > 0 &&
        context.SessionId == context.CurrentSessionId &&
        context.Revision == context.CurrentRevision;

    private static bool IsValidClientAuthenticationCertificate(
        ClientCertificateCandidate certificate,
        DateTimeOffset utcNow) =>
        certificate.ValidFromUtc is { } validFrom &&
        certificate.ValidToUtc is { } validTo &&
        validFrom <= utcNow &&
        validTo >= utcNow &&
        certificate.EnhancedKeyUsages.Any(usage =>
            string.Equals(
                usage.Oid,
                ClientAuthenticationEnhancedKeyUsageOid,
                StringComparison.Ordinal));

    private static PsbcClientCertificateSelectionResult Result(
        PsbcClientCertificateSelectionStatus status) =>
        new(status);
}
