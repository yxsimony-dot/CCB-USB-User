using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;

namespace UsbWebLauncher.App.Browser;

internal sealed class WpfStatementExporter(WebView2 browser) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task<DocumentExportResult>? _active;
    private bool _disposed;
    public event EventHandler? StateChanged;
    public bool IsBusy { get; private set; }
    public string? Message { get; private set; }

    public Task<DocumentExportResult> ExportAsync(ExportBatchContext context, CancellationToken token)
    {
        if (_disposed || IsBusy) return Task.FromResult(DocumentExportResult.Failed("导出任务忙碌或已关闭。"));
        IsBusy = true;
        _active = RunAsync(context, token);
        return _active;
    }

    private async Task<DocumentExportResult> RunAsync(ExportBatchContext context, CancellationToken token)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        total.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            total.Token.ThrowIfCancellationRequested();
            var range = context.Range;
            var directory = context.Directory;
            // Snapshot settings before yielding; publish the active task before page events.
            await Task.Yield();
            total.Token.ThrowIfCancellationRequested();
            if (browser.CoreWebView2 is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var source) ||
                !StatementDownloadPolicy.IsBankOrigin(source.GetLeftPart(UriPartial.Authority)))
                throw new StatementExportException("请先启动业务并登录企业网银，再导出流水。");
            var origin = source.GetLeftPart(UriPartial.Authority);
            var owner = Window.GetWindow(browser) ?? throw new StatementExportException("浏览器窗口不可用，请重开程序。");
            var coordinator = new StatementExportCoordinator(new Page(browser, origin),
                identity => new WpfStatementDownload(core, origin, new WindowInteropHelper(owner).Handle, identity, directory));
            var file = await coordinator.ExportAsync(range, Publish, total.Token);
            if (file is not null) Publish("已导出：" + file);
            return file is null ? DocumentExportResult.Empty(DocumentExportMessages.EmptyStatement) : DocumentExportResult.Completed(file);
        }
        catch (OperationCanceledException)
        {
            var cancelled = _lifetime.IsCancellationRequested || token.IsCancellationRequested;
            Publish(cancelled ? "导出已取消。" : "导出超时，请查看银行页面后重试。");
            return cancelled ? DocumentExportResult.Cancelled() : DocumentExportResult.Failed(Message!);
        }
        catch (StatementExportException error) { Publish(error.Message); }
        catch (InvalidOperationException) { Publish("当前月份尚未到达，或页面状态已变化，请检查月份后重试。"); }
        catch (IOException) { Publish("PDF 未能保存，请检查下载目录和磁盘空间，或点击“下载目录”重新选择。"); }
        catch (UnauthorizedAccessException) { Publish("无法写入下载目录，请检查访问权限或点击“下载目录”重新选择。"); }
        catch
        {
            // Never expose browser URL, page body, account, or native exception text.
            Publish("导出未完成，请查看银行页面后重试。");
        }
        finally
        {
            IsBusy = false;
            NotifyState();
        }
        return DocumentExportResult.Failed(Message ?? "导出未完成。");
    }

    private void Publish(string message)
    {
        Message = message;
        NotifyState();
    }

    private void NotifyState()
    {
        // Progress display failures must not erase a saved file or skip cleanup.
        try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try { if (_active is not null) await _active; }
        finally { _lifetime.Dispose(); }
    }

    private sealed class Page(WebView2 browser, string origin) : IStatementExportPage
    {
        private static readonly string Script = LoadScript();

        public async Task<StatementPageReply> ExecuteAsync(string operation, StatementExportMonth range, string runId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (browser.CoreWebView2 is not { } core ||
                !Uri.TryCreate(core.Source, UriKind.Absolute, out var source) || source.GetLeftPart(UriPartial.Authority) != origin)
                return new("unsupported");
            var request = JsonSerializer.Serialize(new
            {
                operation, runId, origin, startDate = range.StartDate, endDate = range.EndDate,
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()
            });
            var json = await core.ExecuteScriptAsync("(" + Script + ")(" + request + ")").WaitAsync(token);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return new("failed");
            string? Read(string name) => parsed.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            var status = Read("status") ?? "failed";
            StatementExportIdentity? identity = null;
            if (status == "identified")
            {
                try { identity = StatementExportIdentity.Create(Read("companyName") ?? string.Empty, Read("accountLast4") ?? string.Empty, range.Month); }
                catch (ArgumentException) { return new("failed", "results", "naming-identity"); }
            }
            var reply = new StatementPageReply(status, Read("stage"), Read("code"), identity);
            if (operation != "inspect" && operation != "dispose")
                CertificateLoginDiagnostics.Write("statement-export-step", new
                {
                    operation,
                    status = reply.Status is "home-ready" or "opened" or "selected" or "queried" or "identified" or "empty" or "downloading" or "waiting" or "unsupported" or "ambiguous" or "failed"
                        ? reply.Status : "unknown",
                    stage = reply.Stage is "home" or "accounts" or "query" or "results" or "unsupported" or "ambiguous" ? reply.Stage : "unknown",
                    code = reply.Code is "home-not-found" or "naming-identity" or "entry-not-found" or "result-lineage" or "result-identity" or "account-mismatch"
                        or "download-owner" or "download-menu" or "download-pdf" or "stale" or "run-conflict"
                        or "missing-selection" or "missing-query" or "invalid-date" or "empty-result-unconfirmed" ? reply.Code : "none"
                });
            return reply;
        }

        private static string LoadScript()
        {
            using var stream = typeof(WpfStatementExporter).Assembly.GetManifestResourceStream(
                "UsbWebLauncher.App.Browser.CcbStatementExport.js") ?? throw new InvalidOperationException();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
