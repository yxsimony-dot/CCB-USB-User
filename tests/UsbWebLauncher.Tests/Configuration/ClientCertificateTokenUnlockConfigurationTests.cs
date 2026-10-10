// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Security.Cryptography;
using System.Text;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class ClientCertificateTokenUnlockConfigurationTests
{
    [Fact]
    public void Validate_AcceptsAnAbsolutePkcs11ModuleAndExactTokenSerial()
    {
        var result = LauncherProfileValidator.Validate(CreateProfile(
            new("Pkcs11", @"C:\Windows\System32\CCB_HDZB_2G_P11.dll", "SAMPLE_KEY_CONTAINER_1")));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Unknown", @"C:\Windows\System32\module.dll", "SERIAL", "method")]
    [InlineData("Pkcs11", "module.dll", "SERIAL", "modulePath")]
    [InlineData("Pkcs11", @"C:\Windows\System32\module.exe", "SERIAL", "modulePath")]
    [InlineData("Pkcs11", @"C:\Windows\System32\module.dll", "", "tokenSerialNumber")]
    public void Validate_RejectsUnsafeOrIncompleteTokenUnlockConfiguration(
        string method,
        string modulePath,
        string tokenSerial,
        string expectedField)
    {
        var result = LauncherProfileValidator.Validate(CreateProfile(
            new(method, modulePath, tokenSerial)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field.Contains(expectedField, StringComparison.Ordinal));
    }

    [Fact]
    public void AuthenticationHash_ChangesWhenTokenUnlockIdentityChanges()
    {
        var first = CreateAuthentication(
            new("Pkcs11", @"C:\Windows\System32\first.dll", "SERIAL-A"));
        var second = CreateAuthentication(
            new("Pkcs11", @"C:\Windows\System32\second.dll", "SERIAL-A"));
        var third = CreateAuthentication(
            new("Pkcs11", @"C:\Windows\System32\first.dll", "SERIAL-B"));

        Assert.NotEqual(first.GetNormalizedHash(), second.GetNormalizedHash());
        Assert.NotEqual(first.GetNormalizedHash(), third.GetNormalizedHash());
    }

    [Fact]
    public void Validate_KeepsLegacyProfilesOnTheCryptoApiDefault()
    {
        var profile = CreateProfile(null);

        var result = LauncherProfileValidator.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Null(profile.ClientCertificateAuthentication!.TokenUnlock);
    }

    [Fact]
    public void AuthenticationHash_PreservesTheLegacyCryptoApiValidationStamp()
    {
        var legacy = CreateAuthentication(null);
        var explicitCryptoApi = CreateAuthentication(new("CryptoApi"));
        // Golden SHA-256 of the synthetic fixture's 11 legacy fields, UTF-8 encoded
        // and LF-delimited with no trailing newline; computed independently of GetNormalizedHash.
        const string previousHash = "5984A456D497A0354065C63A4C0888A0297C49488B005B484D5266E02A8EE97F";

        Assert.Equal(previousHash, legacy.GetNormalizedHash());
        Assert.Equal(previousHash, explicitCryptoApi.GetNormalizedHash());
    }

    [Fact]
    public void AuthenticationHash_DoesNotCollideWhenNativePromptFieldsContainNewlines()
    {
        var first = CreateAuthentication(null) with
        {
            NativePromptContinuation = new ClientCertificateNativePromptOptions(
                true,
                "UsbWebLauncher.NativePromptBroker.v1",
                "ccb-hdzb-client-certificate-v1",
                @"C:\Program Files (x86)\CCBComponents\Detector\CCBDetector.exe",
                "EDB0029E220ABCC32676B7AA90B2E012C4FB84AF0AD891BB0BA6B0F021FDB607",
                "3723D4D6F69F5999BC6216222A69886352EDBF75",
                "A",
                "B\nC",
                ["D"],
                10000)
        };

        var second = first with
        {
            NativePromptContinuation = first.NativePromptContinuation! with
            {
                WindowTitle = "A\nB",
                WindowClass = "C"
            }
        };

        Assert.NotEqual(first.GetNormalizedHash(), second.GetNormalizedHash());
    }

    private static LauncherProfile CreateProfile(ClientCertificateTokenUnlockOptions? tokenUnlock) =>
        new(
            "https://b2b.ccb.com/",
            new("1677", "0054", "uKeyCI800-K156"),
            CreateAuthentication(tokenUnlock));

    private static ClientCertificateAuthenticationOptions CreateAuthentication(
        ClientCertificateTokenUnlockOptions? tokenUnlock) =>
        new(
            "ccb-client-certificate",
            "b2b.ccb.com",
            443,
            new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
            new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
            "UsbWebLauncher/ClientCertificate/ccb-client-certificate",
            tokenUnlock);
}
