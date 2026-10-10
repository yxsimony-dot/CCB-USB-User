using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class ReceiptDownloadPolicyTests
{
    private const string Origin = "https://b2b3.ccb.com";
    private const string Endpoint = Origin + "/NCCB/NECV6FileDownload_09_LH";

    [Theory]
    [InlineData(Endpoint, true)]
    [InlineData(Endpoint + "?download=synthetic", false)]
    [InlineData(Endpoint + "#fragment", false)]
    [InlineData(Endpoint + "/", false)]
    [InlineData(Origin + "/NCCB/NECV6B2BMainPlat_09_LH", false)]
    [InlineData(Origin + "/anything.pdf", false)]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6FileDownload_09_LH", false)]
    [InlineData("https://b2b3.ccb.com.evil.test/NCCB/NECV6FileDownload_09_LH", false)]
    [InlineData("https://username@b2b3.ccb.com/NCCB/NECV6FileDownload_09_LH", false)]
    [InlineData("http://b2b3.ccb.com/NCCB/NECV6FileDownload_09_LH", false)]
    [InlineData("https://b2b3.ccb.com:444/NCCB/NECV6FileDownload_09_LH", false)]
    [InlineData("blob:" + Endpoint, false)]
    [InlineData(null, false)]
    public void MatchesOnlyObservedAttachmentEndpoint(string? raw, bool accepted)
        => Assert.Equal(accepted, ReceiptDownloadPolicy.IsExpectedEndpoint(Origin, raw));

    [Theory]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_08_LH", true)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_10_LH", true)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_123_LH", true)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload__LH", false)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_test_LH", false)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_08_FW", false)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_08_LH/extra", false)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_08_LH?download=synthetic", false)]
    [InlineData(Origin + "/NCCB/NECV6FileDownload_08_LH#fragment", false)]
    [InlineData("https://xc.b2b3.ccb.com/NCCB/NECV6FileDownload_08_LH", false)]
    [InlineData("https://user@b2b3.ccb.com/NCCB/NECV6FileDownload_08_LH", false)]
    [InlineData("http://b2b3.ccb.com/NCCB/NECV6FileDownload_08_LH", false)]
    public void MatchesNumericDownloadRouteVersionsWithoutBroadeningOtherChecks(string raw, bool accepted)
        => Assert.Equal(accepted, ReceiptDownloadPolicy.IsExpectedEndpoint(Origin, raw));

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("http://b2b3.ccb.com")]
    [InlineData("https://b2b3.ccb.com/path")]
    public void InvalidBankOriginCannotAuthorizeDownload(string origin)
        => Assert.False(ReceiptDownloadPolicy.IsExpectedEndpoint(origin, origin + "/NCCB/NECV6FileDownload_09_LH"));

    [Theory]
    [InlineData("application/pdf", "receipt.pdf", true)]
    [InlineData("application/pdf; charset=binary", "download", true)]
    [InlineData("application/octet-stream", "receipt.PDF", true)]
    [InlineData("application/octet-stream", "receipt.zip", false)]
    [InlineData("text/html", "receipt.pdf", false)]
    [InlineData("application/zip", "receipt.pdf", false)]
    [InlineData("application/force-download", "receipt.pdf", true)]
    [InlineData("application/x-download", "receipt.pdf", true)]
    [InlineData("unknown", "receipt.pdf", true)]
    [InlineData(null, "receipt.pdf", true)]
    [InlineData("application/json", "receipt.pdf", false)]
    [InlineData("application/problem+json", "receipt.pdf", false)]
    [InlineData("image/png", "receipt.pdf", false)]
    public void AcceptsOnlyPdfCompatibleMedia(string? mime, string filename, bool accepted)
        => Assert.Equal(accepted, ReceiptDownloadPolicy.IsPdf(mime, filename));
}
