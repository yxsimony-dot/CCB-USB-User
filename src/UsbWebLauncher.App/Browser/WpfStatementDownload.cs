using System.Drawing;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

// Owns only the download phase of one explicit export. Existing bank popups remain independent.
internal sealed class WpfStatementDownload : IStatementExportDownload
{
    private readonly CoreWebView2 _parent;
    private readonly string _origin;
    private readonly IntPtr _parentWindow;
    private readonly StatementPdfFile _file;
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _child;
    private CoreWebView2DownloadOperation? _download;
    private bool _claimedWindow;
    private bool _disposed;
    private Task? _pendingWindow;
    private CoreWebView2Deferral? _pendingDeferral;
    private CoreWebView2NewWindowRequestedEventArgs? _pendingArgs;

    public WpfStatementDownload(CoreWebView2 parent, string origin, IntPtr parentWindow, StatementExportIdentity identity, string directory)
    {
        _parent = parent;
        _origin = origin;
        _parentWindow = parentWindow;
        _file = StatementPdfFile.Create(directory, identity);
        try
        {
            _parent.NewWindowRequested += OnNewWindowRequested;
        }
        catch { Dispose(); throw; }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        try
        {
            if (!_disposed)
                CertificateLoginDiagnostics.Write("statement-download-window", new
                {
                    expectedTarget = StatementDownloadPolicy.IsResultPage(_origin, args.Uri),
                    expectedSource = StatementDownloadPolicy.IsSourcePage(_origin, args.OriginalSourceFrameInfo?.Source),
                    alreadyClaimed = _claimedWindow,
                    alreadyHandled = args.Handled
                });
            if (_disposed || _claimedWindow || args.Handled ||
                !StatementDownloadPolicy.IsExpectedWindow(_origin, args.Uri, args.OriginalSourceFrameInfo?.Source)) return;
            _claimedWindow = true;
            _pendingDeferral = args.GetDeferral();
            _pendingArgs = args;
            _pendingWindow = ReceiveWindowAsync(args, _pendingDeferral);
        }
        catch { Fail("无法接收银行 PDF 下载窗口，请重试。"); }
    }

    private async Task ReceiveWindowAsync(CoreWebView2NewWindowRequestedEventArgs args, CoreWebView2Deferral deferral)
    {
        try
        {
            var options = _parent.Environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _parent.Profile.ProfileName;
            options.IsInPrivateModeEnabled = _parent.Profile.IsInPrivateModeEnabled;
            var controller = await _parent.Environment.CreateCoreWebView2ControllerAsync(_parentWindow, options);
            if (_disposed)
            {
                controller.Close();
                args.Handled = true;
                return;
            }
            _controller = controller;
            controller.IsVisible = false;
            controller.Bounds = new Rectangle(0, 0, 1, 1);
            _child = controller.CoreWebView2;
            _child.DownloadStarting += OnDownloadStarting;
            _child.NavigationStarting += OnChildNavigationStarting;
            _child.NavigationCompleted += OnChildNavigationCompleted;
            _child.NewWindowRequested += OnChildNewWindowRequested;
            // Preserve the bank's original window/POST request; never replace it with a GET.
            args.NewWindow = _child;
            args.Handled = true;
            CertificateLoginDiagnostics.Write("statement-download-window-ready");
        }
        catch
        {
            try { args.Handled = true; } catch { }
            Fail("无法接收银行 PDF 下载窗口，请重试。");
        }
        finally
        {
            try { deferral.Complete(); } catch { }
            if (ReferenceEquals(_pendingDeferral, deferral)) { _pendingDeferral = null; _pendingArgs = null; }
        }
    }

