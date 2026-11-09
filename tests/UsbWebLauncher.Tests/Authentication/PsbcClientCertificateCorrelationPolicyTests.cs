// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class PsbcClientCertificateCorrelationPolicyTests
{
    public static TheoryData<UsbKeyVendorFamily> PendingVendorFamilies => new()
    {
        UsbKeyVendorFamily.FeitianEnterSafe,
        UsbKeyVendorFamily.Hdzb,
        UsbKeyVendorFamily.Excelsecu,
        UsbKeyVendorFamily.WatchData
    };

    [Fact]
    public void Correlate_MatchesSupportedTendyronProviderAndExactTokenContainer()
    {
        var result = Correlate(
            ReadyKey(" SYNTHETIC_PSBC_CONTAINER "),
            Candidate(
                container: "SYNTHETIC_PSBC_CONTAINER"));

        Assert.True(result.IsMatch);
        Assert.Equal(
            PsbcClientCertificateCorrelationStatus.Matched,
            result.Status);
    }

    [Theory]
    [MemberData(nameof(PendingVendorFamilies))]
    public void Correlate_ReportsPendingEvidenceForUnsupportedVendorFamilies(
        UsbKeyVendorFamily vendorFamily)
    {
        var result = Correlate(
            ReadyKey("SYNTHETIC_PSBC_CONTAINER", vendorFamily),
            Candidate());

        Assert.False(result.IsMatch);
        Assert.Equal(
            PsbcClientCertificateCorrelationStatus.VendorEvidencePending,
            result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Correlate_RejectsTendyronWhenTokenIdentityIsUnavailable(
        string? tokenIdentity)
    {
        var result = Correlate(ReadyKey(tokenIdentity), Candidate());

        Assert.False(result.IsMatch);
        Assert.Equal(
            PsbcClientCertificateCorrelationStatus.TokenEvidenceUnavailable,
            result.Status);
    }

    [Fact]
    public void Correlate_RejectsKeyThatIsNotReady()
    {
        var key = UsbKeyDetectionResult.EnvironmentUnavailable(
            UsbKeyVendorFamily.Tendyron,
            UsbKeyDiagnosticCode.ProviderUnavailable,
            @"USB\VID_1780&PID_0401\SYNTHETIC");

        var result = Correlate(key, Candidate());

        Assert.False(result.IsMatch);
        Assert.Equal(
            PsbcClientCertificateCorrelationStatus.KeyNotReady,
            result.Status);
    }

    [Theory]
    [InlineData(ClientCertificateProviderKind.Ksp, "Tendyron OnKey193 CSP PSBC v1.0", 1, "AT_SIGNATURE", "SYNTHETIC_PSBC_CONTAINER", PsbcClientCertificateCorrelationStatus.ProviderKindMismatch)]
    [InlineData(ClientCertificateProviderKind.Csp, "Synthetic Other PSBC CSP", 1, "AT_SIGNATURE", "SYNTHETIC_PSBC_CONTAINER", PsbcClientCertificateCorrelationStatus.ProviderNameMismatch)]
    [InlineData(ClientCertificateProviderKind.Csp, "Tendyron OnKey193 CSP PSBC v1.0", 2, "AT_SIGNATURE", "SYNTHETIC_PSBC_CONTAINER", PsbcClientCertificateCorrelationStatus.ProviderTypeMismatch)]
    [InlineData(ClientCertificateProviderKind.Csp, "Tendyron OnKey193 CSP PSBC v1.0", 1, "AT_KEYEXCHANGE", "SYNTHETIC_PSBC_CONTAINER", PsbcClientCertificateCorrelationStatus.KeySpecMismatch)]
    [InlineData(ClientCertificateProviderKind.Csp, "Tendyron OnKey193 CSP PSBC v1.0", 1, "AT_SIGNATURE", "SYNTHETIC_OTHER_CONTAINER", PsbcClientCertificateCorrelationStatus.ContainerMismatch)]
    public void Correlate_RejectsAnyProviderOrContainerMismatch(
        ClientCertificateProviderKind providerKind,
        string providerName,
        uint providerType,
        string keySpec,
        string container,
        PsbcClientCertificateCorrelationStatus expectedStatus)
    {
        var result = Correlate(
            ReadyKey("SYNTHETIC_PSBC_CONTAINER"),
            Candidate(
                providerKind,
                providerName,
                providerType,
                keySpec,
                container));

        Assert.False(result.IsMatch);
        Assert.Equal(expectedStatus, result.Status);
    }

    [Theory]
    [InlineData(ClientCertificateProviderMetadataFailure.PropertyNotFound)]
    [InlineData(ClientCertificateProviderMetadataFailure.AccessDenied)]
    [InlineData(ClientCertificateProviderMetadataFailure.NativeReadFailed)]
    public void Correlate_RejectsUnavailableProviderMetadata(
        ClientCertificateProviderMetadataFailure failure)
    {
        var candidate = Candidate(
            metadataSource: ClientCertificateProviderMetadataSource.None,
            metadataFailure: failure);

        var result = Correlate(
            ReadyKey("SYNTHETIC_PSBC_CONTAINER"),
            candidate);

        Assert.False(result.IsMatch);
        Assert.Equal(
            PsbcClientCertificateCorrelationStatus.ProviderMetadataUnavailable,
            result.Status);
    }

    [Fact]
    public void Correlate_DoesNotTreatFirstCandidateAsFallback()
    {
        var key = ReadyKey("SYNTHETIC_PSBC_CONTAINER");
        var candidates = new[]
        {
            Candidate(container: "SYNTHETIC_FIRST_CONTAINER"),
            Candidate(container: "SYNTHETIC_PSBC_CONTAINER")
        };

        var matches = candidates
            .Where(candidate =>
                PsbcClientCertificateCorrelationPolicy
                    .Correlate(key, candidate)
                    .IsMatch)
            .ToArray();

        Assert.Single(matches);
        Assert.Same(candidates[1], matches[0]);
    }

    private static PsbcClientCertificateCorrelationResult Correlate(
        UsbKeyDetectionResult key,
        ClientCertificateCandidate candidate) =>
        PsbcClientCertificateCorrelationPolicy.Correlate(key, candidate);

    private static UsbKeyDetectionResult ReadyKey(
        string? tokenIdentity,
        UsbKeyVendorFamily vendorFamily = UsbKeyVendorFamily.Tendyron) =>
        UsbKeyDetectionResult.Ready(
            vendorFamily,
            @"USB\VID_1780&PID_0401\SYNTHETIC",
            tokenIdentity);

    private static ClientCertificateCandidate Candidate(
        ClientCertificateProviderKind providerKind =
            ClientCertificateProviderKind.Csp,
        string providerName = "Tendyron OnKey193 CSP PSBC v1.0",
        uint providerType = 1,
        string keySpec = "AT_SIGNATURE",
        string container = "SYNTHETIC_PSBC_CONTAINER",
        ClientCertificateProviderMetadataSource metadataSource =
            ClientCertificateProviderMetadataSource.WindowsCertificateKeyProviderInfoProperty,
        ClientCertificateProviderMetadataFailure metadataFailure =
            ClientCertificateProviderMetadataFailure.None) =>
        new(
            "1111111111111111111111111111111111111111",
            "Synthetic PSBC Certificate",
            "Synthetic PSBC Issuer",
            "CN=Synthetic PSBC Certificate",
            "CN=Synthetic PSBC Issuer",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            "01020304",
            [new("1.3.6.1.5.5.7.3.2", "Client Authentication")],
            new(
                providerKind,
                providerName,
                providerType,
                container,
                keySpec,
                metadataSource,
                metadataFailure));
}
