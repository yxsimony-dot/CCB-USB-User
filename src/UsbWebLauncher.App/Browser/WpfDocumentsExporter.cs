using System.IO;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Browser;

internal sealed class WpfDocumentsExporter : IAsyncDisposable
{
    private readonly DownloadDirectorySettings _settings;
    private readonly WpfStatementExporter _statement;
    private readonly WpfReceiptExporter _receipt;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _active;
    private bool _disposed;
    public event EventHandler? StateChanged;
    public bool IsBusy { get; private set; }
    public string? Message { get; private set; }

    public WpfDocumentsExporter(WebView2 browser, DownloadDirectorySettings settings)
    {
        _settings = settings;
        _statement = new(browser);
        _receipt = new(browser);
        _statement.StateChanged += OnChildStateChanged;
        _receipt.StateChanged += OnChildStateChanged;
    }

    public Task ExportAsync(int month, CancellationToken token)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        IsBusy = true;
        _active = RunAsync(month, token);
        return _active;
    }

    private async Task RunAsync(int month, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            // Both stages share exactly one year/month/directory snapshot. Each
            // child retains its own bounded timeout, so stage two gets its full budget.
            var context = ExportBatchContext.Create(DateTime.Now, month, _settings.GetWritableDirectory());
            await Task.Yield();
            var coordinator = new DocumentsExportCoordinator(_statement.ExportAsync, _receipt.ExportAsync);
            var result = await coordinator.ExportAsync(context, Publish, linked.Token);
            Publish(result.Message);
        }
        catch (OperationCanceledException) { Publish("导出已取消。"); }
        catch (InvalidOperationException) { Publish("当前月份尚未到达，请选择本年当前月份或之前的月份。"); }
        catch (IOException) { Publish("无法使用下载目录，请检查目录和磁盘空间，或点击“下载目录”重新选择。"); }
        catch (UnauthorizedAccessException) { Publish("无法写入下载目录，请检查权限或点击“下载目录”重新选择。"); }
        catch { Publish("导出未完成，请查看银行页面后重试。"); }
        finally { IsBusy = false; NotifyState(); }
    }

    private void OnChildStateChanged(object? sender, EventArgs args)
    {
        if (_statement.IsBusy && ReferenceEquals(sender, _statement)) Publish("流水（1/2）：" + _statement.Message);
        else if (_receipt.IsBusy && ReferenceEquals(sender, _receipt)) Publish("回单（2/2）：" + _receipt.Message);
        // Child completion must not unlock the outer command or replace the final summary.
    }

    private void Publish(string message) { Message = message; NotifyState(); }

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
        finally
        {
            _statement.StateChanged -= OnChildStateChanged;
            _receipt.StateChanged -= OnChildStateChanged;
            try { await _statement.DisposeAsync(); }
            finally
            {
                try { await _receipt.DisposeAsync(); }
                finally { _lifetime.Dispose(); }
            }
        }
    }
}
