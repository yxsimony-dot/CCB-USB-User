using System.Globalization;

namespace UsbWebLauncher.App.Browser;

public sealed record StatementExportMonth
{
    private StatementExportMonth(int year, int month)
    {
        Year = year;
        Month = month;
        StartDate = new DateTime(year, month, 1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        EndDate = new DateTime(year, month, DateTime.DaysInMonth(year, month)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    public int Year { get; }
    public int Month { get; }
    public string StartDate { get; }
    public string EndDate { get; }

    public static StatementExportMonth Create(DateTime today, int month)
    {
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "月份必须在 1 至 12 之间。");
        if (month > today.Month)
            throw new InvalidOperationException("所选月份尚未到达，请选择本年当前月份或之前的月份。");
        return new StatementExportMonth(today.Year, month);
    }
}
