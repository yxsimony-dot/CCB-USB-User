namespace UsbWebLauncher.App.Browser;

public sealed record ExportBatchContext
{
    private ExportBatchContext(StatementExportMonth range, string directory) { Range = range; Directory = directory; }
    public StatementExportMonth Range { get; }
    public string Directory { get; }
    public static ExportBatchContext Create(DateTime today, int month, string directory) =>
        new(StatementExportMonth.Create(today, month), System.IO.Path.GetFullPath(directory));
}

public enum DocumentExportStatus { Completed, Empty, Failed, Cancelled }

public static class DocumentExportMessages
{
    public const string EmptyStatement = "流水页面为空，不需要导出该月份流水。";
}

public sealed record DocumentExportResult
{
    private DocumentExportResult(DocumentExportStatus status, string message, string? filePath = null)
    { Status = status; Message = message; FilePath = filePath; }
    public DocumentExportStatus Status { get; }
    public string Message { get; }
    public string? FilePath { get; }
    public static DocumentExportResult Completed(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new(DocumentExportStatus.Completed, "已保存：" + filePath, filePath);
    }
    public static DocumentExportResult Empty(string message) => new(DocumentExportStatus.Empty, message);
    public static DocumentExportResult Failed(string message) => new(DocumentExportStatus.Failed, message);
    public static DocumentExportResult Cancelled() => new(DocumentExportStatus.Cancelled, "导出已取消。");
}

public sealed record DocumentsExportResult(DocumentExportResult Statement, DocumentExportResult? Receipt)
{
    public string Message => Statement.Status != DocumentExportStatus.Completed ? Statement.Message :
        "流水已保存：" + Statement.FilePath + Environment.NewLine +
        (Receipt?.Status == DocumentExportStatus.Completed ? "第一页回单已保存：" + Receipt.FilePath :
            Receipt?.Status == DocumentExportStatus.Failed ? "回单导出失败：" + Receipt.Message + Environment.NewLine +
                "可通过“环境维护 → 导出诊断日志”保存日志后提供分析。" :
            Receipt?.Status == DocumentExportStatus.Cancelled ? "回单" + Receipt.Message :
            Receipt?.Message ?? "回单尚未导出。");
}

public sealed class DocumentsExportCoordinator(
    Func<ExportBatchContext, CancellationToken, Task<DocumentExportResult>> exportStatement,
    Func<ExportBatchContext, CancellationToken, Task<DocumentExportResult>> exportReceipt)
{
    public async Task<DocumentsExportResult> ExportAsync(ExportBatchContext context, Action<string> progress, CancellationToken token)
    {
        Report(progress, "正在导出流水（1/2）…");
        var statement = await RunAsync(exportStatement, context, token);
        if (statement.Status != DocumentExportStatus.Completed) return new(statement, null);
        // A full exporter Task includes its receiver and page-state cleanup.
        // Never begin the second navigation merely because a download was started.
        if (token.IsCancellationRequested) return new(statement, DocumentExportResult.Cancelled());
        Report(progress, "流水已保存，正在导出第一页回单（2/2）…");
        var receipt = await RunAsync(exportReceipt, context, token);
        return new(statement, receipt);
    }

    private static void Report(Action<string> progress, string message)
    {
        try { progress(message); }
        catch { /* Progress subscribers cannot change an export's file result. */ }
    }

    private static async Task<DocumentExportResult> RunAsync(
        Func<ExportBatchContext, CancellationToken, Task<DocumentExportResult>> export,
        ExportBatchContext context, CancellationToken token)
    {
        if (token.IsCancellationRequested) return DocumentExportResult.Cancelled();
        try { return await export(context, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return DocumentExportResult.Cancelled(); }
        catch (StatementExportException error) { return DocumentExportResult.Failed(error.Message); }
        catch { return DocumentExportResult.Failed("导出未完成，请查看银行页面后重试。"); }
    }
}
