using System.IO;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

// The observed receipt POST downloads on the existing WebView, without a child
// window. DownloadStarting has no source frame: ownership additionally requires
// the live page adapter to verify this run's consumed print action and document.
internal sealed class WpfReceiptDownload : IStatementExportDownload
{
    private readonly IReceiptDownloadSource _source;
    private readonly string _origin;
    private readonly string _runId;
    private readonly Func<string, CancellationToken, Task<bool>> _verifyPrintSource;
    private readonly StatementPdfFile _file;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout;
    private IReceiptDownloadOperation? _download;
    private Task? _pendingVerification;
    private Action? _completeDeferral;
    private bool _pending;
    private bool _disposed;

    public WpfReceiptDownload(CoreWebView2 parent, string origin, string runId, StatementExportIdentity identity,
        string directory, Func<string, CancellationToken, Task<bool>> verifyPrintSource)
        : this(new WebViewSource(parent), origin, runId, identity, directory, verifyPrintSource) { }

    internal WpfReceiptDownload(IReceiptDownloadSource source, string origin, string runId, StatementExportIdentity identity,
        string directory, Func<string, CancellationToken, Task<bool>> verifyPrintSource, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(verifyPrintSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (identity.DocumentKind != ExportDocumentKind.ElectronicReceipt)
            throw new ArgumentException("下载命名必须属于本次电子回单。", nameof(identity));
        if (!StatementDownloadPolicy.IsBankOrigin(origin)) throw new ArgumentException("无法确认银行来源。", nameof(origin));
        _source = source;
        _origin = origin;
        _runId = runId;
        _verifyPrintSource = verifyPrintSource;
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
        _file = StatementPdfFile.Create(directory, identity);
        try { _source.DownloadStarting += OnDownloadStarting; }
        catch { Dispose(); throw; }
    }

    private void OnDownloadStarting(object? sender, IReceiptDownloadStarting args)
    {
        if (_disposed || _pending || _completed.Task.IsCompleted || _download is not null ||
            !ReferenceEquals(sender, _source) || args.Handled || args.Cancel) return;
        try
        {
            if (!ReceiptDownloadPolicy.IsExpectedEndpoint(_origin, args.Operation.Uri)) return;
            _pending = true;
            var complete = args.GetCompleteDeferral();
            var completed = false;
            _completeDeferral = () => { if (!completed) { completed = true; complete(); } };
            _pendingVerification = ClaimAsync(args, _completeDeferral);
        }
        catch { _pending = false; Fail("无法接收电子回单下载，请重试。"); }
    }

    private async Task ClaimAsync(IReceiptDownloadStarting args, Action complete)
    {
        var verified = false;
        try
        {
            verified = await _verifyPrintSource(_runId, _lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
            if (_disposed || !verified) return;
            if (!ReceiptDownloadPolicy.IsPdf(args.Operation.MimeType, args.ResultFilePath))
            {
                args.Cancel = true;
                args.Handled = true;
                Fail("银行返回的电子回单不是 PDF，已停止导出。");
                return;
            }
            _download = args.Operation;
            args.ResultFilePath = _file.TempPath;
            args.Handled = true;
            _download.StateChanged += OnStateChanged;
            OnStateChanged(_download, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch
        {
            if (verified && !_disposed)
            {
                try { args.Cancel = true; args.Handled = true; } catch { }
            }
            Fail("无法核对或保存本次电子回单，请查看银行页面后重试。");
        }
        finally
        {
            try { complete(); } catch { }
            if (ReferenceEquals(_completeDeferral, complete)) _completeDeferral = null;
            _pending = false;
        }
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (_disposed || _download is null || !ReferenceEquals(sender, _download)) return;
        try
        {
            if (_download.State == ReceiptDownloadState.Completed) _completed.TrySetResult(true);
            else if (_download.State == ReceiptDownloadState.Interrupted) Fail("电子回单 PDF 下载中断，请重试。");
        }
        catch { Fail("无法确认电子回单 PDF 下载完成。"); }
    }

    private void Fail(string message)
    {
        if (!_disposed) _completed.TrySetException(new StatementExportException(message));
    }

    public async Task<string> WaitAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(_timeout);
        try
        {
            await _completed.Task.WaitAsync(timeout.Token);
            return await _file.CompleteAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_disposed)
        {
            Dispose();
            throw new StatementExportException("等待电子回单 PDF 下载超时，请查看银行页面后重试。");
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try { _source.DownloadStarting -= OnDownloadStarting; } catch { }
        try { _completeDeferral?.Invoke(); } catch { }
        _completeDeferral = null;
        if (_download is not null)
        {
            try { _download.StateChanged -= OnStateChanged; } catch { }
            try { if (_download.State == ReceiptDownloadState.InProgress) _download.Cancel(); } catch { }
        }
        try { _source.Dispose(); } catch { }
        _file.Dispose();
        _completed.TrySetCanceled();
        if (_completed.Task.IsFaulted) _ = _completed.Task.Exception;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_pendingVerification is not null)
        {
            try { await _pendingVerification.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        for (var attempt = 0; attempt < 10 && File.Exists(_file.TempPath); attempt++)
        {
            await Task.Delay(100);
            _file.Dispose();
        }
        _lifetime.Dispose();
    }

    private sealed class WebViewSource : IReceiptDownloadSource
    {
        private readonly CoreWebView2 _parent;
        private EventHandler<IReceiptDownloadStarting>? _starting;
        public WebViewSource(CoreWebView2 parent) => _parent = parent;
        public event EventHandler<IReceiptDownloadStarting>? DownloadStarting
        {
            add { if (_starting is null) _parent.DownloadStarting += OnStarting; _starting += value; }
            remove { _starting -= value; if (_starting is null) _parent.DownloadStarting -= OnStarting; }
        }
        private void OnStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
        {
            if (ReferenceEquals(sender, _parent)) _starting?.Invoke(this, new WebViewStarting(args));
        }
        public void Dispose() { _parent.DownloadStarting -= OnStarting; _starting = null; }
    }

    private sealed class WebViewStarting(CoreWebView2DownloadStartingEventArgs args) : IReceiptDownloadStarting
    {
        public IReceiptDownloadOperation Operation { get; } = new WebViewOperation(args.DownloadOperation);
        public string ResultFilePath { get => args.ResultFilePath; set => args.ResultFilePath = value; }
        public bool Handled { get => args.Handled; set => args.Handled = value; }
        public bool Cancel { get => args.Cancel; set => args.Cancel = value; }
        public Action GetCompleteDeferral() => args.GetDeferral().Complete;
    }

    private sealed class WebViewOperation(CoreWebView2DownloadOperation operation) : IReceiptDownloadOperation
    {
        private EventHandler? _changed;
        public string Uri => operation.Uri;
        public string MimeType => operation.MimeType;
        public ReceiptDownloadState State => operation.State switch
        {
            CoreWebView2DownloadState.InProgress => ReceiptDownloadState.InProgress,
            CoreWebView2DownloadState.Completed => ReceiptDownloadState.Completed,
            _ => ReceiptDownloadState.Interrupted
        };
        public event EventHandler? StateChanged
        {
            add { if (_changed is null) operation.StateChanged += OnChanged; _changed += value; }
            remove { _changed -= value; if (_changed is null) operation.StateChanged -= OnChanged; }
        }
        private void OnChanged(object? sender, object args) => _changed?.Invoke(this, EventArgs.Empty);
        public void Cancel() => operation.Cancel();
    }
}
