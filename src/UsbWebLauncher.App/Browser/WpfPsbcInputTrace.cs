using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

internal sealed class WpfPsbcInputTrace : IDisposable
{
    private static readonly string Script = LoadScript();
    private readonly CoreWebView2 _core;
    private readonly CancellationToken _sessionToken;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly string _traceId = Guid.NewGuid().ToString("N");
    private readonly PsbcInputTraceNavigation _navigationState = new();
    private bool _busy;
    private bool _disposed;
    private long _navigation;
    private int _samples;

    internal WpfPsbcInputTrace(CoreWebView2 core, CancellationToken sessionToken)
    {
        _core = core;
        _sessionToken = sessionToken;
        _core.NavigationStarting += OnNavigationStarting;
        _core.NavigationCompleted += OnNavigationCompleted;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();
        PsbcInputTrace.Write("psbc-real-input-trace-start",
            new { traceId = _traceId, processId = Environment.ProcessId, mode = "manual" });
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _navigation++;
        _navigationState.Starting(args.NavigationId);
    }

    private Uri? CurrentPage => Uri.TryCreate(_core.Source, UriKind.Absolute, out var page) ? page : null;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
        _navigationState.Completed(args.NavigationId, args.IsSuccess, args.HttpStatusCode, CurrentPage);

    private async void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || _busy) return;
        if (_sessionToken.IsCancellationRequested || _lifetime.Elapsed >= TimeSpan.FromMinutes(15) ||
            _samples >= 2048)
        {
            Dispose();
            return;
        }
        // ExecuteScriptAsync grants user activation even for read-only JavaScript.
        // Never run it on a 412 challenge: it changes the next navigation's gesture flag.
        if (!_navigationState.CanObserve(CurrentPage)) return;
        _busy = true;
        try
        {
            var navigation = _navigation;
            var source = _core.Source;
            var json = await _core.ExecuteScriptAsync($"({Script})('drain')");
            if (_disposed || _sessionToken.IsCancellationRequested ||
                navigation != _navigation || source != _core.Source ||
                !_navigationState.CanObserve(CurrentPage)) return;
            foreach (var sample in PsbcInputTrace.Parse(json))
            {
                if (_samples++ >= 2048) break;
                PsbcInputTrace.Write("psbc-real-input",
                    new { traceId = _traceId, navigation, sample });
            }
        }
        catch
        {
            // Navigation and controller teardown may interrupt a read-only sample.
        }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _core.NavigationStarting -= OnNavigationStarting;
        _core.NavigationCompleted -= OnNavigationCompleted;
        _ = StopScriptAsync();
        PsbcInputTrace.Write("psbc-real-input-trace-stop", new { traceId = _traceId });
    }

    private async Task StopScriptAsync()
    {
        try
        {
            if (_navigationState.CanObserve(CurrentPage))
                await _core.ExecuteScriptAsync($"({Script})('stop')");
        }
        catch { }
    }

    private static string LoadScript()
    {
        using var stream = typeof(WpfPsbcInputTrace).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.PsbcInputTrace.js")
            ?? throw new InvalidOperationException("PSBC input trace unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
