using System.IO;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

// Temporary structural probe: no clicks, intercepted windows, or business dependencies.
internal sealed class WpfPopupDiagnostics : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly DispatcherTimer _timer;
    private readonly PopupDiagnosticSampler _sampler;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly string _logPath;
    private readonly string _script;
    private int _remaining;
    private bool _disposed;
    private bool _firstWrite = true;

    public WpfPopupDiagnostics(CoreWebView2 core, Dispatcher dispatcher)
    {
        _core = core;
        using var stream = typeof(WpfPopupDiagnostics).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.CcbPopupDiagnostics.js") ?? throw new InvalidOperationException();
        using var reader = new StreamReader(stream);
        _script = "(" + reader.ReadToEnd() + ")()";
        _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbWebLauncher", "logs", "popup-structure.jsonl");
        _sampler = new(ReadAsync, async json =>
        {
            using var parsed = JsonDocument.Parse(json);
            await WriteAsync("page-structure", parsed.RootElement.Clone());
        });
        _timer = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += OnTick;
        _core.NavigationCompleted += OnNavigationCompleted;
        _core.NewWindowRequested += OnNewWindowRequested;
    }

    private Task<string> ReadAsync() => !_disposed && IsBankPage(_core.Source)
        ? _core.ExecuteScriptAsync(_script)
        : Task.FromResult("null");

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            _timer.Stop();
            if (!args.IsSuccess || !IsBankPage(_core.Source)) return;
            // A bounded background probe catches delayed/iframe popups. Nothing awaits it.
            _remaining = 60;
            _timer.Start();
        }
        catch { }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || --_remaining <= 0) _timer.Stop();
        if (!_disposed) _ = _sampler.CaptureAsync();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Observe the URL only. Leave Handled/NewWindow and the site's behavior untouched.
        try
        {
            if (_disposed || !IsBankPage(_core.Source) || !TryBankAddress(args.Uri, out var uri)) return;
            _ = WriteAsync("new-window", new
            {
                origin = uri!.GetLeftPart(UriPartial.Authority),
                path = Regex.Replace(uri.AbsolutePath, @"\d{6,}|[a-fA-F0-9]{24,}", "[redacted]"),
                userInitiated = args.IsUserInitiated
            });
        }
        catch { }
    }

    private static bool IsBankPage(string raw) => TryBankAddress(raw, out var uri) &&
        uri!.AbsolutePath.StartsWith("/NCCB/", StringComparison.Ordinal);

    private static bool TryBankAddress(string raw, out Uri? uri) =>
        Uri.TryCreate(raw, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        (uri.Host == "ccb.com" || uri.Host.EndsWith(".ccb.com", StringComparison.OrdinalIgnoreCase));

    private async Task WriteAsync(string eventName, object data)
    {
        try
        {
            await _fileGate.WaitAsync();
            try
            {
                if (_disposed) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                if (_firstWrite)
                {
                    await File.WriteAllTextAsync(_logPath, string.Empty);
                    _firstWrite = false;
                }
                var line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, eventName, data }) + Environment.NewLine;
                if (new FileInfo(_logPath).Length + Encoding.UTF8.GetByteCount(line) > 512 * 1024) return;
                await File.AppendAllTextAsync(_logPath, line);
            }
            finally { _fileGate.Release(); }
        }
        catch { /* No popup content or exception text is logged on failure. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler.Dispose();
        try { _timer.Stop(); } catch { }
        try { _timer.Tick -= OnTick; } catch { }
        try { _core.NavigationCompleted -= OnNavigationCompleted; } catch { }
        try { _core.NewWindowRequested -= OnNewWindowRequested; } catch { }
    }
}
