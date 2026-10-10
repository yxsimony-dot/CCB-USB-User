using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementDiagnosticPolicyTests
{
    [Theory]
    [InlineData("https://b2b3.ccb.com/NCCB/query", true)]
    [InlineData("https://ccb.com:443/NCCB/query", true)]
    [InlineData("https://b2b3.ccb.com/other/query", false)]
    [InlineData("http://b2b3.ccb.com/NCCB/query", false)]
    [InlineData("https://ccb.com.evil.test/NCCB/query", false)]
    [InlineData("https://evilccb.com/NCCB/query", false)]
    [InlineData("https://user@ccb.com/NCCB/query", false)]
    [InlineData("https://ccb.com:444/NCCB/query", false)]
    [InlineData("blob:https://ccb.com/NCCB/query", false)]
    [InlineData(null, false)]
    public void BankPageRequiresTrustedOriginAndBusinessPath(string? raw, bool expected)
        => Assert.Equal(expected, StatementDiagnosticPolicy.IsBankPage(raw));

    [Fact]
    public void AddressRetainsRoutingMetadataWithoutValuesOrFragment()
    {
        var json = JsonSerializer.Serialize(StatementDiagnosticPolicy.DescribeAddress(
            "https://b2b3.ccb.com/NCCB/query?TXCODE=secret-value&ACC_NO=123456789&TXCODE=another#private-fragment"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("https://b2b3.ccb.com", document.RootElement.GetProperty("origin").GetString());
        Assert.Equal("/NCCB/query", document.RootElement.GetProperty("path").GetString());
        Assert.Equal(new[] { "ACC_NO", "TXCODE" }, document.RootElement.GetProperty("queryNames").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("123456789", json);
        Assert.DoesNotContain("private-fragment", json);
    }

    [Fact]
    public void RedactsEncodedAccountAndTokenPathSegmentsAndUnsafeQueryNames()
    {
        var json = JsonSerializer.Serialize(StatementDiagnosticPolicy.DescribeAddress(
            "https://b2b3.ccb.com/download/%31%32%33%34%35%36%37%38/0123456789abcdef0123456789abcdef/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789.pdf?%31%32%33%34%35%36=anything&session_12345678=x&START_DATE=20260101"));
        Assert.DoesNotContain("12345678", json);
        Assert.DoesNotContain("0123456789abcdef", json);
        Assert.DoesNotContain("AbCdEfGhIjKlMnOp", json);
        Assert.DoesNotContain("anything", json);
        Assert.DoesNotContain("20260101", json);
        Assert.Contains("START_DATE", json);
        Assert.Contains("redacted", json);
    }

    [Theory]
    [InlineData("https://evil.test/file.pdf")]
    [InlineData("file:///C:/secret.pdf")]
    [InlineData("blob:https://ccb.com/id")]
    public void AddressRejectsUntrustedUris(string raw)
        => Assert.Null(StatementDiagnosticPolicy.DescribeAddress(raw));

    [Theory]
    [InlineData("START_DATE", "START_DATE")]
    [InlineData("accountNumber", "accountNumber")]
    [InlineData("value_12345678", "")]
    [InlineData("123456", "")]
    [InlineData("some secret", "")]
    [InlineData("abcdefabcdefabcdefabcdefabcdefab", "")]
    [InlineData("abcdefff-abcd-abcd-abcd-abcdefabcdef", "")]
    [InlineData("Xg_abC-DefGhiJkLmno_PqRstUvWxYz-abCdEfGhiJkLmno", "")]
    [InlineData(null, "")]
    public void IdentifiersCannotCarryValues(string? raw, string expected)
        => Assert.Equal(expected, StatementDiagnosticPolicy.CleanIdentifier(raw));

    [Fact]
    public void IdentifierDoesNotTruncateUnboundedInputIntoPlausibleMetadata()
        => Assert.Equal("", StatementDiagnosticPolicy.CleanIdentifier(new string('a', 101)));

    [Fact]
    public void DoubleEncodedAccountPathCannotEvadeRedaction()
    {
        var json = JsonSerializer.Serialize(StatementDiagnosticPolicy.DescribeAddress(
            "https://ccb.com/NCCB/%2531%2532%2533%2534%2535%2536"));
        Assert.Contains("redacted", json);
        Assert.DoesNotContain("123456", json);
        Assert.DoesNotContain("2531", json);
    }

    [Theory]
    [InlineData("application/pdf; name=private.pdf", "pdf")]
    [InlineData("Text/HTML; charset=utf-8", "html")]
    [InlineData("application/json", "json")]
    [InlineData("application/octet-stream", "octet-stream")]
    [InlineData("text/plain", "plain")]
    [InlineData("application/xml", "xml")]
    [InlineData("application/javascript", "javascript")]
    [InlineData("x-private/secret", "other")]
    [InlineData(null, "unknown")]
    public void MediaTypeReturnsOnlyCategories(string? value, string expected)
        => Assert.Equal(expected, StatementDiagnosticPolicy.MediaType(value));

    [Theory]
    [InlineData("attachment; filename=12345678.pdf", "attachment")]
    [InlineData("INLINE; filename=secret.pdf", "inline")]
    [InlineData("secret-value", "other")]
    [InlineData(null, "none")]
    public void DispositionDoesNotExposeFilename(string? value, string expected)
        => Assert.Equal(expected, StatementDiagnosticPolicy.Disposition(value));

    [Theory]
    [InlineData("https://ccb.com/NCCB/query", "text/html", true)]
    [InlineData("https://ccb.com/download/report.pdf", "application/pdf", true)]
    [InlineData("https://ccb.com/NCCB/image.png?token=secret", "image/png", false)]
    [InlineData("https://ccb.com/NCCB/main.js", "application/javascript", false)]
    [InlineData("https://ccb.com/NCCB/font.woff2", "font/woff2", false)]
    [InlineData("https://ccb.com/NCCB/style.css", "text/css", false)]
    [InlineData("https://evil.test/NCCB/query", "application/pdf", false)]
    public void ResourceObservationExcludesStaticAndUntrustedResources(string uri, string mime, bool expected)
        => Assert.Equal(expected, StatementDiagnosticPolicy.ShouldObserveResource(uri, mime));
}
