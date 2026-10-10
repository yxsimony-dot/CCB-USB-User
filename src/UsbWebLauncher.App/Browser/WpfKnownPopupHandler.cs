using System.IO;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

// Event-driven handling for verified notices. No polling or business readiness gate.
internal sealed class WpfKnownPopupHandler : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly PopupFrameRegistry<CoreWebView2Frame> _frames = new();
    private Task? _initialization;
    private string? _scriptId;
    private bool _disposed;
    private PopupScriptRefresh? _refresh;

    public WpfKnownPopupHandler(CoreWebView2 core) => _core = core;

    public Task EnsureInitializedAsync() => _initialization ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        if (_disposed) return;
        var installer = LoadScript("CcbKnownPopupDismiss.js");
        var bootstrap = LoadScript("CcbKnownPopupBootstrap.js");
        var script = "(() => { const install = (" + installer + "); install(); return (" + bootstrap + ")(install); })()";
        _refresh = new PopupScriptRefresh(
            () => !_disposed && KnownPopupPolicy.IsBankDocument(_core.Source, "/NCCB/V6/b2bindex.jsp")
                ? _core.ExecuteScriptAsync(script) : Task.FromResult("null"),
            name => CertificateLoginDiagnostics.Write(name));
        _core.NewWindowRequested += OnNewWindowRequested;
        _core.FrameCreated += OnFrameCreated;
        _core.WebMessageReceived += OnWebMessageReceived;
        _core.DOMContentLoaded += OnDOMContentLoaded;
        _core.NavigationCompleted += OnNavigationCompleted;
        // Register before navigation so early notices are covered. This does not wait for a popup.
        var id = await _core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        if (_disposed)
        {
            try { _core.RemoveScriptToExecuteOnDocumentCreated(id); } catch { }
            return;
        }
        _scriptId = id;
    }

    private static string LoadScript(string name)
    {
        using var stream = typeof(WpfKnownPopupHandler).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser." + name) ?? throw new InvalidOperationException();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnDOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (!_disposed && _refresh is not null) _ = _refresh.RunAsync();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!_disposed && args.IsSuccess && _refresh is not null) _ = _refresh.RunAsync();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        try
        {
            if (!_disposed && KnownPopupPolicy.ShouldBlock(_core.Source, args.Uri, args.IsUserInitiated))
            {
                args.Handled = true;
                CertificateLoginDiagnostics.Write("popup-announcement-blocked");
            }
        }
        catch { /* Unknown or failed requests keep their normal behavior. */ }
    }

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args)
    {
        try
        {
            if (_disposed) return;
            var frame = args.Frame;
            if (!_frames.Register(frame, destroyed => frame.Destroyed += (_, _) => destroyed())) return;
            frame.WebMessageReceived += (source, message) =>
                _frames.Dispatch(frame, () => OnWebMessageReceived(source, message));
            frame.FrameCreated += (source, created) =>
                _frames.Dispatch(frame, () => OnFrameCreated(source, created));
        }
        catch { }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (_disposed || !KnownPopupPolicy.IsPopupMessageSource(args.Source, _core.Source)) return;
            var eventName = KnownPopupPolicy.DiagnosticEvent(args.TryGetWebMessageAsString());
            if (eventName is not null) CertificateLoginDiagnostics.Write(eventName);
        }
        catch { /* Do not log unrecognized message payloads or exception text. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The enclosing WebView owns and destroys all native frame subscriptions.
        // Unsubscribing FrameCreated on stale frame wrappers can abort the native
        // runtime. Invalidate callbacks and release managed tracking instead.
        _frames.Dispose();
        Release("refresh", () => _refresh?.Dispose());
        Release("core-new-window", () => _core.NewWindowRequested -= OnNewWindowRequested);
        Release("core-frame-created", () => _core.FrameCreated -= OnFrameCreated);
        Release("core-web-message", () => _core.WebMessageReceived -= OnWebMessageReceived);
        Release("core-dom-loaded", () => _core.DOMContentLoaded -= OnDOMContentLoaded);
        Release("core-navigation-complete", () => _core.NavigationCompleted -= OnNavigationCompleted);
        if (_scriptId is not null)
        {
            Release("core-remove-script", () => _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId));
            _scriptId = null;
        }
    }

    private static void Release(string stage, Action release)
    {
        CertificateLoginDiagnostics.Write("popup-release-start", new { stage });
        try
        {
            release();
            CertificateLoginDiagnostics.Write("popup-release-complete", new { stage });
        }
        catch { CertificateLoginDiagnostics.Write("popup-release-error", new { stage }); }
    }
}
