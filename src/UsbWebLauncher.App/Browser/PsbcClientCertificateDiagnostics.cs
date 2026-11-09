using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbWebLauncher.App.Browser;

internal sealed class PsbcClientCertificateDiagnostics
{
    private const long MaximumFileBytes = 1024 * 1024;
    private readonly object sync = new();
    private readonly string path;
    private readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public PsbcClientCertificateDiagnostics(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "PSBC certificate diagnostics require an absolute output path.",
                nameof(path));
        }

        this.path = Path.GetFullPath(path);
    }

    internal string PathName => path;

    public void BeginRun()
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            catch
            {
                // Diagnostics must not change authentication or UI behavior.
            }
        }
    }

    public void DeleteOutput()
    {
        lock (sync)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Diagnostics cleanup must not change authentication or UI behavior.
            }
        }
    }

    public void Write(WebClientCertificateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidates = request.AvailableCertificates
            .Select(certificate => certificate.Candidate)
            .Select(candidate => new
            {
                candidate.Sha1Fingerprint,
                candidate.Subject,
                candidate.Issuer,
                candidate.SubjectDistinguishedName,
                candidate.IssuerDistinguishedName,
                candidate.ValidFromUtc,
                candidate.ValidToUtc,
                candidate.SerialNumber,
                candidate.EnhancedKeyUsages,
                provider = candidate.ProviderMetadata
            })
            .ToArray();
        var endpoint = new
        {
            scheme = request.RequestUri.Scheme,
            host = request.RequestUri.IdnHost,
            port = request.RequestUri.Port,
            authority = string.Concat(
                request.RequestUri.Scheme,
                "://",
                request.RequestUri.IdnHost,
                ":",
                request.RequestUri.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            request.IsProxyRequest
        };
        var payload = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            containsCertificateIdentityData = true,
            secretDataIncluded = false,
            endpoint,
            candidates
        };

        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumFileBytes)
                {
                    return;
                }

                File.AppendAllText(
                    path,
                    JsonSerializer.Serialize(payload, serializerOptions) +
                    Environment.NewLine);
            }
            catch
            {
                // Diagnostics must not change authentication or UI behavior.
            }
        }
    }
}
