// All certificate identities, provider names and secret sentinels are synthetic fixtures.
using System.Security.Cryptography;
using System.Text.Json;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PsbcClientCertificateDiagnosticsTests
{
    private const string SyntheticPin = "SYNTHETIC_PIN_SECRET";
    private const string SyntheticCredentialManagerSecret =
        "SYNTHETIC_CREDENTIAL_MANAGER_SECRET";

    [Fact]
    public void Write_EmitsEndpointPublicCertificateAndCspKspMetadataWithoutSecrets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcCertificateDiagnosticsTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "psbc-certificates.jsonl");
        var privateKeyBytes = RandomNumberGenerator.GetBytes(96);
        try
        {
            var nativeCertificateWithSecrets = new
            {
                Pin = SyntheticPin,
                CredentialManager = SyntheticCredentialManagerSecret,
                PrivateKey = Convert.ToBase64String(privateKeyBytes)
            };
            var diagnostics = new PsbcClientCertificateDiagnostics(path);
            diagnostics.BeginRun();
            var request = new WebClientCertificateRequest(
                new Uri("https://corpebank.psbc.com:443/"),
                false,
                [
                    CreateCertificate(
                        "1111111111111111111111111111111111111111",
                        ClientCertificateProviderKind.Csp,
                        "Synthetic PSBC CSP",
                        1,
                        "SYNTHETIC_CSP_CONTAINER",
                        "AT_KEYEXCHANGE",
                        nativeCertificateWithSecrets),
                    CreateCertificate(
                        "2222222222222222222222222222222222222222",
                        ClientCertificateProviderKind.Ksp,
                        "Synthetic PSBC KSP",
                        0,
                        "SYNTHETIC_KSP_KEY",
                        "CERT_NCRYPT_KEY_SPEC",
                        new object()),
                    CreateUnavailableCertificate(
                        "3333333333333333333333333333333333333333")
                ]);

            diagnostics.Write(request);

            var output = File.ReadAllText(path);
            using var document = JsonDocument.Parse(output);
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(rootElement.GetProperty("containsCertificateIdentityData").GetBoolean());
            Assert.False(rootElement.GetProperty("secretDataIncluded").GetBoolean());
            var endpoint = rootElement.GetProperty("endpoint");
            Assert.Equal("https", endpoint.GetProperty("scheme").GetString());
            Assert.Equal("corpebank.psbc.com", endpoint.GetProperty("host").GetString());
            Assert.Equal(443, endpoint.GetProperty("port").GetInt32());
            Assert.Equal(
                "https://corpebank.psbc.com:443",
                endpoint.GetProperty("authority").GetString());
            Assert.False(endpoint.GetProperty("isProxyRequest").GetBoolean());

            var candidates = rootElement.GetProperty("candidates");
            Assert.Equal(3, candidates.GetArrayLength());
            AssertCandidate(
                candidates[0],
                "Csp",
                "Synthetic PSBC CSP",
                1,
                "SYNTHETIC_CSP_CONTAINER",
                "AT_KEYEXCHANGE");
            AssertCandidate(
                candidates[1],
                "Ksp",
                "Synthetic PSBC KSP",
                0,
                "SYNTHETIC_KSP_KEY",
                "CERT_NCRYPT_KEY_SPEC");
            var unavailableProvider = candidates[2].GetProperty("provider");
            Assert.Equal(
                "Unknown",
                unavailableProvider.GetProperty("providerKind").GetString());
            Assert.Equal(
                "None",
                unavailableProvider.GetProperty("source").GetString());
            Assert.Equal(
                "PropertyNotFound",
                unavailableProvider.GetProperty("failure").GetString());

            Assert.DoesNotContain(SyntheticPin, output, StringComparison.Ordinal);
            Assert.DoesNotContain(
                SyntheticCredentialManagerSecret,
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToBase64String(privateKeyBytes),
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("\"pin", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credentialManager", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privateKey", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Array.Clear(privateKeyBytes);
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Constructor_RejectsRelativeOutputPath()
    {
        Assert.Throws<ArgumentException>(() =>
            new PsbcClientCertificateDiagnostics("psbc-certificates.jsonl"));
    }

    [Fact]
    public void BeginRunAndDeleteOutput_ControlFileLifecycleWithoutPropagatingIoFailures()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcCertificateDiagnosticsLifecycleTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "psbc-certificates.jsonl");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(path, "stale diagnostic identity");
            var diagnostics = new PsbcClientCertificateDiagnostics(path);

            diagnostics.BeginRun();

            Assert.Equal(string.Empty, File.ReadAllText(path));
            File.WriteAllText(path, "synthetic diagnostic identity");

            diagnostics.DeleteOutput();

            Assert.False(File.Exists(path));

            File.WriteAllText(path, "locked synthetic diagnostic identity");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = Record.Exception(diagnostics.DeleteOutput);

                Assert.Null(exception);
                Assert.True(File.Exists(path));
            }

            diagnostics.DeleteOutput();
            Assert.False(File.Exists(path));
            Assert.Null(Record.Exception(diagnostics.DeleteOutput));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static WebClientCertificate CreateCertificate(
        string fingerprint,
        ClientCertificateProviderKind providerKind,
        string providerName,
        uint providerType,
        string containerOrKeyName,
        string keySpec,
        object nativeCertificate)
    {
        var candidate = new ClientCertificateCandidate(
            fingerprint,
            "SYNTHETIC-PSBC-SUBJECT",
            "SYNTHETIC-PSBC-ISSUER",
            "CN=SYNTHETIC-PSBC-SUBJECT, O=PSBC",
            "CN=SYNTHETIC-PSBC-ISSUER, O=PSBC",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2028-01-01T00:00:00Z"),
            "0102030405060708",
            [new("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            new(
                providerKind,
                providerName,
                providerType,
                containerOrKeyName,
                keySpec,
                ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
                ClientCertificateProviderMetadataFailure.None));
        return new WebClientCertificate(candidate, nativeCertificate);
    }

    private static WebClientCertificate CreateUnavailableCertificate(
        string fingerprint)
    {
        var candidate = new ClientCertificateCandidate(
            fingerprint,
            "SYNTHETIC-PSBC-SUBJECT",
            "SYNTHETIC-PSBC-ISSUER",
            "CN=SYNTHETIC-PSBC-SUBJECT, O=PSBC",
            "CN=SYNTHETIC-PSBC-ISSUER, O=PSBC",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2028-01-01T00:00:00Z"),
            "1112131415161718",
            [new("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            ClientCertificateProviderMetadata.Unavailable(
                ClientCertificateProviderMetadataFailure.PropertyNotFound));
        return new WebClientCertificate(candidate, new object());
    }

    private static void AssertCandidate(
        JsonElement candidate,
        string providerKind,
        string providerName,
        int providerType,
        string containerOrKeyName,
        string keySpec)
    {
        Assert.NotEmpty(candidate.GetProperty("sha1Fingerprint").GetString()!);
        Assert.Equal(
            "SYNTHETIC-PSBC-SUBJECT",
            candidate.GetProperty("subject").GetString());
        Assert.Equal(
            "SYNTHETIC-PSBC-ISSUER",
            candidate.GetProperty("issuer").GetString());
        Assert.Equal(
            "0102030405060708",
            candidate.GetProperty("serialNumber").GetString());
        Assert.Equal(
            "1.3.6.1.5.5.7.3.2",
            candidate.GetProperty("enhancedKeyUsages")[0]
                .GetProperty("oid")
                .GetString());

        var provider = candidate.GetProperty("provider");
        Assert.Equal(providerKind, provider.GetProperty("providerKind").GetString());
        Assert.Equal(providerName, provider.GetProperty("providerName").GetString());
        Assert.Equal(providerType, provider.GetProperty("providerType").GetInt32());
        Assert.Equal(
            containerOrKeyName,
            provider.GetProperty("containerOrKeyName").GetString());
        Assert.Equal(keySpec, provider.GetProperty("keySpec").GetString());
        Assert.Equal(
            "WindowsCertificateKeyProviderInfoProperty",
            provider.GetProperty("source").GetString());
        Assert.Equal("None", provider.GetProperty("failure").GetString());
    }
}
