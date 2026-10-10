using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Browser;

public sealed class CertificateLoginPagePolicyTests
{
    private static readonly Uri Entry = new("https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN?TXCODE=N7N010&LOGIN_TYPE=UC");
    private static readonly ClientCertificateAuthenticationOptions Authentication = new(
        "test", "b2b3.ccb.com", 443, null!, null!, "synthetic-credential-target",
        AdditionalEndpoints: [new("xc.b2b3.ccb.com", 443)]);

    [Theory]
    [InlineData("b2b3.ccb.com")]
    [InlineData("xc.b2b3.ccb.com")]
    public void AllowsOnlyConfiguredBankCertificateEndpointLoginPages(string host)
    {
        var page = new Uri($"https://{host}/NCCB/V6/b2bindex.jsp?PT_LANGUAGE=CN");
        Assert.True(WpfCertificateLoginPage.IsAllowedPage(page, Entry, Authentication));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(page, Entry,
            Authentication with { Host = "other.example", AdditionalEndpoints = [] }));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(page, Entry,
            Authentication with { Port = 444, AdditionalEndpoints = [new(host, 444)] }));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(new Uri($"https://{host}/NCCB/transfer"), Entry, Authentication));
    }

    [Fact]
    public void EntryPageMustBeTheConfiguredCertificateLoginTransaction()
    {
        Assert.True(WpfCertificateLoginPage.IsAllowedPage(Entry, Entry, null));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(new Uri(Entry.AbsoluteUri.Replace("N7N010", "PAYMENT")), Entry, null));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(new Uri(Entry.AbsoluteUri + "&TXCODE=PAYMENT"), Entry, null));
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(new Uri(Entry.AbsoluteUri.Replace("LOGIN_TYPE=UC", "LOGIN_TYPE=UM")), Entry, null));
    }

    [Theory]
    [InlineData("http://b2bbank.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2bbank.ccb.com:444/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2bbank.ccb.com.evil.example/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://user@b2bbank.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2bbank.ccb.com/NCCB/transfer")]
    [InlineData("https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp")]
    public void RejectsUnconfiguredOrNonLoginPages(string url) =>
        Assert.False(WpfCertificateLoginPage.IsAllowedPage(new Uri(url), Entry, null));
}
