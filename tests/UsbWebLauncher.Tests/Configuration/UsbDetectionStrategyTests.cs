using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class UsbDetectionStrategyTests
{
    [Fact]
    public void Parse_CcbModeDoesNotRequireLegacyFields()
    {
        var profile = LauncherProfileJson.Parse("""
            {"targetUrl":"https://example.com/","usbDetectionStrategy":"ccbUsbStor"}
            """);

        Assert.True(LauncherProfileValidator.Validate(profile).IsValid);
        Assert.Empty(profile.GetUsbDeviceAllowlist());
    }

    [Fact]
    public void Parse_PsbcModeDoesNotRequireLegacyOrCcbAuthenticationFields()
    {
        var profile = LauncherProfileJson.Parse("""
            {"targetUrl":"https://www.psbc.com/","usbDetectionStrategy":"psbcUsbKey"}
            """);

        Assert.True(LauncherProfileValidator.Validate(profile).IsValid);
        Assert.Empty(profile.GetUsbDeviceAllowlist());
        Assert.Null(profile.ClientCertificateAuthentication);
    }

    [Fact]
    public void Parse_PsbcModeRoundTripsWithoutCcbAuthentication()
    {
        var profile = LauncherProfileJson.Parse("""
            {"targetUrl":"https://www.psbc.com/","usbDetectionStrategy":"psbcUsbKey"}
            """);
        var json = System.Text.Json.JsonSerializer.Serialize(
            profile,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        var reparsed = LauncherProfileJson.Parse(json);

        Assert.Equal("psbcUsbKey", reparsed.UsbDetectionStrategy);
        Assert.Null(reparsed.ClientCertificateAuthentication);
    }

    [Fact]
    public void ShippedPsbcProfile_IsIndependentFromCcbProfile()
    {
        var appDirectory = FindRepoRootPath("src", "UsbWebLauncher.App");
        var ccbPath = Path.Combine(
            appDirectory,
            BankFlowCatalog.ChinaConstructionBank.ProfileFileName!);
        var psbcPath = Path.Combine(
            appDirectory,
            BankFlowCatalog.PostalSavingsBank.ProfileFileName!);

        Assert.NotEqual(ccbPath, psbcPath);

        var ccb = LauncherProfileJson.Parse(File.ReadAllText(ccbPath));
        var psbcJson = File.ReadAllText(psbcPath);
        var psbc = LauncherProfileJson.Parse(psbcJson);

        Assert.Equal("ccbUsbStor", ccb.UsbDetectionStrategy);
        Assert.Equal("psbcUsbKey", psbc.UsbDetectionStrategy);
        Assert.Equal("https://corpebank.psbc.com/#/login", psbc.TargetUrl);
        Assert.Null(psbc.UsbDevice);
        Assert.Empty(psbc.GetUsbDeviceAllowlist());
        var authentication = Assert.IsType<ClientCertificateAuthenticationOptions>(
            psbc.ClientCertificateAuthentication);
        var prompt = Assert.IsType<ClientCertificateNativePromptOptions>(
            authentication.NativePromptContinuation);
        Assert.True(authentication.Enabled);
        Assert.True(authentication.UsesBrowserNativeAuthentication);
        Assert.Equal("psbc-client-certificate", authentication.Id);
        Assert.Equal("corpebank.psbc.com", authentication.Host);
        Assert.Equal(443, authentication.Port);
        Assert.Equal(
            "UsbWebLauncher/ClientCertificate/psbc-client-certificate",
            authentication.CredentialTargetName);
        Assert.Null(authentication.Certificate);
        Assert.Null(authentication.CryptographicProvider);
        Assert.Null(authentication.TokenUnlock);
        Assert.True(prompt.Enabled);
        Assert.Equal("psbc-uk-password-v1", prompt.PolicyId);
        Assert.Equal(
            NativePromptWindowScopes.TrustedClientStrictTopLevel,
            prompt.WindowScope);
        Assert.Equal("msedgewebview2.exe", prompt.ProcessFileName);
        Assert.Equal("校验UK密码", prompt.WindowTitle);
        Assert.Equal("Edit", prompt.InputWindowClass);
        Assert.Equal(3005, prompt.InputControlId);
        Assert.Equal("Button", prompt.SubmitWindowClass);
        Assert.Equal(1, prompt.SubmitControlId);
        Assert.Equal(
            "psbc",
            BankFlowCatalog.PostalSavingsBank.BrowserCapabilities.SessionDirectoryName);
        Assert.Equal(
            "--psbc-certificate-diagnostics=",
            ApplicationLaunchOptions.PsbcCertificateDiagnosticsArgumentPrefix);
        Assert.Equal(
            "--psbc-native-window-diagnostics=",
            ApplicationLaunchOptions.PsbcNativeWindowDiagnosticsArgumentPrefix);
        Assert.DoesNotContain("ccb", psbcJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PsbcModeRejectsCcbAuthentication()
    {
        var json = """
            {
              "targetUrl":"https://www.psbc.com/",
              "usbDetectionStrategy":"psbcUsbKey",
              "clientCertificateAuthentication": {
                "id": "ccb-client-certificate",
                "host": "b2b.ccb.com",
                "port": 443,
                "certificate": {
                  "sha1Fingerprint": "1111111111111111111111111111111111111111",
                  "subject": "sample",
                  "issuer": "sample"
                },
                "cryptographicProvider": {
                  "cspName": "CCB CSP",
                  "providerType": 1,
                  "containerName": "sample",
                  "keySpec": "AT_SIGNATURE"
                },
                "credentialTargetName": "UsbWebLauncher/ClientCertificate/ccb"
              }
            }
            """;

        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse(json));

        Assert.Contains("clientCertificateAuthentication", exception.Message);
    }

    [Fact]
    public void Parse_MissingStrategyWithoutLegacyFieldsIsInvalid()
    {
        var exception = Assert.Throws<LauncherProfileException>(
            () => LauncherProfileJson.Parse("""
                {"targetUrl":"https://www.psbc.com/"}
                """));

        Assert.Contains("usbDevice.vendorId", exception.Message);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Parse_RejectsUnsupportedStrategyEvenWithValidLegacyFields(string strategy)
    {
        var json = $$$"""
            {"targetUrl":"https://example.com/","usbDetectionStrategy":{{{strategy}}},
             "usbDevice":{"vendorId":"1677","productId":"0054","busReportedDescription":"uKeyCI800-K156"}}
            """;

        Assert.Throws<LauncherProfileException>(() => LauncherProfileJson.Parse(json));
    }

    private static string FindRepoRootPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !Directory.Exists(Path.Combine(current.FullName, "src")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }
}
