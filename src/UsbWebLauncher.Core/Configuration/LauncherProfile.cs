using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UsbWebLauncher.Core.Configuration;

public sealed record LauncherProfile(
    string TargetUrl,
    UsbMatchOptions? UsbDevice = null,
    ClientCertificateAuthenticationOptions? ClientCertificateAuthentication = null,
    IReadOnlyList<UsbMatchOptions>? AdditionalUsbDevices = null,
    string UsbDetectionStrategy = "legacyAllowlist")
{
    public IEnumerable<UsbMatchOptions> GetUsbDeviceAllowlist()
    {
        if (UsbDevice is not null)
        {
            yield return UsbDevice;
        }

        if (AdditionalUsbDevices is null)
        {
            yield break;
        }

        foreach (var device in AdditionalUsbDevices)
        {
            if (device is not null)
            {
                yield return device;
            }
        }
    }
}

public sealed record UsbMatchOptions(
    string VendorId,
    string ProductId,
    string BusReportedDescription,
    string? CdRomName = null,
    string? VolumeLabel = null,
    string? FileSystem = null);

public sealed record ClientCertificateAuthenticationOptions(
    string Id,
    string Host,
    int Port,
    ClientCertificateIdentityOptions Certificate,
    ClientCertificateCryptographicProviderOptions CryptographicProvider,
    string CredentialTargetName,
    ClientCertificateTokenUnlockOptions? TokenUnlock = null,
    ClientCertificateNativePromptOptions? NativePromptContinuation = null,
    IReadOnlyList<ClientCertificateEndpointOptions>? AdditionalEndpoints = null,
    string AuthenticationMode = "ConfiguredToken")
{
    public bool UsesBrowserNativeAuthentication => AuthenticationMode == "BrowserNative";

    public string GetNormalizedHash()
    {
        var fields = UsesBrowserNativeAuthentication ? new List<string>
        {
            "BrowserNative",
            Normalize(Id),
            NormalizeHost(Host),
            Port.ToString(CultureInfo.InvariantCulture),
            Normalize(CredentialTargetName)
        } : new List<string>
        {
            Normalize(Id),
            NormalizeHost(Host),
            Port.ToString(CultureInfo.InvariantCulture),
            NormalizeFingerprint(Certificate.Sha1Fingerprint),
            Normalize(Certificate.Subject),
            Normalize(Certificate.Issuer),
            Normalize(CryptographicProvider.CspName),
            CryptographicProvider.ProviderType.ToString(CultureInfo.InvariantCulture),
            Normalize(CryptographicProvider.ContainerName),
            NormalizeKeySpec(CryptographicProvider.KeySpec),
            Normalize(CredentialTargetName)
        };

        if (!UsesBrowserNativeAuthentication && TokenUnlock is not null &&
            NormalizeUnlockMethod(TokenUnlock.Method) != "CRYPTOAPI")
        {
            fields.Add(NormalizeUnlockMethod(TokenUnlock.Method));
            fields.Add(NormalizePath(TokenUnlock.ModulePath));
            fields.Add(Normalize(TokenUnlock.TokenSerialNumber ?? string.Empty));
        }

        var additionalEndpoints = AdditionalEndpoints?
            .OfType<ClientCertificateEndpointOptions>()
            .Select(endpoint => new ClientCertificateEndpointOptions(
                NormalizeHost(endpoint.Host),
                endpoint.Port))
            .OrderBy(endpoint => endpoint.Host, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Port)
            .ToArray() ?? [];

        if (NativePromptContinuation is null && additionalEndpoints.Length == 0)
        {
            var legacyCanonicalRepresentation = string.Join("\n", fields);
            return HashCanonicalRepresentation(legacyCanonicalRepresentation);
        }

        var structuredRepresentation = new StringBuilder();
        foreach (var field in fields)
        {
            AppendLengthPrefixedField(structuredRepresentation, field);
        }

        if (additionalEndpoints.Length > 0)
        {
            AppendLengthPrefixedField(structuredRepresentation, "AdditionalEndpoints");
            AppendLengthPrefixedField(
                structuredRepresentation,
                additionalEndpoints.Length.ToString(CultureInfo.InvariantCulture));

            foreach (var endpoint in additionalEndpoints)
            {
                AppendLengthPrefixedField(structuredRepresentation, endpoint.Host);
                AppendLengthPrefixedField(
                    structuredRepresentation,
                    endpoint.Port.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (NativePromptContinuation is null)
        {
            return HashCanonicalRepresentation(structuredRepresentation.ToString());
        }

        AppendLengthPrefixedField(structuredRepresentation, NormalizeBool(NativePromptContinuation.Enabled));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.BrokerPipeName));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.PolicyId));
        AppendLengthPrefixedField(structuredRepresentation, NormalizePath(NativePromptContinuation.ProcessPath));
        AppendLengthPrefixedField(structuredRepresentation, NormalizeFingerprint(NativePromptContinuation.ProcessSha256));
        AppendLengthPrefixedField(structuredRepresentation, NormalizeFingerprint(NativePromptContinuation.PublisherCertificateThumbprint));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.WindowTitle));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.WindowClass));
        foreach (var childWindowClass in NativePromptContinuation.RequiredChildWindowClasses)
        {
            AppendLengthPrefixedField(structuredRepresentation, Normalize(childWindowClass));
        }

        AppendLengthPrefixedField(
            structuredRepresentation,
            NativePromptContinuation.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.WindowScope));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.ProcessFileName ?? string.Empty));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.TrustedRootWindowTitle ?? string.Empty));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.TrustedRootWindowClassPrefix ?? string.Empty));
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.InputWindowClass ?? string.Empty));
        AppendLengthPrefixedField(
            structuredRepresentation,
            NativePromptContinuation.InputControlId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        AppendLengthPrefixedField(structuredRepresentation, Normalize(NativePromptContinuation.SubmitWindowClass ?? string.Empty));
        AppendLengthPrefixedField(
            structuredRepresentation,
            NativePromptContinuation.SubmitControlId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        return HashCanonicalRepresentation(structuredRepresentation.ToString());
    }

    internal static string NormalizeHost(string host) => Normalize(host).ToLowerInvariant();

    internal static string NormalizeFingerprint(string fingerprint) =>
        string.Concat(Normalize(fingerprint).Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    internal static string NormalizeKeySpec(string keySpec) => Normalize(keySpec).ToUpperInvariant();

    internal static string NormalizeUnlockMethod(string method) => Normalize(method).ToUpperInvariant();

    internal static string NormalizePath(string? path) =>
        Normalize(path ?? string.Empty).Replace('/', '\\').ToUpperInvariant();

    internal static string NormalizeBool(bool value) => value ? "True" : "False";

    internal static string Normalize(string value) => value.Trim();

    private static string HashCanonicalRepresentation(string canonicalRepresentation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRepresentation)));

    private static void AppendLengthPrefixedField(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }
}

