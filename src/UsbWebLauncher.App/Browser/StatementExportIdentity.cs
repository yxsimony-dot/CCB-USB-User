using System.Globalization;

namespace UsbWebLauncher.App.Browser;

public sealed class StatementExportIdentity
{
    private StatementExportIdentity(string companyName, string accountLast4, string fileStem, ExportDocumentKind documentKind)
    {
        CompanyName = companyName;
        AccountLast4 = accountLast4;
        FileStem = fileStem;
        DocumentKind = documentKind;
    }

    public string CompanyName { get; }
    public string AccountLast4 { get; }
    public string FileStem { get; }
    public ExportDocumentKind DocumentKind { get; }

    public static StatementExportIdentity CreateReceipt(string companyName, string accountLast4, int month) =>
        Create(companyName, accountLast4, month, ExportDocumentKind.ElectronicReceipt);

    public static StatementExportIdentity Create(string companyName, string accountLast4, int month, ExportDocumentKind kind = ExportDocumentKind.Statement)
    {
        var documentSuffix = kind switch
        {
            ExportDocumentKind.Statement => "对账",
            ExportDocumentKind.ElectronicReceipt => "回单",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (accountLast4 is null || accountLast4.Length != 4 || accountLast4.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("账户尾号必须是四位数字。", nameof(accountLast4));

        var company = companyName.Trim();
        var monthPrefix = month.ToString("D2", CultureInfo.InvariantCulture) + "-";
        // Reserve room within Windows' 255-character filename component limit for
        // the separator, bank/suffix, .pdf, and a collision suffix up to " (2147483647)".
        if (company.Length > 230 - documentSuffix.Length - monthPrefix.Length)
            throw new ArgumentException("公司全名过长，无法完整保存为文件名。", nameof(companyName));

        var safeCompany = new string(company.Select(character =>
            char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray());
        return new StatementExportIdentity(company, accountLast4, $"{monthPrefix}{safeCompany}-建行{accountLast4}-{documentSuffix}", kind);
    }
}
