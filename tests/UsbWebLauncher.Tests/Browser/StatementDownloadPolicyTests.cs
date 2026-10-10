using UsbWebLauncher.App.Browser;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementDownloadPolicyTests
{
    private const string Origin = "https://b2b3.ccb.com";
    private const string Page = Origin + "/NCCB/NECV6B2BMainPlat_09_LH";

    [Fact]
    public void NavigationAbortedBeforeDownloadStartingDoesNotStopPendingDownload()
        => Assert.False(StatementDownloadPolicy.ShouldFailNavigation(false, CoreWebView2WebErrorStatus.ConnectionAborted, false));

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.HostNameNotResolved)]
    [InlineData(CoreWebView2WebErrorStatus.CannotConnect)]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionReset)]
    [InlineData(CoreWebView2WebErrorStatus.Disconnected)]
    [InlineData(CoreWebView2WebErrorStatus.Timeout)]
    [InlineData(CoreWebView2WebErrorStatus.CertificateExpired)]
    [InlineData(CoreWebView2WebErrorStatus.CertificateIsInvalid)]
    [InlineData(CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse)]
    public void GenuineNavigationFailuresStillStopBeforeDownloadStarts(CoreWebView2WebErrorStatus status)
        => Assert.True(StatementDownloadPolicy.ShouldFailNavigation(false, status, false));

    [Fact]
    public void CanceledNavigationWaitsForDownload()
        => Assert.False(StatementDownloadPolicy.ShouldFailNavigation(false, CoreWebView2WebErrorStatus.OperationCanceled, false));

    [Fact]
    public void StartedDownloadRemainsAuthoritativeAfterNavigationError()
        => Assert.False(StatementDownloadPolicy.ShouldFailNavigation(false, CoreWebView2WebErrorStatus.ConnectionReset, true));

    [Fact]
    public void SuccessfulNavigationDoesNotStopPendingDownload()
        => Assert.False(StatementDownloadPolicy.ShouldFailNavigation(true, CoreWebView2WebErrorStatus.Unknown, false));

    [Theory]
    [InlineData(Page, Page, true)]
    [InlineData(Page + "?SKEY=not-logged", Page, true)]
    [InlineData("https://www.ccb.com/notice", Page, false)]
    [InlineData(Page, "https://b2b3.ccb.com.evil.test/NCCB/NECV6B2BMainPlat_09_LH", false)]
    [InlineData(Page, Origin + "/NCCB/login", false)]
    [InlineData("http://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_09_LH", Page, false)]
    public void OnlyExpectedOriginAndResultFrameMayRequestOwnedWindow(string target, string source, bool expected)
        => Assert.Equal(expected, StatementDownloadPolicy.IsExpectedWindow(Origin, target, source));

    [Theory]
    [InlineData("08")]
    [InlineData("10")]
    [InlineData("123")]
    public void NumericBankRouteVersionsCanOpenStatementDownloadWindow(string version)
    {
        var page = Origin + "/NCCB/NECV6B2BMainPlat_" + version + "_LH";
        Assert.True(StatementDownloadPolicy.IsExpectedWindow(Origin, page + "?download=synthetic", page));
    }

    [Theory]
    [InlineData("08")]
    [InlineData("09")]
    [InlineData("12")]
    public void FwResultFrameCanOpenLhPrintEndpointButCannotBePrintTarget(string version)
    {
        var print = Origin + "/NCCB/NECV6B2BMainPlat_" + version + "_LH";
        var source = Origin + "/NCCB/NECV6B2BMainPlat_" + version + "_FW";
        Assert.True(StatementDownloadPolicy.IsExpectedWindow(Origin, print, source));
        Assert.False(StatementDownloadPolicy.IsExpectedWindow(Origin, source, print));
    }

    [Theory]
    [InlineData(Origin + "/NCCB/NECV6B2BMainPlat__LH")]
    [InlineData(Origin + "/NCCB/NECV6B2BMainPlat_test_LH")]
    [InlineData(Origin + "/NCCB/NECV6B2BMainPlat_08_LH/extra")]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("https://user@b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    [InlineData("http://b2b3.ccb.com/NCCB/NECV6B2BMainPlat_08_LH")]
    public void VersionCompatibilityStillRequiresExactRouteFamilyAndSameBankOrigin(string otherPage)
    {
        Assert.False(StatementDownloadPolicy.IsExpectedWindow(Origin, otherPage, Page));
        Assert.False(StatementDownloadPolicy.IsExpectedWindow(Origin, Page, otherPage));
    }

    [Theory]
    [InlineData(Page, "application/pdf", true)]
    [InlineData(Page, "text/html", false)]
    [InlineData(Origin + "/NCCB/file.pdf", "application/octet-stream", true)]
    [InlineData("https://evil.test/file.pdf", "application/pdf", false)]
    [InlineData("blob:https://b2b3.ccb.com/abc", "application/pdf", true)]
    [InlineData("blob:https://evil.test/abc", "application/pdf", false)]
    public void PdfCandidateRequiresMatchingOriginAndPdfType(string uri, string mime, bool expected)
        => Assert.Equal(expected, StatementDownloadPolicy.IsPdf(Origin, uri, mime));

    [Fact]
    public void GenericDownloadEndpointCanSupplyPdfFilenameButNeverHtml()
    {
        Assert.True(StatementDownloadPolicy.IsPdf(Origin, Page, "application/octet-stream", "statement.pdf"));
        Assert.False(StatementDownloadPolicy.IsPdf(Origin, Page, "text/html", "statement.pdf"));
        Assert.False(StatementDownloadPolicy.IsPdf(Origin, Page, "application/octet-stream", "statement.csv"));
    }
}
