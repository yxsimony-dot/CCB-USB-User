using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace UsbWebLauncher.App.Browser;

public sealed class WpfCertificateLoginPage : ICertificateLoginPage
{
    private static readonly string Script = LoadScript();
    private readonly WebView2 control;
    private readonly Uri target;
    private readonly ClientCertificateAuthenticationOptions? authentication;
    private readonly CancellationToken sessionToken;
    private readonly CertificateLoginNavigation _navigation = new();
    private string? _lastDiagnostic;

    public WpfCertificateLoginPage(WebView2 control, Uri target,
        ClientCertificateAuthenticationOptions? authentication = null, CancellationToken sessionToken = default)
    {
        this.control = control;
        this.target = target;
        this.authentication = authentication;
        this.sessionToken = sessionToken;
        // Subscribe before CoreWebView2 initialization, without making the control
        // keep a replaced page adapter alive for its remaining lifetime.
        WeakEventManager<WebView2, CoreWebView2NavigationStartingEventArgs>.AddHandler(
            control, nameof(control.NavigationStarting), OnNavigationStarting);
        WeakEventManager<WebView2, CoreWebView2NavigationCompletedEventArgs>.AddHandler(
            control, nameof(control.NavigationCompleted), OnNavigationCompleted);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        _navigation.NavigationStarting(args.NavigationId);

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
        _navigation.NavigationCompleted(args.NavigationId);

    public Task<CertificateLoginPageState> InspectAsync(CancellationToken cancellationToken) =>
        OnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var core = control.CoreWebView2;
            var inspection = _navigation.BeginInspection(ParseUri(core?.Source));
            if (core is null || _navigation.IsNavigating) return CertificateLoginPageState.WaitingForPage;
            var page = inspection.Page;
            if (!IsAllowedPage(page, target, authentication))
            {
                CertificateLoginDiagnostics.Write("page-address-rejected", new { Origin = page?.GetLeftPart(UriPartial.Authority), Path = page?.AbsolutePath });
                return CertificateLoginPageState.Unsupported;
            }
            var ticket = Guid.NewGuid().ToString("N");
            var status = await ExecuteAsync(page!, "inspect", ticket, null, cancellationToken);
            var currentPage = ParseUri(core.Source);
            var isCurrent = _navigation.IsCurrent(inspection, currentPage);
            CertificateLoginDiagnostics.Write("page-inspection", new
            {
                Status = status, Origin = page!.GetLeftPart(UriPartial.Authority), Path = page.AbsolutePath,
                CurrentOrigin = currentPage?.GetLeftPart(UriPartial.Authority), CurrentPath = currentPage?.AbsolutePath,
                NavigationInProgress = _navigation.IsNavigating, DocumentChanged = !isCurrent
            });
            // ExecuteScriptAsync may run in the incoming document, with an old request URL.
            // No result from that transition can classify the new page or authorize a PIN.
            if (!isCurrent) return CertificateLoginPageState.WaitingForPage;
            if (status != "ready") await CaptureDiagnosticsAsync(page, ticket, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Diagnostics also awaits WebView2 and can span a navigation.
            return _navigation.CompleteInspection(inspection, ParseUri(core.Source), status, ticket);
        }, cancellationToken);

