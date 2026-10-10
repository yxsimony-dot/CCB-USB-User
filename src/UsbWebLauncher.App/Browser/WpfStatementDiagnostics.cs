using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

// Temporary observation only. Never submits a form, changes a request, or handles a download.
internal sealed class WpfStatementDiagnostics : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly StatementDiagnosticLog _log;
    private readonly PopupDiagnosticSampler _sampler;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Dictionary<CoreWebView2DownloadOperation, int> _downloads = [];
    private readonly string _script;
    private int _nextDownload;
    private bool _disposed;

    public WpfStatementDiagnostics(CoreWebView2 core, Dispatcher dispatcher, bool receiptDiscovery = false)
    {
        _core = core;
        using var stream = typeof(WpfStatementDiagnostics).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.CcbStatementDiagnostics.js") ?? throw new InvalidOperationException();
        using var reader = new StreamReader(stream);
        _script = "(" + reader.ReadToEnd() + ")()";
        _log = new StatementDiagnosticLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbWebLauncher", "logs", receiptDiscovery ? "receipt-discovery.jsonl" : "statement-discovery.jsonl"));
        _sampler = new PopupDiagnosticSampler(ReadAsync, json =>
        {
            using var parsed = JsonDocument.Parse(json);
            // A full queue must not make the sampler cache this snapshot as delivered.
            if (!_log.TryWrite("page-structure", parsed.RootElement.Clone()))
                throw new InvalidOperationException("Diagnostic queue unavailable.");
            return Task.CompletedTask;
        });
        _timer = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        try
        {
            _timer.Tick += OnTick;
            _core.WebResourceResponseReceived += OnResponseReceived;
            _core.DownloadStarting += OnDownloadStarting;
            _core.NewWindowRequested += OnNewWindowRequested;
            _log.TryWrite("capture-start", new
            {
                version = 2, purpose = receiptDiscovery ? "receipt" : "statement", maximumMinutes = 20, maximumBytes = 2 * 1024 * 1024,
                postBodyFields = "not-collected", responseBody = "not-collected"
            });
            _timer.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private bool Active()
    {
        if (_disposed) return false;
        if (_elapsed.Elapsed >= TimeSpan.FromMinutes(20) || !_log.IsAccepting)
        {
            Stop(_log.IsAccepting ? "time-limit" : "log-unavailable-or-full");
            return false;
        }
        return true;
    }

    private Task<string> ReadAsync() => Active() && StatementDiagnosticPolicy.IsBankPage(_core.Source)
        ? _core.ExecuteScriptAsync(_script) : Task.FromResult("null");

    private void OnTick(object? sender, EventArgs args)
    {
        try { if (Active()) _ = _sampler.CaptureAsync(); }
        catch { }
    }

    private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            if (!Active() || !StatementDiagnosticPolicy.IsBankPage(_core.Source)) return;
            var request = args.Request;
            var response = args.Response;
            var contentType = response.Headers.Contains("Content-Type")
                ? response.Headers.GetHeader("Content-Type") : null;
            if (!StatementDiagnosticPolicy.ShouldObserveResource(request.Uri, contentType)) return;
            _log.TryWrite("resource-response", new
            {
                address = StatementDiagnosticPolicy.DescribeAddress(request.Uri),
                method = request.Method is "GET" or "POST" or "HEAD" ? request.Method : "other",
                status = response.StatusCode,
                mediaType = StatementDiagnosticPolicy.MediaType(contentType),
                disposition = StatementDiagnosticPolicy.Disposition(response.Headers.Contains("Content-Disposition")
                    ? response.Headers.GetHeader("Content-Disposition") : null),
                postBodyFields = request.Method == "POST" ? "not-collected" : "not-applicable"
            });
        }
        catch { /* Metadata only: do not log exceptions, bodies, or complete headers. */ }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        try
        {
            if (!Active() || !StatementDiagnosticPolicy.IsBankPage(_core.Source)) return;
            var address = StatementDiagnosticPolicy.DescribeAddress(args.Uri);
            if (address is null) return;
            _log.TryWrite("new-window", new
            {
                address,
                source = StatementDiagnosticPolicy.DescribeAddress(args.OriginalSourceFrameInfo?.Source),
                userInitiated = args.IsUserInitiated
            });
        }
        catch { }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            if (!Active() || !StatementDiagnosticPolicy.IsBankPage(_core.Source)) return;
            var operation = args.DownloadOperation;
            var address = StatementDiagnosticPolicy.DescribeAddress(operation.Uri);
            var blob = operation.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) &&
                StatementDiagnosticPolicy.DescribeAddress(operation.Uri[5..]) is not null;
            if (address is null && !blob) return;
            var id = ++_nextDownload;
            _log.TryWrite("download-start", new
            {
                id, address, uriKind = blob ? "blob" : "https",
                mediaType = StatementDiagnosticPolicy.MediaType(operation.MimeType)
            });
            if (_downloads.Count >= 8) return;
            _downloads.Add(operation, id);
            operation.StateChanged += OnDownloadStateChanged;
            RecordDownloadState(operation, id);
        }
        catch { }
    }

    private void OnDownloadStateChanged(object? sender, object args)
    {
        try
        {
            if (!Active() || sender is not CoreWebView2DownloadOperation operation ||
                !_downloads.TryGetValue(operation, out var id)) return;
            RecordDownloadState(operation, id);
        }
        catch { }
    }

    private void RecordDownloadState(CoreWebView2DownloadOperation operation, int id)
    {
        var state = operation.State;
        _log.TryWrite("download-state", new { id, state = state.ToString() });
        if (state is CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted)
        {
            operation.StateChanged -= OnDownloadStateChanged;
            _downloads.Remove(operation);
        }
    }

    private void Stop(string reason)
    {
        if (_disposed) return;
        _log.TryWrite("capture-stopped", new { reason, droppedRecords = _log.DroppedRecords });
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler.Dispose();
        try { _timer.Stop(); } catch { }
        try { _timer.Tick -= OnTick; } catch { }
        try { _core.WebResourceResponseReceived -= OnResponseReceived; } catch { }
        try { _core.DownloadStarting -= OnDownloadStarting; } catch { }
        try { _core.NewWindowRequested -= OnNewWindowRequested; } catch { }
        foreach (var operation in _downloads.Keys)
        {
            try { operation.StateChanged -= OnDownloadStateChanged; } catch { }
        }
        _downloads.Clear();
        _log.Dispose();
    }
}
