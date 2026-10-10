using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Authentication;

public static class ClientCertificateRequestMatcher
{
    public static ClientCertificateRequestMatchResult Match(
        ClientCertificateRequestSnapshot request,
        ClientCertificateAuthenticationOptions authentication,
        bool isTokenUnlocked)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authentication);

        if (!IsValidRequest(request))
        {
            return ClientCertificateRequestMatchResult.InvalidRequest();
        }

        if (!IsAuthorizedTarget(request.RequestUri, authentication))
        {
            return ClientCertificateRequestMatchResult.UnauthorizedTarget();
        }

        if (!isTokenUnlocked)
        {
            return ClientCertificateRequestMatchResult.TokenNotUnlocked();
        }

        var matches = authentication.UsesBrowserNativeAuthentication
            ? request.AvailableCertificates.ToArray()
            : request.AvailableCertificates
                .Where(certificate => certificate.Matches(authentication.Certificate))
                .ToArray();

        return matches.Length switch
        {
            0 => ClientCertificateRequestMatchResult.CertificateMissing(),
            1 => ClientCertificateRequestMatchResult.Selected(matches[0]),
            _ => ClientCertificateRequestMatchResult.CertificateConflict()
        };
    }

    private static bool IsValidRequest(ClientCertificateRequestSnapshot request) =>
        !request.IsProxyRequest &&
        request.RequestUri.IsAbsoluteUri &&
        string.Equals(request.RequestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizedTarget(
        Uri requestUri,
        ClientCertificateAuthenticationOptions authentication)
    {
        var requestHost = ClientCertificateAuthenticationOptions.NormalizeHost(requestUri.Host);
        if (MatchesEndpoint(requestHost, requestUri.Port, authentication.Host, authentication.Port))
        {
            return true;
        }

        return authentication.AdditionalEndpoints?.Any(endpoint =>
            endpoint is not null &&
            MatchesEndpoint(requestHost, requestUri.Port, endpoint.Host, endpoint.Port)) == true;
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
}
