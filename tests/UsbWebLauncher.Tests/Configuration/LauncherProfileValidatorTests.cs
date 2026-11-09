// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class LauncherProfileValidatorTests
{
    private const string ValidProcessSha256 = "EDB0029E220ABCC32676B7AA90B2E012C4FB84AF0AD891BB0BA6B0F021FDB607";
    private const string ValidPublisherThumbprint = "3723D4D6F69F5999BC6216222A69886352EDBF75";

    [Fact]
    public void Validate_RejectsWhitespacePaddedNativePromptProcessSha256()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidNativePrompt() with
                {
                    ProcessSha256 = $" {ValidProcessSha256} "
                }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.nativePromptContinuation.processSha256");
    }

    [Fact]
    public void Validate_RejectsEmbeddedWhitespaceNativePromptProcessSha256()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidNativePrompt() with
                {
                    ProcessSha256 = ValidProcessSha256.Insert(32, " ")
                }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.nativePromptContinuation.processSha256");
    }

    [Fact]
    public void Validate_RejectsWhitespacePaddedNativePromptPublisherThumbprint()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidNativePrompt() with
                {
                    PublisherCertificateThumbprint = $" {ValidPublisherThumbprint} "
                }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.nativePromptContinuation.publisherCertificateThumbprint");
    }

    [Fact]
    public void Validate_RejectsEmbeddedWhitespaceNativePromptPublisherThumbprint()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidNativePrompt() with
                {
                    PublisherCertificateThumbprint = ValidPublisherThumbprint.Insert(20, " ")
                }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.nativePromptContinuation.publisherCertificateThumbprint");
    }

    [Fact]
    public void Validate_RejectsNativePromptPolicyWithRelativeProcessPath()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidNativePrompt() with
                {
                    ProcessPath = @"CCBDetector.exe"
                }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error => error.Field.EndsWith("processPath", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizedHash_ChangesWhenNativePromptBinaryHashChanges()
    {
        var first = CreateValidAuthentication();
        var second = first with
        {
            NativePromptContinuation = first.NativePromptContinuation! with
            {
                ProcessSha256 = new string('B', 64)
            }
        };

        Assert.NotEqual(first.GetNormalizedHash(), second.GetNormalizedHash());
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("b2b.ccb.com")]
    [InlineData("https://user:password@example.com/")]
    public void Validate_RejectsTargetThatIsNotAnAbsoluteHttpsUri(string targetUrl)
    {
        var profile = CreateValidProfile() with { TargetUrl = targetUrl };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "targetUrl");
    }

    [Theory]
    [InlineData("178")]
    [InlineData("17800")]
    [InlineData("ZZZZ")]
    public void Validate_RejectsMalformedVendorId(string vendorId)
    {
        var profile = CreateValidProfile() with
        {
            UsbDevice = CreateValidProfile().UsbDevice! with { VendorId = vendorId }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "usbDevice.vendorId");
    }

    [Theory]
    [InlineData("401")]
    [InlineData("04010")]
    [InlineData("G401")]
    public void Validate_RejectsMalformedProductId(string productId)
    {
        var profile = CreateValidProfile() with
        {
            UsbDevice = CreateValidProfile().UsbDevice! with { ProductId = productId }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "usbDevice.productId");
    }

    [Fact]
    public void Validate_AcceptsObservedTokenProfile()
    {
        var result = LauncherProfileValidator.Validate(CreateValidProfile());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_AcceptsDormantIndependentPsbcAuthentication()
    {
        var result = LauncherProfileValidator.Validate(CreateValidPsbcProfile());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_AcceptsEnabledPsbcAuthenticationWithCompleteSyntheticPolicy()
    {
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication =
                CreateValidPsbcAuthentication(enabled: true)
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("credential")]
    [InlineData("policy")]
    [InlineData("host")]
    public void Validate_RejectsCcbNamespaceInPsbcAuthentication(string field)
    {
        var authentication = CreateValidPsbcAuthentication();
        authentication = field switch
        {
            "id" => authentication with { Id = "ccb-client-certificate" },
            "credential" => authentication with
            {
                CredentialTargetName =
                    "UsbWebLauncher/ClientCertificate/ccb-client-certificate"
            },
            "policy" => authentication with
            {
                NativePromptContinuation =
                    authentication.NativePromptContinuation! with
                    {
                        PolicyId = "ccb-hdzb-client-certificate-v1"
                    }
            },
            "host" => authentication with { Host = "b2b3.ccb.com" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication = authentication
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("*.psbc.com")]
    [InlineData("https://corpebank.psbc.com")]
    [InlineData("corpebank.psbc.com/login")]
    public void Validate_RejectsUnsafePsbcAuthenticationHost(string host)
    {
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication =
                CreateValidPsbcAuthentication() with { Host = host }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.host");
    }

    [Fact]
    public void Validate_RejectsFixedCcbTokenBindingsInPsbcAuthentication()
    {
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication =
                CreateValidPsbcAuthentication() with
                {
                    Certificate = new(
                        new string('A', 40),
                        "Synthetic CCB subject",
                        "Synthetic CCB issuer"),
                    CryptographicProvider = new(
                        "Synthetic CCB provider",
                        1,
                        "Synthetic CCB container",
                        "AT_KEYEXCHANGE"),
                    TokenUnlock = new("CRYPTOAPI")
                }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.certificate");
        Assert.Contains(result.Errors, error =>
            error.Field ==
            "clientCertificateAuthentication.cryptographicProvider");
        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.tokenUnlock");
    }

    [Fact]
    public void Validate_RejectsEnabledPromptForDormantPsbcAuthentication()
    {
        var authentication = CreateValidPsbcAuthentication();
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication = authentication with
            {
                NativePromptContinuation =
                    authentication.NativePromptContinuation! with
                    {
                        Enabled = true
                    }
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field ==
            "clientCertificateAuthentication.nativePromptContinuation.enabled");
    }

    [Fact]
    public void Validate_AcceptsDistinctNavigationAndCertificateAuthenticationHosts()
    {
        var profile = CreateValidProfile() with
        {
            TargetUrl = "https://b2bbank.ccb.com/",
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                Host = "b2b3.ccb.com"
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_AcceptsExplicitAdditionalCertificateEndpoint()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                AdditionalEndpoints = [new("xc.b2b3.ccb.com", 443)]
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_RejectsInvalidAdditionalCertificateEndpoint()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                AdditionalEndpoints = [new("https://xc.b2b3.ccb.com", 0)]
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.additionalEndpoints[0].host");
        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.additionalEndpoints[0].port");
    }

    [Fact]
    public void Validate_RejectsDuplicateAdditionalCertificateEndpoints()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                AdditionalEndpoints =
                [
                    new("B2B.CCB.COM", 443),
                    new("xc.b2b3.ccb.com", 443),
                    new("XC.B2B3.CCB.COM", 443)
                ]
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.additionalEndpoints[0]");
        Assert.Contains(result.Errors, error =>
            error.Field == "clientCertificateAuthentication.additionalEndpoints[2]");
    }

    [Fact]
    public void NormalizedHash_IsStableAcrossAdditionalEndpointOrderAndChangesWithEndpointIdentity()
    {
        var first = CreateValidAuthentication() with
        {
            AdditionalEndpoints =
            [
                new("xc.b2b3.ccb.com", 443),
                new("auth.example.com", 8443)
            ]
        };
        var reordered = first with
        {
            AdditionalEndpoints =
            [
                new("AUTH.EXAMPLE.COM", 8443),
                new("XC.B2B3.CCB.COM", 443)
            ]
        };
        var changed = first with
        {
            AdditionalEndpoints = [new("xc.b2b3.ccb.com", 8443)]
        };

        Assert.Equal(first.GetNormalizedHash(), reordered.GetNormalizedHash());
        Assert.NotEqual(first.GetNormalizedHash(), changed.GetNormalizedHash());
    }

    [Fact]
    public void Validate_AcceptsAdditionalUsbDeviceAndSignatureKeySpec()
    {
        var profile = CreateValidProfile() with
        {
            AdditionalUsbDevices =
            [
                new("1780", "0401", "Tendyron Token", "TENDYRON U-DISK USB Device", "CCB", "CDFS")
            ],
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                CryptographicProvider = new(
                    "Tendyron OnKey CSP CCB v1.0",
                    1,
                    "{00000000-0000-0003}",
                    "AT_SIGNATURE")
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ReportsIndexedFieldForInvalidAdditionalUsbDevice()
    {
        var profile = CreateValidProfile() with
        {
            AdditionalUsbDevices = [new("178", "0401", "Tendyron Token")]
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.Contains(result.Errors, error =>
            error.Field == "additionalUsbDevices[0].vendorId");
    }

    [Fact]
    public void Validate_AcceptsStrictTrustedClientDescendantPromptPolicy()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidDescendantNativePrompt()
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("process-file")]
    [InlineData("root-title")]
    [InlineData("root-class")]
    [InlineData("input-class")]
    [InlineData("input-id")]
    [InlineData("submit-class")]
    [InlineData("submit-id")]
    [InlineData("same-id")]
    public void Validate_RejectsIncompleteTrustedClientDescendantPromptPolicy(string difference)
    {
        var prompt = difference switch
        {
            "process-file" => CreateValidDescendantNativePrompt() with { ProcessFileName = @"C:\bad.exe" },
            "root-title" => CreateValidDescendantNativePrompt() with { TrustedRootWindowTitle = string.Empty },
            "root-class" => CreateValidDescendantNativePrompt() with { TrustedRootWindowClassPrefix = string.Empty },
            "input-class" => CreateValidDescendantNativePrompt() with { InputWindowClass = string.Empty },
            "input-id" => CreateValidDescendantNativePrompt() with { InputControlId = 0 },
            "submit-class" => CreateValidDescendantNativePrompt() with { SubmitWindowClass = string.Empty },
            "submit-id" => CreateValidDescendantNativePrompt() with { SubmitControlId = 0 },
            "same-id" => CreateValidDescendantNativePrompt() with { SubmitControlId = 1010 },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = prompt
            }
        };

        Assert.False(LauncherProfileValidator.Validate(profile).IsValid);
    }

    [Fact]
    public void Validate_AcceptsStrictTrustedClientTopLevelPromptPolicy()
    {
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = CreateValidTrustedClientTopLevelPrompt()
            }
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("process-path")]
    [InlineData("process-file")]
    [InlineData("publisher")]
    [InlineData("title")]
    [InlineData("children")]
    [InlineData("input-class")]
    [InlineData("input-id")]
    [InlineData("submit-class")]
    [InlineData("submit-id")]
    [InlineData("same-id")]
    public void Validate_RejectsIncompleteTrustedClientTopLevelPromptPolicy(string difference)
    {
        var prompt = difference switch
        {
            "process-path" => CreateValidTrustedClientTopLevelPrompt() with
            {
                ProcessPath = @"C:\bad.exe"
            },
            "process-file" => CreateValidTrustedClientTopLevelPrompt() with
            {
                ProcessFileName = @"C:\bad.exe"
            },
            "publisher" => CreateValidTrustedClientTopLevelPrompt() with
            {
                PublisherCertificateThumbprint = ValidPublisherThumbprint
            },
            "title" => CreateValidTrustedClientTopLevelPrompt() with { WindowTitle = string.Empty },
            "children" => CreateValidTrustedClientTopLevelPrompt() with
            {
                RequiredChildWindowClasses = ["Edit"]
            },
            "input-class" => CreateValidTrustedClientTopLevelPrompt() with
            {
                InputWindowClass = string.Empty
            },
            "input-id" => CreateValidTrustedClientTopLevelPrompt() with { InputControlId = 0 },
            "submit-class" => CreateValidTrustedClientTopLevelPrompt() with
            {
                SubmitWindowClass = string.Empty
            },
            "submit-id" => CreateValidTrustedClientTopLevelPrompt() with { SubmitControlId = 0 },
            "same-id" => CreateValidTrustedClientTopLevelPrompt() with { SubmitControlId = 1010 },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        var profile = CreateValidProfile() with
        {
            ClientCertificateAuthentication = CreateValidAuthentication() with
            {
                NativePromptContinuation = prompt
            }
        };

        Assert.False(LauncherProfileValidator.Validate(profile).IsValid);
    }

    [Fact]
    public void Validate_AcceptsEnabledPsbcStrictTopLevelPromptPolicy()
    {
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication = CreateValidPsbcAuthentication(enabled: true)
        };

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("process-path")]
    [InlineData("process-file")]
    [InlineData("process-hash")]
    [InlineData("publisher")]
    [InlineData("title")]
    [InlineData("window-class")]
    [InlineData("children")]
    [InlineData("root-title")]
    [InlineData("root-class")]
    [InlineData("input-class")]
    [InlineData("input-id")]
    [InlineData("submit-class")]
    [InlineData("submit-id")]
    [InlineData("same-id")]
    public void Validate_RejectsIncompletePsbcStrictTopLevelPromptPolicy(string difference)
    {
        var prompt = CreateValidPsbcStrictTopLevelPrompt();
        prompt = difference switch
        {
            "scope" => prompt with
            {
                WindowScope = NativePromptWindowScopes.TrustedClientTopLevel
            },
            "process-path" => prompt with { ProcessPath = @"C:\bad.exe" },
            "process-file" => prompt with { ProcessFileName = @"C:\bad.exe" },
            "process-hash" => prompt with { ProcessSha256 = string.Empty },
            "publisher" => prompt with { PublisherCertificateThumbprint = string.Empty },
            "title" => prompt with { WindowTitle = string.Empty },
            "window-class" => prompt with { WindowClass = string.Empty },
            "children" => prompt with { RequiredChildWindowClasses = ["Edit"] },
            "root-title" => prompt with { TrustedRootWindowTitle = "Unexpected" },
            "root-class" => prompt with { TrustedRootWindowClassPrefix = "Unexpected" },
            "input-class" => prompt with { InputWindowClass = string.Empty },
            "input-id" => prompt with { InputControlId = 0 },
            "submit-class" => prompt with { SubmitWindowClass = string.Empty },
            "submit-id" => prompt with { SubmitControlId = 0 },
            "same-id" => prompt with { SubmitControlId = 3005 },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        var authentication = CreateValidPsbcAuthentication(enabled: true) with
        {
            NativePromptContinuation = prompt
        };
        var profile = CreateValidPsbcProfile() with
        {
            ClientCertificateAuthentication = authentication
        };

        Assert.False(LauncherProfileValidator.Validate(profile).IsValid);
    }

    private static LauncherProfile CreateValidProfile() => new(
        "https://b2b.ccb.com/",
        new UsbMatchOptions("1677", "0054", "uKeyCI800-K156"),
        CreateValidAuthentication());

    private static ClientCertificateAuthenticationOptions CreateValidAuthentication() => new(
        "ccb-client-certificate",
        "b2b.ccb.com",
        443,
        new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
        new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
        NativePromptContinuation: CreateValidNativePrompt());

    private static LauncherProfile CreateValidPsbcProfile() => new(
        "https://corpebank.psbc.com/#/login",
        ClientCertificateAuthentication: CreateValidPsbcAuthentication(),
        UsbDetectionStrategy: "psbcUsbKey");

    private static ClientCertificateAuthenticationOptions
        CreateValidPsbcAuthentication(bool enabled = false) => new(
            "psbc-client-certificate",
            "corpebank.psbc.com",
            443,
            null!,
            null!,
            "UsbWebLauncher/ClientCertificate/psbc-client-certificate",
            NativePromptContinuation: enabled
                ? CreateValidPsbcStrictTopLevelPrompt()
                : new(
                    false,
                    "UsbWebLauncher.NativePromptBroker.v1",
                    "psbc-uk-password-v1",
                    "",
                    "",
                    "",
                    "",
                    "",
                    [],
                    0),
            AuthenticationMode: "BrowserNative",
            Enabled: enabled);

    private static ClientCertificateNativePromptOptions CreateValidNativePrompt() => new(
        true,
        "UsbWebLauncher.NativePromptBroker.v1",
        "ccb-hdzb-client-certificate-v1",
        @"C:\Program Files (x86)\CCBComponents\Detector\CCBDetector.exe",
        ValidProcessSha256,
        ValidPublisherThumbprint,
        "E路护航网银安全组件",
        "#32770",
        ["Shell Embedding", "Shell DocObject View", "Internet Explorer_Server"],
        10000);

    private static ClientCertificateNativePromptOptions CreateValidDescendantNativePrompt() =>
        CreateValidNativePrompt() with
        {
            WindowScope = NativePromptWindowScopes.TrustedClientDescendant,
            ProcessPath = string.Empty,
            ProcessSha256 = string.Empty,
            ProcessFileName = "msedgewebview2.exe",
            PublisherCertificateThumbprint = "4028CAD637509D4744B17EC5B42AED8D7A31E6AF",
            WindowTitle = string.Empty,
            RequiredChildWindowClasses = [],
            TrustedRootWindowTitle = "USB Web Launcher",
            TrustedRootWindowClassPrefix = "HwndWrapper[UsbWebLauncher.App;",
            InputWindowClass = "Edit",
            InputControlId = 1010,
            SubmitWindowClass = "Button",
            SubmitControlId = 1
        };

    private static ClientCertificateNativePromptOptions CreateValidTrustedClientTopLevelPrompt() =>
        CreateValidNativePrompt() with
        {
            WindowScope = NativePromptWindowScopes.TrustedClientTopLevel,
            ProcessPath = string.Empty,
            ProcessSha256 = string.Empty,
            ProcessFileName = "UsbWebLauncher.App.exe",
            PublisherCertificateThumbprint = string.Empty,
            WindowTitle = "验证通用盾密码",
            RequiredChildWindowClasses = [],
            TrustedRootWindowTitle = string.Empty,
            TrustedRootWindowClassPrefix = string.Empty,
            InputWindowClass = "Edit",
            InputControlId = 1010,
            SubmitWindowClass = "Button",
            SubmitControlId = 1
        };

    private static ClientCertificateNativePromptOptions CreateValidPsbcStrictTopLevelPrompt() =>
        CreateValidNativePrompt() with
        {
            PolicyId = "psbc-uk-password-v1",
            WindowScope = NativePromptWindowScopes.TrustedClientStrictTopLevel,
            ProcessPath = string.Empty,
            ProcessSha256 = "8B6FABDF9F4EBB331CB7325BB9947E4E3F9A2B5345602FEEACF277B1F9F355BE",
            ProcessFileName = "msedgewebview2.exe",
            PublisherCertificateThumbprint = "4028CAD637509D4744B17EC5B42AED8D7A31E6AF",
            WindowTitle = "校验UK密码",
            WindowClass = "#32770",
            RequiredChildWindowClasses = [],
            TrustedRootWindowTitle = string.Empty,
            TrustedRootWindowClassPrefix = string.Empty,
            InputWindowClass = "Edit",
            InputControlId = 3005,
            SubmitWindowClass = "Button",
            SubmitControlId = 1
        };
}
