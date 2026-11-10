using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Core.Authentication;

public enum PsbcClientCertificateCorrelationStatus
{
    Matched,
    KeyNotReady,
    VendorEvidencePending,
    TokenEvidenceUnavailable,
    ProviderMetadataUnavailable,
    ProviderKindMismatch,
    ProviderNameMismatch,
    ProviderTypeMismatch,
    KeySpecMismatch,
    ContainerMismatch
}

public sealed record PsbcClientCertificateCorrelationResult(
    PsbcClientCertificateCorrelationStatus Status)
{
    public bool IsMatch => Status == PsbcClientCertificateCorrelationStatus.Matched;
}

public static class PsbcClientCertificateCorrelationPolicy
{
    private const string TendyronProviderName =
        "Tendyron OnKey193 CSP PSBC v1.0";
    private const uint TendyronProviderType = 1;
    private const string TendyronKeySpec = "AT_SIGNATURE";

    public static PsbcClientCertificateCorrelationResult Correlate(
        UsbKeyDetectionResult key,
        ClientCertificateCandidate certificate)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(certificate);

        if (key.Status != UsbKeyDetectionStatus.Ready ||
            key.VendorFamily is null)
        {
            return Result(PsbcClientCertificateCorrelationStatus.KeyNotReady);
        }

        if (key.VendorFamily != UsbKeyVendorFamily.Tendyron)
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.VendorEvidencePending);
        }

        var tokenIdentity = NormalizeContainerIdentity(key.TokenIdentity);
        if (tokenIdentity.Length == 0)
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.TokenEvidenceUnavailable);
        }

        var provider = certificate.ProviderMetadata;
        if (provider.Failure != ClientCertificateProviderMetadataFailure.None ||
            provider.Source == ClientCertificateProviderMetadataSource.None)
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.ProviderMetadataUnavailable);
        }

        if (provider.ProviderKind != ClientCertificateProviderKind.Csp)
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.ProviderKindMismatch);
        }

        if (!string.Equals(
                ClientCertificateAuthenticationOptions.Normalize(provider.ProviderName),
                TendyronProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.ProviderNameMismatch);
        }

        if (provider.ProviderType != TendyronProviderType)
        {
            return Result(
                PsbcClientCertificateCorrelationStatus.ProviderTypeMismatch);
        }

        if (!string.Equals(
                ClientCertificateAuthenticationOptions.NormalizeKeySpec(provider.KeySpec),
                TendyronKeySpec,
                StringComparison.Ordinal))
        {
            return Result(PsbcClientCertificateCorrelationStatus.KeySpecMismatch);
        }

        var containerIdentity = NormalizeContainerIdentity(provider.ContainerOrKeyName);
        var knownContainers = key.ContainerIdentities ?? [tokenIdentity];
        if (containerIdentity.Length == 0 ||
            !knownContainers.Any(identity => string.Equals(
                NormalizeContainerIdentity(identity),
                containerIdentity,
                StringComparison.Ordinal)))
        {
            return Result(PsbcClientCertificateCorrelationStatus.ContainerMismatch);
        }

        return Result(PsbcClientCertificateCorrelationStatus.Matched);
    }

    private static string NormalizeContainerIdentity(string? value) =>
        ClientCertificateAuthenticationOptions.Normalize(value ?? string.Empty);

    private static PsbcClientCertificateCorrelationResult Result(
        PsbcClientCertificateCorrelationStatus status) =>
        new(status);
}
