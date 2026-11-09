// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class PsbcClientCertificateSelectorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Select_SelectsExactlyOneCurrentKeyCertificate()
    {
        var candidate = Candidate();

        var result = Select([candidate]);

        Assert.True(result.IsSelected);
        Assert.Equal(
            PsbcClientCertificateSelectionStatus.Selected,
            result.Status);
        Assert.Same(candidate, result.SelectedCertificate);
    }

    [Fact]
    public void Select_RejectsWhenNoCertificateMatchesCurrentKey()
    {
        var result = Select(
        [
            Candidate(container: "SYNTHETIC_OTHER_CONTAINER")
        ]);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.CertificateMissing,
            result.Status);
        Assert.Null(result.SelectedCertificate);
    }

    [Fact]
    public void Select_RejectsMultipleMatchingCertificates()
    {
        var result = Select([Candidate(), Candidate()]);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.CertificateConflict,
            result.Status);
        Assert.Null(result.SelectedCertificate);
    }

    [Theory]
    [InlineData(-2, -1)]
    [InlineData(1, 2)]
    public void Select_RejectsCertificateOutsideValidityPeriod(
        int validFromDays,
        int validToDays)
    {
        var result = Select(
        [
            Candidate(
                validFromUtc: Now.AddDays(validFromDays),
                validToUtc: Now.AddDays(validToDays))
        ]);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.CertificateMissing,
            result.Status);
    }

    [Fact]
    public void Select_RejectsCertificateWithoutClientAuthenticationUsage()
    {
        var result = Select(
        [
            Candidate(
                usages: [new("1.3.6.1.5.5.7.3.1", "Server Authentication")])
        ]);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.CertificateMissing,
            result.Status);
    }

    [Theory]
    [InlineData("https://sub.corpebank.psbc.com/")]
    [InlineData("https://corpebank.psbc.com.evil/")]
    [InlineData("https://corpebank.psbc.com:8443/")]
    public void Select_RejectsNonAuthorizedEndpoint(string requestUri)
    {
        var result = Select(
            [Candidate()],
            requestUri: new Uri(requestUri));

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.UnauthorizedEndpoint,
            result.Status);
    }

    [Fact]
    public void Select_RejectsWhenCurrentBankIsNotPsbc()
    {
        var result = Select(
            [Candidate()],
            context: Context() with { IsPostalSavingsBank = false });

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.WrongBank,
            result.Status);
    }

    [Theory]
    [InlineData(10, 2, 11, 2)]
    [InlineData(10, 2, 10, 3)]
    public void Select_RejectsStaleSessionOrRevision(
        long sessionId,
        long revision,
        long currentSessionId,
        long currentRevision)
    {
        var context = Context() with
        {
            SessionId = sessionId,
            Revision = revision,
            CurrentSessionId = currentSessionId,
            CurrentRevision = currentRevision
        };

        var result = Select([Candidate()], context: context);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.StaleRuntime,
            result.Status);
    }

    [Fact]
    public void Select_RejectsKeyThatIsNotReady()
    {
        var context = Context() with
        {
            Key = UsbKeyDetectionResult.EnvironmentUnavailable(
                UsbKeyVendorFamily.Tendyron,
                UsbKeyDiagnosticCode.ProviderUnavailable,
                @"USB\VID_1780&PID_0401\SYNTHETIC")
        };

        var result = Select([Candidate()], context: context);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.KeyNotReady,
            result.Status);
    }

    [Theory]
    [InlineData("http://corpebank.psbc.com/", false)]
    [InlineData("https://corpebank.psbc.com/", true)]
    public void Select_RejectsInvalidCertificateRequests(
        string requestUri,
        bool isProxy)
    {
        var result = Select(
            [Candidate()],
            requestUri: new Uri(requestUri),
            isProxy: isProxy);

        Assert.Equal(
            PsbcClientCertificateSelectionStatus.InvalidRequest,
            result.Status);
    }

    private static PsbcClientCertificateSelectionResult Select(
        IReadOnlyList<ClientCertificateCandidate> candidates,
        Uri? requestUri = null,
        bool isProxy = false,
        PsbcClientCertificateSelectionContext? context = null) =>
        PsbcClientCertificateSelector.Select(
            new(
                requestUri ?? new Uri("https://corpebank.psbc.com/#/login"),
                isProxy,
                candidates),
            Authentication(),
            context ?? Context());

    private static ClientCertificateAuthenticationOptions Authentication() => new(
        "psbc-client-certificate",
        "corpebank.psbc.com",
        443,
        new(string.Empty, string.Empty, string.Empty),
        new(string.Empty, 0, string.Empty, string.Empty),
        "UsbWebLauncher/ClientCertificate/psbc-client-certificate",
        NativePromptContinuation: new(
            true,
            "UsbWebLauncher.NativePromptBroker.v1",
            "psbc-uk-password",
            string.Empty,
            string.Empty,
            string.Empty,
            "Synthetic PSBC Prompt",
            "#32770",
            [],
            10000),
        AuthenticationMode: "BrowserNative");

    private static PsbcClientCertificateSelectionContext Context() => new(
        IsPostalSavingsBank: true,
        SessionId: 10,
        Revision: 2,
        CurrentSessionId: 10,
        CurrentRevision: 2,
        Key: UsbKeyDetectionResult.Ready(
            UsbKeyVendorFamily.Tendyron,
            @"USB\VID_1780&PID_0401\SYNTHETIC",
            "SYNTHETIC_PSBC_CONTAINER"),
        UtcNow: Now);

    private static ClientCertificateCandidate Candidate(
        string container = "SYNTHETIC_PSBC_CONTAINER",
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? validToUtc = null,
        IReadOnlyList<ClientCertificateEnhancedKeyUsage>? usages = null) =>
        new(
            "1111111111111111111111111111111111111111",
            "Synthetic PSBC Certificate",
            "Synthetic PSBC Issuer",
            "CN=Synthetic PSBC Certificate",
            "CN=Synthetic PSBC Issuer",
            validFromUtc ?? Now.AddDays(-1),
            validToUtc ?? Now.AddDays(1),
            "01020304",
            usages ?? [new("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            new(
                ClientCertificateProviderKind.Csp,
                "Tendyron OnKey193 CSP PSBC v1.0",
                1,
                container,
                "AT_SIGNATURE",
                ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
                ClientCertificateProviderMetadataFailure.None));
}