    private void OnChildNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        try
        {
            if (_disposed || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.GetLeftPart(UriPartial.Authority) != _origin)
            {
                args.Cancel = true;
                Fail("下载窗口跳转到未识别的地址，已停止导出。");
            }
        }
        catch { args.Cancel = true; Fail("银行下载窗口不可用。"); }
    }

    private void OnChildNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_disposed) return;
        CertificateLoginDiagnostics.Write("statement-download-navigation", new
        {
            success = args.IsSuccess,
            error = args.WebErrorStatus.ToString(),
            httpStatus = args.HttpStatusCode,
            downloadStarted = _download is not null
        });
        // An attachment reports ConnectionAborted before DownloadStarting in WebView2.
        // Keep the child alive for its download event; only download completion proves success.
        if (StatementDownloadPolicy.ShouldFailNavigation(args.IsSuccess, args.WebErrorStatus, _download is not null))
            Fail("银行下载请求失败，请重试。");
    }

    private void OnChildNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        Fail("银行下载产生了额外窗口，已停止导出，请查看页面。");
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            if (_disposed || !ReferenceEquals(sender, _child)) return;
            var operation = args.DownloadOperation;
            var accepted = StatementDownloadPolicy.IsPdf(_origin, operation.Uri, operation.MimeType, args.ResultFilePath);
            CertificateLoginDiagnostics.Write("statement-download-start", new { accepted });
            if (!accepted)
            {
                if (ReferenceEquals(sender, _child))
                {
                    args.Cancel = true;
                    args.Handled = true;
                    Fail("银行返回的下载不是 PDF，已停止导出。");
                }
                return;
            }
            if (_download is not null)
            {
                if (ReferenceEquals(sender, _child)) { args.Cancel = true; args.Handled = true; }
                return;
            }
            _download = operation;
            args.ResultFilePath = _file.TempPath;
            args.Handled = true;
            operation.StateChanged += OnStateChanged;
            OnStateChanged(operation, EventArgs.Empty);
        }
        catch
        {
            try { args.Cancel = true; args.Handled = true; } catch { }
            Fail("无法保存银行 PDF，请检查下载目录权限。");
        }
    }

    private void OnStateChanged(object? sender, object args)
    {
        try
        {
            if (_disposed || _download is null) return;
            CertificateLoginDiagnostics.Write("statement-download-state", new
            {
                state = _download.State switch
                {
                    CoreWebView2DownloadState.InProgress => "in-progress",
                    CoreWebView2DownloadState.Completed => "completed",
                    CoreWebView2DownloadState.Interrupted => "interrupted",
                    _ => "unknown"
                }
            });
            if (_download.State == CoreWebView2DownloadState.Completed) _completed.TrySetResult(true);
            else if (_download.State == CoreWebView2DownloadState.Interrupted) Fail("PDF 下载中断，请重试。");
        }
        catch { Fail("无法确认 PDF 下载完成。"); }
    }

    private void Fail(string message)
    {
        if (!_disposed) _completed.TrySetException(new StatementExportException(message));
    }

    public async Task<string> WaitAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await _completed.Task.WaitAsync(timeout.Token);
            return await _file.CompleteAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StatementExportException("等待 PDF 下载超时，请查看银行页面后重试。");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _parent.NewWindowRequested -= OnNewWindowRequested; } catch { }
        try { if (_pendingArgs is not null) _pendingArgs.Handled = true; } catch { }
        try { _pendingDeferral?.Complete(); } catch { }
        _pendingDeferral = null;
        _pendingArgs = null;
        if (_download is not null)
        {
            try { _download.StateChanged -= OnStateChanged; } catch { }
            try { if (_download.State == CoreWebView2DownloadState.InProgress) _download.Cancel(); } catch { }
        }
        if (_child is not null)
        {
            try { _child.DownloadStarting -= OnDownloadStarting; } catch { }
            try { _child.NavigationStarting -= OnChildNavigationStarting; } catch { }
            try { _child.NavigationCompleted -= OnChildNavigationCompleted; } catch { }
            try { _child.NewWindowRequested -= OnChildNewWindowRequested; } catch { }
        }
        try { _controller?.Close(); } catch { }
        _file.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_pendingWindow is not null)
        {
            // Deferral is already completed on cancellation; late-created controllers close themselves.
            try { await _pendingWindow.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        for (var attempt = 0; attempt < 10 && File.Exists(_file.TempPath); attempt++)
        {
            await Task.Delay(100);
            _file.Dispose();
        }
        // Observe any error raised before the final bank action itself was rejected.
        if (_completed.Task.IsFaulted) _ = _completed.Task.Exception;
    }
}
