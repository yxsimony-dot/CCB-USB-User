using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace UsbWebLauncher.App.Browser;

internal sealed class WpfPsbcNavigationDiagnostics : IDisposable
{
    private readonly CoreWebView2DevToolsProtocolEventReceiver _requests;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _failures;
    private readonly PsbcNavigationDiagnostics _diagnostics;

    private WpfPsbcNavigationDiagnostics(CoreWebView2 core, string mainFrameId)
    {
        _diagnostics = new(mainFrameId);
        _requests = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _failures = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        _requests.DevToolsProtocolEventReceived += OnRequest;
        _failures.DevToolsProtocolEventReceived += OnFailure;
    }

    public static async Task<WpfPsbcNavigationDiagnostics?> TryCreateAsync(CoreWebView2 core)
    {
        WpfPsbcNavigationDiagnostics? observer = null;
        try
        {
            var treeJson = await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}")
                .WaitAsync(TimeSpan.FromSeconds(5));
            using var tree = JsonDocument.Parse(treeJson);
            var frameId = tree.RootElement.GetProperty("frameTree")
                .GetProperty("frame").GetProperty("id").GetString();
            if (string.IsNullOrEmpty(frameId)) return null;
            observer = new(core, frameId);
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}")
                .WaitAsync(TimeSpan.FromSeconds(5));
            return observer;
        }
        catch
        {
            observer?.Dispose();
            CertificateLoginDiagnostics.Write("psbc-network-diagnostics-unavailable");
            return null;
        }
    }

    public void BeginNavigation(long navigationId) => _diagnostics.BeginNavigation(navigationId);

    public string? GetError(long navigationId) => _diagnostics.GetError(navigationId);

    private void OnRequest(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args) =>
        _diagnostics.ObserveRequest(args.ParameterObjectAsJson);

    private void OnFailure(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (_diagnostics.ObserveFailure(args.ParameterObjectAsJson) is { } error)
            CertificateLoginDiagnostics.Write("psbc-document-network-failed", new { error });
    }

    public void Dispose()
    {
        _requests.DevToolsProtocolEventReceived -= OnRequest;
        _failures.DevToolsProtocolEventReceived -= OnFailure;
    }
}
