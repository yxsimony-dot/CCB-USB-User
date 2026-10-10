// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Reflection;
using System.Text.RegularExpressions;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class LauncherProfileJsonTests
{
    [Fact]
    public void Parse_ReadsCamelCaseLauncherProfile()
    {
        const string json = """
            {
              "targetUrl": "https://b2b.ccb.com/",
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              }
            }
            """;

        var profile = LauncherProfileJson.Parse(json);

        Assert.Equal("https://b2b.ccb.com/", profile.TargetUrl);
        Assert.NotNull(profile.UsbDevice);
        Assert.Equal("1677", profile.UsbDevice.VendorId);
        Assert.Equal("0054", profile.UsbDevice.ProductId);
        Assert.Equal("uKeyCI800-K156", profile.UsbDevice.BusReportedDescription);
    }

    [Fact]
    public void Parse_ReadsAdditionalUsbDeviceAllowlist()
    {
        const string json = """
            {
              "targetUrl": "https://b2b.ccb.com/",
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              },
              "additionalUsbDevices": [
                {
                  "vendorId": "1780",
                  "productId": "0401",
                  "busReportedDescription": "Tendyron Token",
                  "cdRomName": "TENDYRON U-DISK USB Device",
                  "volumeLabel": "CCB",
                  "fileSystem": "CDFS"
                }
              ]
            }
            """;

        var profile = LauncherProfileJson.Parse(json);

        var additional = Assert.Single(profile.AdditionalUsbDevices!);
        Assert.Equal("1780", additional.VendorId);
        Assert.Equal("0401", additional.ProductId);
        Assert.Equal("Tendyron Token", additional.BusReportedDescription);
        Assert.Equal("TENDYRON U-DISK USB Device", additional.CdRomName);
        Assert.Equal("CCB", additional.VolumeLabel);
        Assert.Equal("CDFS", additional.FileSystem);
    }

    [Fact]
    public void Parse_ReadsClientCertificateAuthenticationConfiguration()
    {
        const string json = """
            {
              "targetUrl": "https://b2b.ccb.com/",
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              },
              "clientCertificateAuthentication": {
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
              }
            }
            """;

        var profile = LauncherProfileJson.Parse(json);
        var authentication = GetRequiredProperty(profile, "ClientCertificateAuthentication");
        var certificate = GetRequiredProperty(authentication, "Certificate");
        var cryptographicProvider = GetRequiredProperty(authentication, "CryptographicProvider");

        Assert.Equal("ccb-client-certificate", GetRequiredString(authentication, "Id"));
        Assert.Equal("b2b.ccb.com", GetRequiredString(authentication, "Host"));
        Assert.Equal(443, GetRequiredInt(authentication, "Port"));
        Assert.Equal(
            "1111111111111111111111111111111111111111",
            GetRequiredString(certificate, "Sha1Fingerprint"));
        Assert.Equal("GSB00000000003", GetRequiredString(certificate, "Subject"));
        Assert.Equal("CCBRSACAROOT", GetRequiredString(certificate, "Issuer"));
        Assert.Equal("CCB HD CSP V3.0", GetRequiredString(cryptographicProvider, "CspName"));
        Assert.Equal(1, GetRequiredInt(cryptographicProvider, "ProviderType"));
        Assert.Equal("SAMPLE_KEY_CONTAINER_1_RSA", GetRequiredString(cryptographicProvider, "ContainerName"));
        Assert.Equal("AT_KEYEXCHANGE", GetRequiredString(cryptographicProvider, "KeySpec"));
        Assert.Equal(
            "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
            GetRequiredString(authentication, "CredentialTargetName"));
    }

    [Fact]
    public void Parse_ReadsExplicitAdditionalCertificateEndpoints()
    {
        var profile = LauncherProfileJson.Parse(CreateClientCertificateJson("""
            "id": "ccb-client-certificate",
            "host": "b2b3.ccb.com",
            "port": 443,
            "additionalEndpoints": [
              {
                "host": "xc.b2b3.ccb.com",
                "port": 443
              }
            ],
            "certificate": {
              "sha1Fingerprint": "1111111111111111111111111111111111111111",
              "subject": "GSB00000000003",
              "issuer": "CCBRSACAROOT"
            },
            "cryptographicProvider": {
              "cspName": "CCB HD CSP V3.0",
              "providerType": 1,
              "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
              "keySpec": "AT_KEYEXCHANGE"
            },
            "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
            """));

        var endpoint = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyCollection<ClientCertificateEndpointOptions>>(
                profile.ClientCertificateAuthentication!.AdditionalEndpoints));

        Assert.Equal("xc.b2b3.ccb.com", endpoint.Host);
        Assert.Equal(443, endpoint.Port);
    }

    [Fact]
    public void Parse_ReadsNativePromptContinuationConfigurationFromShippedProfile()
    {
        var profilePath = FindRepoRootPath("src", "UsbWebLauncher.App", "launcher-profile.json");
        var json = File.ReadAllText(profilePath);

        var profile = LauncherProfileJson.Parse(json);
        var authentication = GetRequiredProperty(profile, "ClientCertificateAuthentication");
        var nativePrompt = GetRequiredProperty(authentication, "NativePromptContinuation");

        Assert.Equal(true, GetRequiredProperty(nativePrompt, "Enabled"));
        Assert.Equal("UsbWebLauncher.NativePromptBroker.v1", GetRequiredString(nativePrompt, "BrokerPipeName"));
        Assert.Equal("ccb-hdzb-client-certificate-v1", GetRequiredString(nativePrompt, "PolicyId"));
        Assert.Equal(string.Empty, GetRequiredString(nativePrompt, "ProcessPath"));
        Assert.Equal(string.Empty, GetRequiredString(nativePrompt, "ProcessSha256"));
        Assert.Equal(string.Empty, GetRequiredString(nativePrompt, "PublisherCertificateThumbprint"));
        Assert.Equal("验证通用盾密码", GetRequiredString(nativePrompt, "WindowTitle"));
        Assert.Equal("#32770", GetRequiredString(nativePrompt, "WindowClass"));

        var requiredChildWindowClasses = GetRequiredProperty(nativePrompt, "RequiredChildWindowClasses");
        var classes = Assert.IsAssignableFrom<IReadOnlyCollection<string>>(requiredChildWindowClasses);
        Assert.Empty(classes);
        Assert.Equal(30000, GetRequiredInt(nativePrompt, "TimeoutMilliseconds"));
        Assert.Equal(
            NativePromptWindowScopes.TrustedClientTopLevel,
            GetRequiredString(nativePrompt, "WindowScope"));
        Assert.Equal("msedgewebview2.exe", GetRequiredString(nativePrompt, "ProcessFileName"));
        Assert.Equal(string.Empty, GetRequiredString(nativePrompt, "TrustedRootWindowTitle"));
        Assert.Equal(string.Empty, GetRequiredString(nativePrompt, "TrustedRootWindowClassPrefix"));
        Assert.Equal("Edit", GetRequiredString(nativePrompt, "InputWindowClass"));
        Assert.Equal(3743, GetRequiredInt(nativePrompt, "InputControlId"));
        Assert.Equal("Button", GetRequiredString(nativePrompt, "SubmitWindowClass"));
        Assert.Equal(4000, GetRequiredInt(nativePrompt, "SubmitControlId"));
    }

    [Fact]
    public void Parse_RejectsNativePromptContinuationWhenProcessSha256HasWhitespacePadding()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
                "nativePromptContinuation": {
                  "enabled": true,
                  "brokerPipeName": "UsbWebLauncher.NativePromptBroker.v1",
                  "policyId": "ccb-hdzb-client-certificate-v1",
                  "processPath": "C:\\Program Files (x86)\\CCBComponents\\Detector\\CCBDetector.exe",
                  "processSha256": " EDB0029E220ABCC32676B7AA90B2E012C4FB84AF0AD891BB0BA6B0F021FDB607 ",
                  "publisherCertificateThumbprint": "3723D4D6F69F5999BC6216222A69886352EDBF75",
                  "windowTitle": "E路护航网银安全组件",
                  "windowClass": "#32770",
                  "requiredChildWindowClasses": [
                    "Shell Embedding",
                    "Shell DocObject View",
                    "Internet Explorer_Server"
                  ],
                  "timeoutMilliseconds": 10000
                }
                """)));

        Assert.Contains("clientCertificateAuthentication.nativePromptContinuation.processSha256", exception.Message);
    }

    [Fact]
    public void Parse_RejectsNativePromptContinuationWhenPublisherThumbprintHasEmbeddedWhitespace()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
                "nativePromptContinuation": {
                  "enabled": true,
                  "brokerPipeName": "UsbWebLauncher.NativePromptBroker.v1",
                  "policyId": "ccb-hdzb-client-certificate-v1",
                  "processPath": "C:\\Program Files (x86)\\CCBComponents\\Detector\\CCBDetector.exe",
                  "processSha256": "EDB0029E220ABCC32676B7AA90B2E012C4FB84AF0AD891BB0BA6B0F021FDB607",
                  "publisherCertificateThumbprint": "3723D4D6F69F5999BC6216222A69886352E DBF75",
                  "windowTitle": "E路护航网银安全组件",
                  "windowClass": "#32770",
                  "requiredChildWindowClasses": [
                    "Shell Embedding",
                    "Shell DocObject View",
                    "Internet Explorer_Server"
                  ],
                  "timeoutMilliseconds": 10000
                }
                """)));

        Assert.Contains("clientCertificateAuthentication.nativePromptContinuation.publisherCertificateThumbprint", exception.Message);
    }

    [Fact]
    public void Parse_RejectsClientCertificateAuthenticationWhenIdIsMissing()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.id", exception.Message);
    }

    [Fact]
    public void Parse_RejectsClientCertificateAuthenticationWhenHostContainsScheme()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "id": "ccb-client-certificate",
                "host": "https://b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.host", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Parse_RejectsClientCertificateAuthenticationWhenPortIsInvalid(int port)
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson($$"""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": {{port}},
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.port", exception.Message);
    }

    [Theory]
    [InlineData("B0C9DADA164B6DC28527FD6371E3EC63DEA9ADD")]
    [InlineData("B0C9DADA164B6DC28527FD6371E3EC63DEA9ADDZ")]
    public void Parse_RejectsClientCertificateAuthenticationWhenSha1FingerprintIsInvalid(string fingerprint)
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson($$"""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "{{fingerprint}}",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.certificate.sha1Fingerprint", exception.Message);
    }

    [Fact]
    public void Parse_RejectsClientCertificateAuthenticationWhenKeySpecIsUnsupported()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_UNKNOWN"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.cryptographicProvider.keySpec", exception.Message);
    }

    [Fact]
    public void Parse_RejectsClientCertificateAuthenticationWhenSecretFieldIsPresent()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(CreateClientCertificateJson("""
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "pin": "12345678",
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
                """)));

        Assert.Contains("clientCertificateAuthentication.pin", exception.Message);
    }

    [Fact]
    public void Parse_RejectsNestedSecretFieldOutsideClientCertificateAuthentication()
    {
        const string json = """
            {
              "targetUrl": "https://b2b.ccb.com/",
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              },
              "clientCertificateAuthentication": {
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "GSB00000000003",
                  "issuer": "CCBRSACAROOT"
                },
                "cryptographicProvider": {
                  "cspName": "CCB HD CSP V3.0",
                  "providerType": 1,
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "keySpec": "AT_KEYEXCHANGE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
              },
              "diagnostics": [
                {
                  "label": "ok"
                },
                {
                  "secretValue": "forbidden"
                }
              ]
            }
            """;

        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(json));

        Assert.Contains("diagnostics[1].secretValue", exception.Message);
    }

    [Fact]
    public void Parse_ComputesStableClientCertificateAuthenticationHashAcrossFieldOrderAndCasingDifferences()
    {
        var first = LauncherProfileJson.Parse(CreateClientCertificateJson("""
            "id": "ccb-client-certificate",
            "host": "B2B.CCB.COM",
            "port": 443,
            "certificate": {
              "sha1Fingerprint": "1111111111111111111111111111111111111111",
              "subject": "GSB00000000003",
              "issuer": "CCBRSACAROOT"
            },
            "cryptographicProvider": {
              "cspName": "CCB HD CSP V3.0",
              "providerType": 1,
              "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
              "keySpec": " at_keyexchange "
            },
            "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
            """));

        var second = LauncherProfileJson.Parse("""
            {
              "clientCertificateAuthentication": {
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
                "cryptographicProvider": {
                  "keySpec": "AT_KEYEXCHANGE",
                  "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
                  "providerType": 1,
                  "cspName": "CCB HD CSP V3.0"
                },
                "certificate": {
                  "issuer": "CCBRSACAROOT",
                  "subject": "GSB00000000003",
                  "sha1Fingerprint": "1111111111111111111111111111111111111111"
                },
                "port": 443,
                "host": "b2b.ccb.com",
                "id": "ccb-client-certificate"
              },
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              },
              "targetUrl": "https://b2b.ccb.com/"
            }
            """);

        Assert.Equal(
            GetNormalizedHash(first),
            GetNormalizedHash(second));
    }

    [Fact]
    public void Parse_ComputesDifferentClientCertificateAuthenticationHashWhenIdentityChanges()
    {
        var baseline = LauncherProfileJson.Parse(CreateClientCertificateJson("""
            "id": "ccb-client-certificate",
            "host": "b2b.ccb.com",
            "port": 443,
            "certificate": {
              "sha1Fingerprint": "1111111111111111111111111111111111111111",
              "subject": "GSB00000000003",
              "issuer": "CCBRSACAROOT"
            },
            "cryptographicProvider": {
              "cspName": "CCB HD CSP V3.0",
              "providerType": 1,
              "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
              "keySpec": "AT_KEYEXCHANGE"
            },
            "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
            """));

        var changedFingerprint = LauncherProfileJson.Parse(CreateClientCertificateJson("""
            "id": "ccb-client-certificate",
            "host": "b2b.ccb.com",
            "port": 443,
            "certificate": {
              "sha1Fingerprint": "A0C9DADA164B6DC28527FD6371E3EC63DEA9ADDF",
              "subject": "GSB00000000003",
              "issuer": "CCBRSACAROOT"
            },
            "cryptographicProvider": {
              "cspName": "CCB HD CSP V3.0",
              "providerType": 1,
              "containerName": "SAMPLE_KEY_CONTAINER_1_RSA",
              "keySpec": "AT_KEYEXCHANGE"
            },
            "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
            """));

        Assert.NotEqual(
            GetNormalizedHash(baseline),
            GetNormalizedHash(changedFingerprint));
    }

    [Fact]
    public void Parse_ReadsShippedLauncherProfileWithClientCertificateAuthenticationAndNoSecretFields()
    {
        var profilePath = FindRepoRootPath("src", "UsbWebLauncher.App", "launcher-profile.json");
        var json = File.ReadAllText(profilePath);

        var profile = LauncherProfileJson.Parse(json);
        var authentication = GetRequiredProperty(profile, "ClientCertificateAuthentication");
        var typedAuthentication = Assert.IsType<ClientCertificateAuthenticationOptions>(authentication);
        Assert.True(typedAuthentication.UsesBrowserNativeAuthentication);
        Assert.Null(typedAuthentication.Certificate);
        Assert.Null(typedAuthentication.CryptographicProvider);
        Assert.Null(typedAuthentication.TokenUnlock);
        var additionalUsbDevices = Assert.IsAssignableFrom<IReadOnlyCollection<UsbMatchOptions>>(
            profile.AdditionalUsbDevices);
        var tendyronDevice = Assert.Single(additionalUsbDevices);
        var additionalEndpoints = Assert.IsAssignableFrom<IReadOnlyCollection<ClientCertificateEndpointOptions>>(
            GetRequiredProperty(authentication, "AdditionalEndpoints"));
        var additionalEndpoint = Assert.Single(additionalEndpoints);
        var nativePrompt = GetRequiredProperty(authentication, "NativePromptContinuation");

        Assert.Equal("b2b3.ccb.com", GetRequiredString(authentication, "Host"));
        Assert.Equal(443, GetRequiredInt(authentication, "Port"));
        Assert.Equal("xc.b2b3.ccb.com", additionalEndpoint.Host);
        Assert.Equal(443, additionalEndpoint.Port);
        Assert.Equal("1780", tendyronDevice.VendorId);
        Assert.Equal("0401", tendyronDevice.ProductId);
        Assert.Equal("Tendyron Token", tendyronDevice.BusReportedDescription);
        Assert.Equal("TENDYRON U-DISK USB Device", tendyronDevice.CdRomName);
        Assert.Equal("CCB", tendyronDevice.VolumeLabel);
        Assert.Equal("CDFS", tendyronDevice.FileSystem);
        Assert.Equal(3743, GetRequiredInt(nativePrompt, "InputControlId"));
        Assert.Equal(4000, GetRequiredInt(nativePrompt, "SubmitControlId"));
        Assert.DoesNotMatch(new Regex("\"(pin|password|secret)\"\\s*:", RegexOptions.IgnoreCase), json);
    }

    [Fact]
    public void ShippedNativePromptPolicy_ExactlyMatchesShippedAuthenticationProfile()
    {
        var profilePath = FindRepoRootPath("src", "UsbWebLauncher.App", "launcher-profile.json");
        var policyPath = FindRepoRootPath(
            "src",
            "UsbWebLauncher.NativePromptBroker",
            WindowsNativePromptBrokerInstaller.PolicyFileName);
        var authentication = Assert.IsType<UsbWebLauncher.Core.Configuration.ClientCertificateAuthenticationOptions>(
            LauncherProfileJson.Parse(File.ReadAllText(profilePath)).ClientCertificateAuthentication);
        var expectedPrompt = Assert.IsType<UsbWebLauncher.Core.Configuration.ClientCertificateNativePromptOptions>(
            authentication.NativePromptContinuation);

        var policy = NativePromptBrokerPolicyFile.Load(policyPath);

        Assert.Equal(authentication.GetNormalizedHash(), policy.PolicyHash);
        Assert.Equal(expectedPrompt.Enabled, policy.Prompt.Enabled);
        Assert.Equal(expectedPrompt.BrokerPipeName, policy.Prompt.BrokerPipeName);
        Assert.Equal(expectedPrompt.PolicyId, policy.Prompt.PolicyId);
        Assert.Equal(expectedPrompt.ProcessPath, policy.Prompt.ProcessPath);
        Assert.Equal(expectedPrompt.ProcessSha256, policy.Prompt.ProcessSha256);
        Assert.Equal(
            expectedPrompt.PublisherCertificateThumbprint,
            policy.Prompt.PublisherCertificateThumbprint);
        Assert.Equal(expectedPrompt.WindowTitle, policy.Prompt.WindowTitle);
        Assert.Equal(expectedPrompt.WindowClass, policy.Prompt.WindowClass);
        Assert.Equal(expectedPrompt.RequiredChildWindowClasses, policy.Prompt.RequiredChildWindowClasses);
        Assert.Equal(expectedPrompt.TimeoutMilliseconds, policy.Prompt.TimeoutMilliseconds);
        Assert.Equal(expectedPrompt.WindowScope, policy.Prompt.WindowScope);
        Assert.Equal(expectedPrompt.ProcessFileName, policy.Prompt.ProcessFileName);
        Assert.Equal(expectedPrompt.TrustedRootWindowTitle, policy.Prompt.TrustedRootWindowTitle);
        Assert.Equal(expectedPrompt.TrustedRootWindowClassPrefix, policy.Prompt.TrustedRootWindowClassPrefix);
        Assert.Equal(expectedPrompt.InputWindowClass, policy.Prompt.InputWindowClass);
        Assert.Equal(expectedPrompt.InputControlId, policy.Prompt.InputControlId);
        Assert.Equal(expectedPrompt.SubmitWindowClass, policy.Prompt.SubmitWindowClass);
        Assert.Equal(expectedPrompt.SubmitControlId, policy.Prompt.SubmitControlId);
    }

    [Fact]
    public void Parse_RejectsProfileThatFailsValidation()
    {
        const string json = """
            {
              "targetUrl": "http://example.com/",
              "usbDevice": {
                "vendorId": "1677",
                "productId": "0054",
                "busReportedDescription": "uKeyCI800-K156"
              }
            }
            """;

        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(json));

        Assert.Contains("targetUrl", exception.Message);
    }

    private static string CreateClientCertificateJson(string clientCertificateAuthenticationJson) =>
        $$"""
        {
          "targetUrl": "https://b2b.ccb.com/",
          "usbDevice": {
            "vendorId": "1677",
            "productId": "0054",
            "busReportedDescription": "uKeyCI800-K156"
          },
          "clientCertificateAuthentication": {
            {{clientCertificateAuthenticationJson}}
          }
        }
        """;

    private static object GetRequiredProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);

        var value = property.GetValue(instance);
        Assert.NotNull(value);
        return value!;
    }

    private static string GetRequiredString(object instance, string propertyName)
    {
        var value = Assert.IsType<string>(GetRequiredProperty(instance, propertyName));
        return value;
    }

    private static int GetRequiredInt(object instance, string propertyName)
    {
        var value = Assert.IsType<int>(GetRequiredProperty(instance, propertyName));
        return value;
    }

    private static string GetNormalizedHash(object profile)
    {
        var authentication = GetRequiredProperty(profile, "ClientCertificateAuthentication");
        var method = authentication.GetType().GetMethod("GetNormalizedHash", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var hash = method.Invoke(authentication, []);
        return Assert.IsType<string>(hash);
    }

    private static string FindRepoRootPath(params string[] relativeSegments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UsbWebLauncher.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. relativeSegments]);
    }
}