public sealed record ClientCertificateEndpointOptions(
    string Host,
    int Port);

public sealed record ClientCertificateIdentityOptions(
    string Sha1Fingerprint,
    string Subject,
    string Issuer);

public sealed record ClientCertificateCryptographicProviderOptions(
    string CspName,
    int ProviderType,
    string ContainerName,
    string KeySpec);

public sealed record ClientCertificateTokenUnlockOptions(
    string Method,
    string? ModulePath = null,
    string? TokenSerialNumber = null);

public sealed record ClientCertificateNativePromptOptions(
    bool Enabled,
    string BrokerPipeName,
    string PolicyId,
    string ProcessPath,
    string ProcessSha256,
    string PublisherCertificateThumbprint,
    string WindowTitle,
    string WindowClass,
    IReadOnlyList<string> RequiredChildWindowClasses,
    int TimeoutMilliseconds,
    string WindowScope = NativePromptWindowScopes.TopLevelProcess,
    string? ProcessFileName = null,
    string? TrustedRootWindowTitle = null,
    string? TrustedRootWindowClassPrefix = null,
    string? InputWindowClass = null,
    int? InputControlId = null,
    string? SubmitWindowClass = null,
    int? SubmitControlId = null);

public static class NativePromptWindowScopes
{
    public const string TopLevelProcess = "TopLevelProcess";
    public const string TrustedClientDescendant = "TrustedClientDescendant";
    public const string TrustedClientTopLevel = "TrustedClientTopLevel";
}
