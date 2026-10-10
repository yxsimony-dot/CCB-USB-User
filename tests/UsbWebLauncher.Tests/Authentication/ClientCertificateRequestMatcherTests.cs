// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Reflection;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class ClientCertificateRequestMatcherTests
{
    [Fact]
    public void Match_SelectsSingleMatchingCertificateForAuthorizedHttpsRequest()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/accounts"),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("Selected", GetStatus(result));

        var selectedCertificate = GetPropertyValue(result, "SelectedCertificate");
        Assert.NotNull(selectedCertificate);
        Assert.Equal(
            "1111111111111111111111111111111111111111",
            GetStringProperty(selectedCertificate!, "Sha1Fingerprint"));
    }

    [Fact]
    public void Match_TreatsAuthorizedHostAsCaseInsensitive()
    {
        var result = Match(
            new Uri("https://B2B.CCB.COM/accounts"),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("Selected", GetStatus(result));
    }

    [Fact]
    public void Match_UsesConfiguredDefaultHttpsPortWhenRequestOmitsIt()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/"),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("Selected", GetStatus(result));
    }

    [Fact]
    public void Match_SelectsCertificateForExplicitAdditionalEndpoint()
    {
        var options = CreateAuthenticationOptions() with
        {
            AdditionalEndpoints = [new("xc.b2b3.ccb.com", 443)]
        };
        var result = Match(
            new Uri("https://XC.B2B3.CCB.COM/accounts"),
            isProxy: false,
            isTokenUnlocked: true,
            options,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("Selected", GetStatus(result));
    }

    [Theory]
    [InlineData("https://sub.xc.b2b3.ccb.com/")]
    [InlineData("https://xc.b2b3.ccb.com:8443/")]
    public void Match_DoesNotExpandAdditionalEndpointBeyondExactHostAndPort(string requestUri)
    {
        var options = CreateAuthenticationOptions() with
        {
            AdditionalEndpoints = [new("xc.b2b3.ccb.com", 443)]
        };
        var result = Match(
            new Uri(requestUri),
            isProxy: false,
            isTokenUnlocked: true,
            options,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("UnauthorizedTarget", GetStatus(result));
    }

    [Theory]
    [InlineData("https://evil.b2b.ccb.com/")]
    [InlineData("https://b2b.ccb.com.evil/")]
    [InlineData("https://b2b.ccb.com:8443/")]
    public void Match_RejectsUnauthorizedTargets(string requestUri)
    {
        var result = Match(
            new Uri(requestUri),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("UnauthorizedTarget", GetStatus(result));
        Assert.Null(GetPropertyValue(result, "SelectedCertificate"));
    }

    [Fact]
    public void Match_RejectsProxyCertificateRequests()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/"),
            isProxy: true,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("InvalidRequest", GetStatus(result));
    }

    [Fact]
    public void Match_RejectsNonHttpsCertificateRequests()
    {
        var result = Match(
            new Uri("http://b2b.ccb.com/"),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("InvalidRequest", GetStatus(result));
    }

    [Fact]
    public void Match_ReportsLockedTokenBeforeSelectingCertificate()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/"),
            isProxy: false,
            isTokenUnlocked: false,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("TokenNotUnlocked", GetStatus(result));
    }

    [Fact]
    public void Match_ReportsMissingCertificateWhenNoCandidateMatches()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/"),
            isProxy: false,
            isTokenUnlocked: true,
            ("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("CertificateMissing", GetStatus(result));
    }

    [Fact]
    public void Match_ReportsCertificateConflictWhenMultipleCandidatesMatch()
    {
        var result = Match(
            new Uri("https://b2b.ccb.com/"),
            isProxy: false,
            isTokenUnlocked: true,
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
            ("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"));

        Assert.Equal("CertificateConflict", GetStatus(result));
        Assert.Null(GetPropertyValue(result, "SelectedCertificate"));
    }

    private static object Match(
        Uri requestUri,
        bool isProxy,
        bool isTokenUnlocked,
        params (string Sha1Fingerprint, string Subject, string Issuer)[] certificates)
        => Match(
            requestUri,
            isProxy,
            isTokenUnlocked,
            CreateAuthenticationOptions(),
            certificates);

    private static object Match(
        Uri requestUri,
        bool isProxy,
        bool isTokenUnlocked,
        ClientCertificateAuthenticationOptions options,
        params (string Sha1Fingerprint, string Subject, string Issuer)[] certificates)
    {
        var matcherType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateRequestMatcher");
        var snapshot = CreateRequestSnapshot(requestUri, isProxy, certificates);
        var matchMethod = matcherType.GetMethod(
            "Match",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(matchMethod);

        var result = matchMethod!.Invoke(null, [snapshot, options, isTokenUnlocked]);
        Assert.NotNull(result);
        return result!;
    }

    private static object CreateRequestSnapshot(
        Uri requestUri,
        bool isProxy,
        IEnumerable<(string Sha1Fingerprint, string Subject, string Issuer)> certificates)
    {
        var snapshotType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateRequestSnapshot");
        var certificateType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateCandidate");
        var certificateInstances = Array.CreateInstance(certificateType, certificates.Count());
        var index = 0;

        foreach (var certificate in certificates)
        {
            var instance = Activator.CreateInstance(
                certificateType,
                certificate.Sha1Fingerprint,
                certificate.Subject,
                certificate.Issuer);

            Assert.NotNull(instance);
            certificateInstances.SetValue(instance, index++);
        }

        var snapshot = Activator.CreateInstance(snapshotType, requestUri, isProxy, certificateInstances);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private static Type GetRequiredType(string fullName)
    {
        var type = typeof(LauncherProfile).Assembly.GetType(fullName);
        Assert.NotNull(type);
        return type!;
    }

    private static string GetStatus(object result)
    {
        var status = GetPropertyValue(result, "Status");
        Assert.NotNull(status);
        return status!.ToString()!;
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return property!.GetValue(instance);
    }

    private static string GetStringProperty(object instance, string propertyName)
    {
        var value = GetPropertyValue(instance, propertyName);
        return Assert.IsType<string>(value);
    }

    private static ClientCertificateAuthenticationOptions CreateAuthenticationOptions() => new(
        "ccb-client-certificate",
        "b2b.ccb.com",
        443,
        new ClientCertificateIdentityOptions(
            "1111111111111111111111111111111111111111",
            "GSB00000000003",
            "CCBRSACAROOT"),
        new ClientCertificateCryptographicProviderOptions(
            "CCB HD CSP V3.0",
            1,
            "SAMPLE_KEY_CONTAINER_1_RSA",
            "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-client-certificate");
}
