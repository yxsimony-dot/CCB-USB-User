using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.App.Browser;

internal sealed class WpfCurrentCompanyDisplay : IDisposable
{
    private readonly WebView2 browser;
    private readonly Action<string> publish;
    private readonly CurrentCompanyState state = new();
    private readonly DispatcherTimer timer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string script;
    private bool inspecting;
    private bool disposed;
    private ulong navigationId;

    public WpfCurrentCompanyDisplay(WebView2 browser, Action<string> publish)
    {
        this.browser = browser;
        this.publish = publish;
        using var stream = typeof(WpfCurrentCompanyDisplay).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.CcbCurrentCompany.js") ?? throw new InvalidOperationException();
        using var reader = new StreamReader(stream);
        script = reader.ReadToEnd();
        browser.NavigationStarting += OnNavigationStarting;
        browser.NavigationCompleted += OnNavigationCompleted;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            OnTick, browser.Dispatcher);
        publish(state.Text);
    }

    public void SetDevicePresent(bool present)
    {
        if (disposed) return;
        state.SetDevicePresent(present);
        publish(state.Text);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        navigationId = args.NavigationId;
        state.NavigationStarting();
        publish(state.Text);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (disposed || args.NavigationId != navigationId) return;
        // Attachment downloads abort navigation while leaving the business document alive.
        // Re-inspect that document; never restore a previously cached company name.
        var documentMayRemain = args.WebErrorStatus is
            CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.OperationCanceled;
        var allowed = AllowedPage(browser.CoreWebView2?.Source);
        state.NavigationCompleted(allowed && args.IsSuccess, allowed && documentMayRemain);
        _ = InspectAsync();
    }

    private async void OnTick(object? sender, EventArgs args) => await InspectAsync();

    private async Task InspectAsync()
    {
        if (disposed || inspecting || state.BeginRead() is not long ticket || browser.CoreWebView2 is not { } core) return;
        inspecting = true;
        try
        {
            var source = core.Source;
            if (!AllowedPage(source))
            {
                state.Apply(ticket, "unsupported", null);
                publish(state.Text);
                return;
            }
            var request = JsonSerializer.Serialize(new { origin = new Uri(source).GetLeftPart(UriPartial.Authority) });
            var json = await core.ExecuteScriptAsync("(" + script + ")(" + request + ")")
                .WaitAsync(TimeSpan.FromSeconds(3), lifetime.Token);
            if (disposed || source != core.Source) return;
            using var parsed = JsonDocument.Parse(json);
            string? Read(string name) => parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            state.Apply(ticket, Read("status"), Read("companyName"));
            publish(state.Text);
        }
        catch
        {
            if (!disposed)
            {
                state.Apply(ticket, "waiting", null);
                publish(state.Text);
            }
        }
        finally { inspecting = false; }
    }

    private static bool AllowedPage(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Host is "b2b3.ccb.com" or "xc.b2b3.ccb.com" && uri.AbsolutePath.StartsWith("/NCCB/", StringComparison.Ordinal);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= OnTick;
        browser.NavigationStarting -= OnNavigationStarting;
        browser.NavigationCompleted -= OnNavigationCompleted;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
