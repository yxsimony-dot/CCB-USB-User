using System.Globalization;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementExportMonthTests
{
    [Theory]
    [InlineData(2024, 9, 2, "20240201", "20240229")]
    [InlineData(2025, 9, 2, "20250201", "20250228")]
    [InlineData(2026, 9, 9, "20260901", "20260930")]
    [InlineData(2027, 1, 1, "20270101", "20270131")]
    [InlineData(2026, 12, 12, "20261201", "20261231")]
    public void UsesCurrentYearAndWholeCalendarMonth(int year, int currentMonth, int month, string start, string end)
    {
        var result = StatementExportMonth.Create(new DateTime(year, currentMonth, 2), month);
        Assert.Equal(year, result.Year);
        Assert.Equal(month, result.Month);
        Assert.Equal(start, result.StartDate);
        Assert.Equal(end, result.EndDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void RejectsInvalidMonth(int month) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => StatementExportMonth.Create(new DateTime(2026, 9, 30), month));

    [Fact]
    public void RejectsFutureMonthInsteadOfChoosingPreviousYear()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StatementExportMonth.Create(new DateTime(2026, 1, 1), 12));
        Assert.Contains("尚未到达", error.Message);
    }

    [Fact]
    public void DatesUseGregorianCalendarRegardlessOfUiCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var result = StatementExportMonth.Create(new DateTime(2026, 9, 30), 9);
            Assert.Equal("20260901", result.StartDate);
            Assert.Equal("20260930", result.EndDate);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
