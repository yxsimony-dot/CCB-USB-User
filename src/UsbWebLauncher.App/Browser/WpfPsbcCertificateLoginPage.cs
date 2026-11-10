using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.App.Browser;

public sealed class WpfPsbcCertificateLoginPage : ICertificateLoginPage
{
    private static readonly string Script = LoadScript();
    private readonly WebView2 _control;
    private readonly CancellationToken _sessionToken;
    private readonly CertificateLoginNavigation _navigation = new();
    private readonly WindowsWebViewSecureInput _keyboard = new();
    private CertificateLoginNavigation.Inspection _inspection;
    private string? _lastStatus;

    public WpfPsbcCertificateLoginPage(WebView2 control, CancellationToken sessionToken = default)
    {
        _control = control;
        _sessionToken = sessionToken;
        WeakEventManager<WebView2, CoreWebView2NavigationStartingEventArgs>.AddHandler(
            control, nameof(control.NavigationStarting), OnNavigationStarting);
        WeakEventManager<WebView2, CoreWebView2NavigationCompletedEventArgs>.AddHandler(
            control, nameof(control.NavigationCompleted), OnNavigationCompleted);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        _navigation.NavigationStarting(args.NavigationId);
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
        _navigation.NavigationCompleted(args.NavigationId);
    private Uri? CurrentPage => Uri.TryCreate(_control.CoreWebView2?.Source,
        UriKind.Absolute, out var page) ? page : null;

    internal static bool IsAllowedPage(Uri? page)
    {
        if (!IsProfessionalOrigin(page)) return false;
        return page!.Fragment is "#/login" or "#/login?tab=ukeyLogin";
    }

    private static bool IsProfessionalOrigin(Uri? page) => page is { IsAbsoluteUri: true } &&
        page.Scheme == "https" && page.Host == "corpebank.psbc.com" && page.Port == 443 &&
        page.UserInfo.Length == 0 && page.AbsolutePath == "/" && page.Query.Length == 0;

    public Task<CertificateLoginPageState> InspectAsync(CancellationToken cancellationToken) =>
        OnUiAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionToken);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            _inspection = _navigation.BeginInspection(CurrentPage);
            if (_control.CoreWebView2 is null || _navigation.IsNavigating ||
                CurrentPage?.Scheme == "about") return CertificateLoginPageState.WaitingForPage;
            if (IsProfessionalOrigin(CurrentPage) && CurrentPage!.Fragment is "" or "#" or "#/")
                return CertificateLoginPageState.WaitingForPage;
            if (!IsAllowedPage(CurrentPage)) return CertificateLoginPageState.Unsupported;
            if (PsbcInputTrace.ManualObservation) return CertificateLoginPageState.ManualObservation;
            if (Window.GetWindow(_control)?.IsActive != true) return CertificateLoginPageState.WaitingForPage;
            var inspection = _inspection;
            var ticket = Guid.NewGuid().ToString("N");
            var status = await ExecuteAsync("inspect", ticket, 0, token);
            token.ThrowIfCancellationRequested();
            if (_lastStatus != status)
            {
                // Status only: no DOM values, credential, account or script payload.
                CertificateLoginDiagnostics.Write("psbc-page-inspection", new { Status = status });
                _lastStatus = status;
            }
            return _navigation.CompleteInspection(inspection, CurrentPage, status, ticket);
        }, cancellationToken);

    public Task<CertificateLoginResult> TryLoginAsync(ClientCertificatePin pin, CancellationToken cancellationToken) =>
        OnUiAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionToken);
            linked.Token.ThrowIfCancellationRequested();
            if (PsbcInputTrace.ManualObservation) return CertificateLoginResult.Unsupported;
            var ticket = _navigation.TakeTicket(CurrentPage);
            if (ticket is null || !IsAllowedPage(CurrentPage)) return CertificateLoginResult.Unsupported;
            return await PsbcPageLoginSubmission.RunAsync(new Submission(this, ticket, _inspection), pin, linked.Token);
        }, cancellationToken);

    private async Task<string?> ExecuteAsync(string operation, string ticket, int length, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (operation != "release") _sessionToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(new { operation, ticket, length });
        var execution = _control.CoreWebView2.ExecuteScriptAsync($"({Script})({payload})");
        var json = operation == "submit" ? await execution : await execution.WaitAsync(token);
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.ValueKind == JsonValueKind.Object &&
            parsed.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
    }

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_control.Dispatcher.CheckAccess()) return await action();
        return await _control.Dispatcher.InvokeAsync(action,
            System.Windows.Threading.DispatcherPriority.Normal, token).Task.Unwrap().ConfigureAwait(false);
    }

    private sealed class Submission(WpfPsbcCertificateLoginPage page, string ticket,
        CertificateLoginNavigation.Inspection inspection) : IPsbcPageLoginInput
    {
        private IntPtr _owner, _target;
        private uint _browserProcess;
        private bool Current => !page._sessionToken.IsCancellationRequested &&
            page._navigation.IsCurrent(inspection, page.CurrentPage) && IsAllowedPage(page.CurrentPage);
        private bool Focused => Current && page._keyboard.IsCurrent(_owner, _browserProcess, _target);

        public async Task<bool> PrepareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var window = Window.GetWindow(page._control);
            if (!Current || window?.IsActive != true) return Record("prepare-window", false);
            _owner = new WindowInteropHelper(window).Handle;
            _browserProcess = page._control.CoreWebView2.BrowserProcessId;
            page._control.Focus();
            if (await page.ExecuteAsync("prepare", ticket, 0, token) != "focused" || !Current)
                return Record("prepare-control", false);
            _target = page._keyboard.Prepare(_owner, _browserProcess);
            return Record("prepare-keyboard", Focused);
        }
        public Task<string?> CheckAsync(int length, CancellationToken token) =>
            Focused ? page.ExecuteAsync("check", ticket, length, token) : Task.FromResult<string?>("failed");
        public bool Send(ClientCertificatePin pin, CancellationToken token) =>
            Record("send", Focused && page._keyboard.Send(_owner, _browserProcess, _target, pin, token));
        public Task<string?> SubmitAsync(int length, CancellationToken token) =>
            Focused ? page.ExecuteAsync("submit", ticket, length, token) : Task.FromResult<string?>("failed");
        public async Task ReleaseAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await page.ExecuteAsync("release", ticket, 0, timeout.Token);
        }

        private static bool Record(string stage, bool success)
        {
            CertificateLoginDiagnostics.Write("psbc-page-input", new { Stage = stage, Success = success });
            return success;
        }
    }

    private static string LoadScript()
    {
        using var stream = typeof(WpfPsbcCertificateLoginPage).Assembly.GetManifestResourceStream(
            "UsbWebLauncher.App.Browser.PsbcCertificateLogin.js")
            ?? throw new InvalidOperationException("PSBC page adapter unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
