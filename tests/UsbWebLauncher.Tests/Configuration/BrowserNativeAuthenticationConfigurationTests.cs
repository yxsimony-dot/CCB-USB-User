using System.Text.Json;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class BrowserNativeAuthenticationConfigurationTests
{
    [Fact]
    public void BrowserNative_ParsesWithoutPerKeyBindings()
    {
        var auth = Options() with { Certificate = null!, CryptographicProvider = null!, TokenUnlock = null };
        var json = JsonSerializer.Serialize(new LauncherProfile("https://bank.example/", ClientCertificateAuthentication: auth,
            UsbDetectionStrategy: "ccbUsbStor"));
        var profile = LauncherProfileJson.Parse(json);
        Assert.True(profile.ClientCertificateAuthentication!.UsesBrowserNativeAuthentication);
        Assert.True(LauncherProfileValidator.Validate(profile).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    public void BrowserNative_RejectsUnknownAuthenticationMode(string? mode)
    {
        var result = Validate(Options() with { AuthenticationMode = mode! });
        Assert.Contains(result.Errors, error => error.Field == "clientCertificateAuthentication.authenticationMode");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrowserNative_RequiresEnabledNativePrompt(bool missing)
    {
        var auth = Options();
        auth = auth with { NativePromptContinuation = missing ? null : auth.NativePromptContinuation! with { Enabled = false } };
        Assert.False(Validate(auth).IsValid);
    }

    [Fact]
    public void BrowserNative_IgnoresDormantInvalidTokenBindingButStillValidatesPromptAndEndpoint()
    {
        var auth = Options() with { Certificate = null!, CryptographicProvider = null!, TokenUnlock = new("Unknown") };
        Assert.True(Validate(auth).IsValid);
        Assert.False(Validate(auth with { Host = "https://bad.example" }).IsValid);
        Assert.False(Validate(auth with { NativePromptContinuation = auth.NativePromptContinuation! with { InputControlId = 0 } }).IsValid);
    }

    [Fact]
    public void BrowserNative_HashIgnoresPerKeyMetadataAndSupportsAbsentBindings()
    {
        var first = Options();
        var second = first with
        {
            Certificate = new(new string('B', 40), "different subject", "different issuer"),
            CryptographicProvider = new("other provider", 2, "other container", "AT_SIGNATURE"),
            TokenUnlock = new("PKCS11", "C:\\other.dll", "OTHER-SYNTHETIC-SERIAL")
        };
        Assert.Equal(first.GetNormalizedHash(), second.GetNormalizedHash());
        Assert.Equal(first.GetNormalizedHash(), (first with { Certificate = null!, CryptographicProvider = null!, TokenUnlock = null }).GetNormalizedHash());
        Assert.NotEqual(first.GetNormalizedHash(), (first with { AuthenticationMode = "ConfiguredToken" }).GetNormalizedHash());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("credential")]
    [InlineData("endpoint")]
    [InlineData("prompt")]
    public void BrowserNative_HashRetainsSharedAuthenticationPolicy(string changedField)
    {
        var baseline = Options();
        var changed = changedField switch
        {
            "id" => baseline with { Id = "other" },
            "host" => baseline with { Host = "other.example" },
            "port" => baseline with { Port = 8443 },
            "credential" => baseline with { CredentialTargetName = "other-credential" },
            "endpoint" => baseline with { AdditionalEndpoints = [new("other.example", 443)] },
            _ => baseline with { NativePromptContinuation = baseline.NativePromptContinuation! with { InputControlId = 2 } }
        };
        Assert.NotEqual(baseline.GetNormalizedHash(), changed.GetNormalizedHash());
    }

    [Theory]
    [InlineData(0, ClientCertificateRequestMatchStatus.CertificateMissing)]
    [InlineData(1, ClientCertificateRequestMatchStatus.Selected)]
    [InlineData(2, ClientCertificateRequestMatchStatus.CertificateConflict)]
    public void BrowserNative_MatchesAllBrowserOfferedCandidatesWithoutFixedIdentity(int count, ClientCertificateRequestMatchStatus expected)
    {
        var candidates = new[] { new ClientCertificateCandidate(new string('C', 40), "Company C", "Issuer C"),
            new ClientCertificateCandidate(new string('D', 40), "Company D", "Issuer D") }.Take(count).ToArray();
        var request = new ClientCertificateRequestSnapshot(new Uri("https://bank.example/"), false, candidates);
        var result = ClientCertificateRequestMatcher.Match(request, Options(), true);
        Assert.Equal(expected, result.Status);
        if (count == 1) Assert.Same(candidates[0], result.SelectedCertificate);
        else Assert.Null(result.SelectedCertificate);
    }

    [Theory]
    [InlineData("https://bank.example/", true, true, ClientCertificateRequestMatchStatus.InvalidRequest)]
    [InlineData("http://bank.example/", false, true, ClientCertificateRequestMatchStatus.InvalidRequest)]
    [InlineData("https://bank.example.evil/", false, true, ClientCertificateRequestMatchStatus.UnauthorizedTarget)]
    [InlineData("https://bank.example:8443/", false, true, ClientCertificateRequestMatchStatus.UnauthorizedTarget)]
    [InlineData("https://bank.example/", false, false, ClientCertificateRequestMatchStatus.TokenNotUnlocked)]
    [InlineData("https://other.example/", false, true, ClientCertificateRequestMatchStatus.Selected)]
    public void BrowserNative_PreservesRequestAuthorizationAndReadiness(string uri, bool proxy, bool ready, ClientCertificateRequestMatchStatus expected)
    {
        var auth = Options() with { Certificate = null!, CryptographicProvider = null!, AdditionalEndpoints = [new("other.example", 443)] };
        var request = new ClientCertificateRequestSnapshot(new Uri(uri), proxy,
            [new ClientCertificateCandidate(new string('C', 40), "Company C", "Issuer C")]);
        Assert.Equal(expected, ClientCertificateRequestMatcher.Match(request, auth, ready).Status);
    }

    private static ProfileValidationResult Validate(ClientCertificateAuthenticationOptions auth) =>
        LauncherProfileValidator.Validate(new LauncherProfile("https://bank.example/", ClientCertificateAuthentication: auth, UsbDetectionStrategy: "ccbUsbStor"));

    private static ClientCertificateAuthenticationOptions Options() => new(
        "native-test", "bank.example", 443,
        new(new string('A', 40), "Fixed subject", "Fixed issuer"),
        new("Fixed provider", 1, "Fixed container", "AT_KEYEXCHANGE"),
        "synthetic-credential", NativePromptContinuation: new(
            true, "test-pipe", "test-policy", "", "", "", "Synthetic prompt", "#32770", [], 10000,
            WindowScope: NativePromptWindowScopes.TrustedClientTopLevel,
            ProcessFileName: "UsbWebLauncher.App.exe", InputWindowClass: "Edit", InputControlId: 1010,
            SubmitWindowClass: "Button", SubmitControlId: 1), AuthenticationMode: "BrowserNative");
}
