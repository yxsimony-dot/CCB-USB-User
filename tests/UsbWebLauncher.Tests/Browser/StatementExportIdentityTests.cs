using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementExportIdentityTests
{
    [Theory]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    [InlineData(12, "12")]
    public void UsesSelectedMonthAsTwoDigits(int month, string prefix)
    {
        Assert.Equal(prefix + "-测试公司-建行0001-对账", StatementExportIdentity.Create("测试公司", "0001", month).FileStem);
        Assert.Equal(prefix + "-测试公司-建行0001-回单", StatementExportIdentity.CreateReceipt("测试公司", "0001", month).FileStem);
    }

    [Fact]
    public void ReceiptAddsSuffixAndPreservesWholeCompanyAndLeadingZeroes()
    {
        var receipt = StatementExportIdentity.CreateReceipt("  测试服务有限公司工会  ", "0001", 9);
        Assert.Equal(ExportDocumentKind.ElectronicReceipt, receipt.DocumentKind);
        Assert.Equal("测试服务有限公司工会", receipt.CompanyName);
        Assert.Equal("0001", receipt.AccountLast4);
        Assert.Equal("09-测试服务有限公司工会-建行0001-回单", receipt.FileStem);
        Assert.Equal(receipt.FileStem, StatementExportIdentity.Create(receipt.CompanyName, "0001", 9, ExportDocumentKind.ElectronicReceipt).FileStem);
        var statement = StatementExportIdentity.Create(receipt.CompanyName, "0001", 9);
        Assert.Equal(ExportDocumentKind.Statement, statement.DocumentKind);
        Assert.Equal("09-测试服务有限公司工会-建行0001-对账", statement.FileStem);
    }

    [Fact]
    public void ReceiptSanitizesUnsafeCharactersWithoutChangingIdentity()
    {
        const string company = "Example Company\\部门/组:甲*乙?丙\"丁<戊>己|庚\u0001辛";
        var receipt = StatementExportIdentity.CreateReceipt(company, "0123", 9);
        Assert.Equal(company, receipt.CompanyName);
        Assert.Equal("09-Example Company_部门_组_甲_乙_丙_丁_戊_己_庚_辛-建行0123-回单", receipt.FileStem);
    }

    [Fact]
    public void BothKindsReserveSeparatorAndSuffixWithoutTruncatingCompany()
    {
        var company = new string('司', 225);
        var receipt = StatementExportIdentity.CreateReceipt(company, "0507", 9);
        Assert.Equal("09-" + company + "-建行0507-回单", receipt.FileStem);
        Assert.Equal(255, (receipt.FileStem + " (2147483647).pdf").Length);
        Assert.Throws<ArgumentException>(() => StatementExportIdentity.CreateReceipt(company + "司", "0507", 9));
        Assert.Throws<ArgumentException>(() => StatementExportIdentity.Create(company + "司", "0507", 9));
    }

    [Fact]
    public void RejectsUnknownDocumentKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatementExportIdentity.Create("测试公司", "0507", 9, (ExportDocumentKind)99));
    }

    [Fact]
    public void UsesWholeCompanyNameAndPreservesLeadingZeroInAccountSuffix()
    {
        var identity = StatementExportIdentity.Create("  测试服务有限公司分公司工会  ", "0507", 9);
        Assert.Equal("测试服务有限公司分公司工会", identity.CompanyName);
        Assert.Equal("0507", identity.AccountLast4);
        Assert.Equal("09-测试服务有限公司分公司工会-建行0507-对账", identity.FileStem);
    }

    [Fact]
    public void PreservesInternalSpacesAndReplacesUnsafeWindowsCharacters()
    {
        var identity = StatementExportIdentity.Create("Example Company\\部门/组:甲*乙?丙\"丁<戊>己|庚\u0001辛", "0123", 9);
        Assert.Equal("09-Example Company_部门_组_甲_乙_丙_丁_戊_己_庚_辛-建行0123-对账", identity.FileStem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void RejectsMissingCompanyName(string? companyName)
    {
        Assert.ThrowsAny<ArgumentException>(() => StatementExportIdentity.Create(companyName!, "0507", 9));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("507")]
    [InlineData("00507")]
    [InlineData("０５０７")]
    [InlineData("05*7")]
    [InlineData(" 507")]
    [InlineData("0507 ")]
    public void RejectsAnythingOtherThanFourAsciiDigits(string? suffix)
    {
        Assert.ThrowsAny<ArgumentException>(() => StatementExportIdentity.Create("测试公司", suffix!, 9));
    }

    [Fact]
    public void RejectsOverlongCompanyWithoutTruncatingAndLeavesRoomForCollisionSuffix()
    {
        var company = new string('司', 225);
        Assert.Equal("09-" + company + "-建行0507-对账", StatementExportIdentity.Create(company, "0507", 9).FileStem);
        Assert.Throws<ArgumentException>(() => StatementExportIdentity.Create(company + "司", "0507", 9));
    }

    [Fact]
    public void DefaultStringRepresentationDoesNotExposeIdentity()
    {
        var identity = StatementExportIdentity.Create("测试隐私公司", "0507", 9);
        Assert.DoesNotContain("测试隐私公司", identity.ToString());
        Assert.DoesNotContain("0507", identity.ToString());
    }
}
