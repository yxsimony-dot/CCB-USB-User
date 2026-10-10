namespace UsbWebLauncher.App.Browser;

public sealed class ReceiptExportCoordinator(
    IStatementExportPage page,
    Func<StatementExportIdentity, string, IStatementExportDownload> createDownload,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<string?> ExportAsync(StatementExportMonth range, Action<string> progress, CancellationToken token)
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            progress("正在确认并返回网银首页…");
            await ActionAsync("ensure-home", "home-ready", range, runId, token);
            progress("正在打开电子回单查询…");
            await ActionAsync("open", "opened", range, runId, token);
            progress($"正在查询 {range.Year} 年 {range.Month} 月电子回单…");
            await ActionAsync("query", "queried", range, runId, token);
            progress("正在核对第一页回单…");
            var identified = await ActionAsync("identify", "identified", range, runId, token);
            if (identified.Status == "empty") { progress("该月无回单。"); return null; }
            var identity = identified.Identity;
            if (identity is null || identity.DocumentKind != ExportDocumentKind.ElectronicReceipt)
                throw Failure("identify", "failed", "naming-identity");
            await using var download = createDownload(identity, runId);
            progress("正在全选并下载第一页电子回单 PDF…");
            await ActionAsync("download", "downloading", range, runId, token);
            return await download.WaitAsync(token);
        }
        finally
        {
            try { await page.ExecuteAsync("dispose", range, runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }
    }

    private async Task<StatementPageReply> ActionAsync(string operation, string expected,
        StatementExportMonth range, string runId, CancellationToken token)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                phase.Token.ThrowIfCancellationRequested();
                var reply = await page.ExecuteAsync(operation, range, runId, phase.Token).WaitAsync(phase.Token);
                if (reply.Status == expected || operation == "identify" && reply.Status == "empty") return reply;
                if (reply.Status != "waiting") throw Failure(operation, reply.Status, reply.Code);
                await _delay(TimeSpan.FromMilliseconds(350), phase.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StatementExportException("等待电子回单页面超时，请查看银行页面后重试。");
        }
    }

    private static StatementExportException Failure(string operation, string status, string? code) => new(
        operation == "ensure-home" ? "未能确认网银首页已就绪，请确认已登录并能打开首页后重试。" :
        code == "naming-identity" ? "无法核对公司全名和账号尾号，已停止回单导出，请查看账户信息。" :
        operation == "download" ? "未能确认第一页回单及预览/打印入口，已停止导出，请查看银行页面。" :
        status == "ambiguous" ? "检测到多个匹配页面或控件，已停止回单导出，请回到银行首页后重试。" :
        "银行页面未满足回单导出条件，已停止操作，请查看页面中的账户、日期和筛选后重试。");
}