    public Task<CertificateLoginResult> TryLoginAsync(ClientCertificatePin pin, CancellationToken cancellationToken) =>
        OnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = ParseUri(control.CoreWebView2?.Source);
            var ticket = _navigation.TakeTicket(page);
            if (ticket is null || !IsAllowedPage(page, target, authentication))
                return CertificateLoginResult.Unsupported;
            var status = await ExecuteAsync(page!, "submit", ticket, pin, cancellationToken);
            return status switch
            {
                "submitted" => CertificateLoginResult.Submitted,
                "unsupported" or "waiting" => CertificateLoginResult.Unsupported,
                _ => CertificateLoginResult.Failed
            };
        }, cancellationToken);

    private async Task<string?> ExecuteAsync(Uri page, string operation, string ticket,
        ClientCertificatePin? pin, CancellationToken cancellationToken)
    {
        sessionToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(new
        {
            operation, ticket,
            origin = page.GetLeftPart(UriPartial.Authority),
            path = page.AbsolutePath,
            pin = pin is null ? null : new string(pin.AsMemory().Span)
        });
        cancellationToken.ThrowIfCancellationRequested();
        sessionToken.ThrowIfCancellationRequested();
        var execution = control.CoreWebView2.ExecuteScriptAsync($"({Script})({payload})");
        // Inspection carries no PIN and can be abandoned on timeout; its fresh document
        // ticket cannot authorize a later attempt. Once submitting, await actual completion
        // so another attempt cannot overtake a possibly still-running submission.
        var json = operation == "inspect"
            ? await execution.WaitAsync(cancellationToken)
            : await execution;
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("status", out var status)
            ? status.GetString() : null;
    }

    private async Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (control.Dispatcher.CheckAccess()) return await action();
        return await control.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);
    }

    private async Task CaptureDiagnosticsAsync(Uri page, string ticket, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new { operation = "diagnose", ticket,
            origin = page.GetLeftPart(UriPartial.Authority), path = page.AbsolutePath });
        var json = await control.CoreWebView2.ExecuteScriptAsync($"({Script})({request})").WaitAsync(cancellationToken);
        if (json == _lastDiagnostic) return;
        _lastDiagnostic = json;
        using var parsed = JsonDocument.Parse(json);
        CertificateLoginDiagnostics.Write("page-structure", parsed.RootElement);
    }

    internal static bool IsAllowedPage(Uri? page, Uri target, ClientCertificateAuthenticationOptions? authentication)
    {
        if (page is null || !page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || page.Port != 443 ||
            page.UserInfo.Length != 0 || target.Scheme != Uri.UriSchemeHttps || target.Port != 443 ||
            target.Host != "b2bbank.ccb.com") return false;
        var entryHost = page.Host == target.Host;
        var certificateHost = page.Host is "b2b3.ccb.com" or "xc.b2b3.ccb.com" && authentication is not null &&
            ((authentication.Host == page.Host && authentication.Port == page.Port) ||
             authentication.AdditionalEndpoints?.Any(endpoint => endpoint.Host == page.Host && endpoint.Port == page.Port) == true);
        if (!entryHost && !certificateHost) return false;
        if (page.AbsolutePath == "/NCCB/V6/b2bindex.jsp") return certificateHost;
        if (page.AbsolutePath != "/NCCB/MNECV6B2BMainPlat_UN") return false;
        var query = page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).ToArray();
        bool HasSingle(string name, string value) => query.Count(part => Uri.UnescapeDataString(part[0]) == name) == 1 &&
            query.Any(part => part.Length == 2 && Uri.UnescapeDataString(part[0]) == name && Uri.UnescapeDataString(part[1]) == value);
        return HasSingle("TXCODE", "N7N010") && HasSingle("LOGIN_TYPE", "UC");
    }

    private static Uri? ParseUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string LoadScript()
    {
        using var stream = typeof(WpfCertificateLoginPage).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.CcbCertificateLogin.js") ?? throw new InvalidOperationException("登录页面适配器不可用。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed class CertificateLoginNavigation
{
    internal readonly record struct Inspection(Uri? Page, long Generation);

    private string? _ticket;
    private Inspection? _authorizedInspection;
    private long _generation;
    private ulong? _navigationId;
    public bool IsNavigating => _navigationId.HasValue;

    public void NavigationStarting(ulong navigationId)
    {
        _navigationId = navigationId;
        _generation++;
        _ticket = null;
        _authorizedInspection = null;
    }

    public void NavigationCompleted(ulong navigationId)
    {
        if (_navigationId == navigationId) _navigationId = null;
    }

    public Inspection BeginInspection(Uri? page)
    {
        _ticket = null;
        _authorizedInspection = null;
        return new(page, _generation);
    }

    public bool IsCurrent(Inspection inspection, Uri? currentPage) =>
        !IsNavigating && inspection.Generation == _generation && inspection.Page is not null &&
        currentPage is not null && string.Equals(inspection.Page.AbsoluteUri, currentPage.AbsoluteUri, StringComparison.Ordinal);

    public CertificateLoginPageState CompleteInspection(Inspection inspection, Uri? currentPage,
        string? status, string ticket)
    {
        if (!IsCurrent(inspection, currentPage)) return CertificateLoginPageState.WaitingForPage;
        if (status == "ready")
        {
            _ticket = ticket;
            _authorizedInspection = inspection;
            return CertificateLoginPageState.Ready;
        }
        return status == "unsupported" ? CertificateLoginPageState.Unsupported : CertificateLoginPageState.WaitingForPage;
    }

    public string? TakeTicket(Uri? page)
    {
        var ticket = _ticket;
        _ticket = null; // An ambiguous completion must never replay this credential.
        var inspection = _authorizedInspection;
        _authorizedInspection = null;
        return inspection is { } authorized && IsCurrent(authorized, page) ? ticket : null;
    }
}
