using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class KnownPopupPolicyTests
{
    private const string Page = "https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp";
    private const string Notice = "https://www.ccb.com/chn/html1/subject/25/10/1029wy/html.html";

    [Theory]
    [InlineData("b2b3.ccb.com")]
    [InlineData("xc.b2b3.ccb.com")]
    public void BlocksAnnouncementFromSupportedBankHost(string host) =>
        Assert.True(KnownPopupPolicy.ShouldBlock($"https://{host}/NCCB/V6/b2bindex.jsp?PT_LANGUAGE=CN", Notice, false));

    [Fact]
    public void AnnouncementWithInheritedUserActivationIsStillBlocked() =>
        Assert.True(KnownPopupPolicy.ShouldBlock(Page, Notice, true));

    [Theory]
    [InlineData("https://www.ccb.com/chn/html1/subject/27/02/notice.html?from=login#message")]
    [InlineData("https://www2.ccb.com//chn/2026-10/09/article_2026100916283559202.shtml")]
    [InlineData("https://www2.ccb.com/chn/2027-01/10/article_other.shtml?from=home")]
    [InlineData("https://wealth.ccb.com/chn/home/index.shtml")]
    [InlineData("https://wealth.ccb.com/chn/new-promotion/index.shtml#intro")]
    [InlineData("https://i.xiu.ccb.com/v2/manage/book/cfcrve/")]
    [InlineData("https://i.xiu.ccb.com/v2/manage/book/new-guide/?from=home")]
    public void PublicInformationFamiliesDoNotDependOnArticleIdOrUserActivation(string target)
    {
        Assert.True(KnownPopupPolicy.ShouldBlock(Page, target, false));
        Assert.True(KnownPopupPolicy.ShouldBlock(Page, target, true));
    }

    [Theory]
    [InlineData("https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN?TXCODE=N7N010")]
    [InlineData("https://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_10_FW")]
    public void LoginAndVersionedBusinessSourcesReceiveSamePopupPolicy(string source) =>
        Assert.True(KnownPopupPolicy.ShouldBlock(source, Notice, false));

    [Theory]
    [InlineData("https://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH")]
    [InlineData("https://b2b3.ccb.com/NCCB/NECV6FileDownload_08_LH")]
    [InlineData("https://b2b3.ccb.com/NCCB/NECV6FileDownload_09_LH")]
    [InlineData("https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN")]
    [InlineData("blob:https://b2b3.ccb.com/example-download")]
    [InlineData("about:blank")]
    public void BusinessDownloadsAndAuthenticationAreNeverClaimedByInformationPopupPolicy(string target) =>
        Assert.False(KnownPopupPolicy.ShouldBlock(Page, target, false));

    [Theory]
    [InlineData("https://www.ccb.com/other")]
    [InlineData("https://wealth.ccb.com/other")]
    [InlineData("https://i.xiu.ccb.com/v2/manage/bookish/cfcrve/")]
    [InlineData("https://i.xiu.ccb.com/v2/transactions/")]
    [InlineData("https://www2.ccb.com/chn-like/notice")]
    [InlineData("https://www2.ccb.com.evil.example/chn/notice")]
    [InlineData("https://wealth.ccb.com.evil.example/chn/home/index.shtml")]
    [InlineData("https://i.xiu.ccb.com.evil.example/v2/manage/book/cfcrve/")]
    [InlineData("https://user@wealth.ccb.com/chn/home/index.shtml")]
    [InlineData("https://i.xiu.ccb.com:444/v2/manage/book/cfcrve/")]
    [InlineData("https://www.ccb.com.evil.example/chn/html1/subject/25/10/1029wy/html.html")]
    [InlineData("http://www.ccb.com/chn/html1/subject/25/10/1029wy/html.html")]
    [InlineData("https://www.ccb.com:444/chn/html1/subject/25/10/1029wy/html.html")]
    [InlineData("https://user@www.ccb.com/chn/html1/subject/25/10/1029wy/html.html")]
    [InlineData("about:blank")]
    [InlineData("")]
    public void UnknownWindowNeverBlocked(string destination) =>
        Assert.False(KnownPopupPolicy.ShouldBlock(Page, destination, false));

    [Theory]
    [InlineData("https://evil.example/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2b3.ccb.com.evil.example/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2b3.ccb.com/NCCB-lookalike/transfer")]
    [InlineData("https://b2bbank.ccb.com/outside/login")]
    [InlineData("http://b2b3.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2b3.ccb.com:444/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://user@b2b3.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("")]
    public void UnrecognizedParentCannotActivatePolicy(string source) =>
        Assert.False(KnownPopupPolicy.ShouldBlock(source, Notice, false));

    [Theory]
    [InlineData("usb-launcher-popup:notice-clicked", "popup-notice-clicked")]
    [InlineData("usb-launcher-popup:notice-installed", "popup-notice-installed")]
    [InlineData("usb-launcher-popup:notice-failed", "popup-notice-failed")]
    [InlineData("usb-launcher-popup:mobile-clicked", "popup-mobile-clicked")]
    [InlineData("usb-launcher-popup:guide-clicked", "popup-guide-clicked")]
    [InlineData("usb-launcher-popup:promotion-clicked", "popup-promotion-clicked")]
    [InlineData("SECRET-value", null)]
    public void DiagnosticsMapOnlyFixedMessages(string message, string? expected) =>
        Assert.Equal(expected, KnownPopupPolicy.DiagnosticEvent(message));

    [Theory]
    [InlineData("b2b3.ccb.com", "/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("b2b3.ccb.com", "/NCCB/NECV6B2BMainPlat_08_FW")]
    [InlineData("xc.b2b3.ccb.com", "/NCCB/NECV6B2BMainPlat_09_LH")]
    [InlineData("xc.b2b3.ccb.com", "/NCCB/NECV6B2BMainPlat_08_LH?view=notice")]
    [InlineData("b2b3.ccb.com", "/NCCB/V6/b2bindex.jsp")]
    public void AcceptsPopupMessageFromSameOriginBankDocument(string host, string path) =>
        Assert.True(KnownPopupPolicy.IsPopupMessageSource(
            $"https://{host}{path}", $"https://{host}/NCCB/V6/b2bindex.jsp"));

    [Theory]
    [InlineData("https://b2b3.ccb.com/other")]
    [InlineData("https://b2b3.ccb.com/NCCB-lookalike/notice")]
    [InlineData("https://b2b3.ccb.com/NCCB/../outside")]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH")]
    [InlineData("https://b2b3.ccb.com.evil.example/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("http://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://b2b3.ccb.com:444/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://user@b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("about:blank")]
    [InlineData("")]
    public void RejectsPopupMessageOutsideCurrentBankOrigin(string source) =>
        Assert.False(KnownPopupPolicy.IsPopupMessageSource(source, Page));

    [Theory]
    [InlineData("https://b2b3.ccb.com/NCCB/transfer")]
    [InlineData("https://evil.example/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2b3.ccb.com.evil.example/NCCB/V6/b2bindex.jsp")]
    [InlineData("http://b2b3.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://b2b3.ccb.com:444/NCCB/V6/b2bindex.jsp")]
    [InlineData("https://user@b2b3.ccb.com/NCCB/V6/b2bindex.jsp")]
    [InlineData("")]
    public void RejectsPopupMessageWithoutApprovedTopShell(string topSource) =>
        Assert.False(KnownPopupPolicy.IsPopupMessageSource(
            "https://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH", topSource));
}
