namespace UsbWebLauncher.App.Browser;

public sealed record StatementPageReply(string Status, string? Stage = null, string? Code = null, StatementExportIdentity? Identity = null);

public interface IStatementExportPage
{
    Task<StatementPageReply> ExecuteAsync(string operation, StatementExportMonth range, string runId, CancellationToken token);
}

public interface IStatementExportDownload : IDisposable, IAsyncDisposable
{
    Task<string> WaitAsync(CancellationToken token);
    ValueTask IAsyncDisposable.DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public sealed class StatementExportException(string message) : Exception(message);

public sealed class StatementExportCoordinator(
    IStatementExportPage page,
    Func<StatementExportIdentity, IStatementExportDownload> createDownload,
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
            progress("正在打开活期账户…");
            await ActionAsync("open-accounts", "opened", range, runId, token);
            await WaitForAsync("accounts", range, runId, token);
            progress("正在选择第一条活期账户…");
            await ActionAsync("select-first", "selected", range, runId, token);
            progress("正在打开日期查询页…");
            await WaitForAsync("query", range, runId, token);
            progress($"正在查询 {range.Year} 年 {range.Month} 月流水…");
            await ActionAsync("query", "queried", range, runId, token);
            await WaitForAsync("results", range, runId, token);
            var identified = await ActionAsync("identify", "identified", range, runId, token);
            if (identified.Status == "empty")
            {
                progress(DocumentExportMessages.EmptyStatement);
                return null;
            }
            var identity = identified.Identity ?? throw Failure("identify", "failed", "naming-identity");
            progress("正在下载全部流水 PDF…");
            await using var download = createDownload(identity);
            await ActionAsync("download", "downloading", range, runId, token);
            return await download.WaitAsync(token);
        }
        finally
        {
            // This only releases per-run in-page state, never submits another action.
            try { await page.ExecuteAsync("dispose", range, runId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }
    }

    private async Task<StatementPageReply> ActionAsync(string operation, string expected, StatementExportMonth range, string runId, CancellationToken token)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                phase.Token.ThrowIfCancellationRequested();
                var result = await page.ExecuteAsync(operation, range, runId, phase.Token);
                if (result.Status == expected || operation == "identify" && result.Status == "empty" && result.Stage == "results") return result;
                if (result.Status != "waiting") throw Failure(operation, result.Status, result.Code);
                await _delay(TimeSpan.FromMilliseconds(350), phase.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StatementExportException("等待银行页面超时，请查看页面后重试。");
        }
    }

    private async Task WaitForAsync(string stage, StatementExportMonth range, string runId, CancellationToken token)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(30));
        var consecutiveUnsupported = 0;
        try
        {
            while (true)
            {
                phase.Token.ThrowIfCancellationRequested();
                var result = await page.ExecuteAsync("inspect", range, runId, phase.Token);
                // Reopening a bank tab briefly exposes the previous query/results
                // while the original account-list request is still in flight.
                if (result.Status == "ready" && result.Stage == stage) return;
                if (result.Status is "ambiguous" or "failed") throw Failure(stage, result.Status, result.Code);
                if (result.Status == "unsupported")
                {
                    // A navigating frame can briefly have no recognizable controls.
                    // Inspect only; never replay a submitted action to recover it.
                    if (++consecutiveUnsupported >= 5) throw Failure(stage, result.Status, result.Code);
                }
                else if (result.Status is "ready" or "waiting")
                {
                    consecutiveUnsupported = 0;
                }
                await _delay(TimeSpan.FromMilliseconds(350), phase.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StatementExportException(stage == "results"
                ? "未等到流水结果，请查看银行页面是否提示日期限制或无流水。"
                : "等待银行页面超时，请查看页面后重试。");
        }
    }

    private static StatementExportException Failure(string step, string status, string? code) => new(
        step == "ensure-home" ? "未能确认网银首页已就绪，请确认已登录并能打开首页后重试。" :
        code == "naming-identity" ? "无法核对公司全名和账号尾号，已停止导出，请查看账户信息。" :
        code == "entry-not-found" ? "未找到活期账户入口，请回到网银首页后重试。" :
        code is "account-mismatch" or "result-mismatch" or "result-identity" or "result-lineage" ? "无法核对结果页的账户或日期，已停止导出，请重新查询。" :
        step == "open-accounts" && status == "unsupported" ? "请先启动业务并登录企业网银，再导出流水。" :
        step == "download" ? "未能确认“下载全部”的 PDF 入口，已停止导出，请查看银行页面。" :
        status == "ambiguous" ? "检测到多个匹配页面或控件，已停止导出，请回到银行首页后重试。" :
        "银行页面未满足导出条件，已停止操作，请查看页面后重试。");
}
