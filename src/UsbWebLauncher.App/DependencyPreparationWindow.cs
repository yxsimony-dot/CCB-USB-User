using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.App;

internal sealed class DependencyPreparationWindow : Window
{
    private readonly TextBlock _web = Status(), _bank = Status(), _brokerText = Status(), _summary = Status();
    private readonly Button _recheck = Button("重新检查"), _install = Button("安装 / 修复认证组件"), _next = Button("打开应用并设置 PIN");
    private readonly WindowsNativePromptBrokerPrerequisites _broker = new(true);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ContentControl _probe = new() { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
    private bool _busy;

    public DependencyPreparationWindow(bool canOpenApplication = true)
    {
        Title = "环境准备 · USB Web Launcher " + ProductVersion.Display;
        Width = 660; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "首次使用前，完成这三项环境准备", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "安装官方组件后返回此处重新检查。银行驱动可能需要重启电脑。", Margin = new Thickness(0, 10, 0, 18), TextWrapping = TextWrapping.Wrap });
        AddDependency(panel, "1. 网页运行环境", _web, "打开 Microsoft 官方安装页", "https://developer.microsoft.com/microsoft-edge/webview2");
        AddDependency(panel, "2. 建行网银组件和 UKey 驱动", _bank, "打开建设银行官方下载页", "https://ccb.com/cn/html1/office/ebank/dzb/subject/center/download/corporate_index.html");
        panel.Children.Add(new TextBlock { Text = "3. 本机认证组件", FontSize = 16, Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(_brokerText);
        _install.Click += async (_, _) => await InstallAsync(); panel.Children.Add(_install);
        panel.Children.Add(_summary);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _recheck.Click += async (_, _) => await RecheckAsync(); actions.Children.Add(_recheck);
        _next.IsEnabled = false;
        if (canOpenApplication)
        {
            _next.Margin = new Thickness(12, 0, 0, 0);
            _next.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); Close(); }
                catch { _summary.Text = "无法打开应用，请从开始菜单重新启动。"; }
            };
            actions.Children.Add(_next);
        }
        panel.Children.Add(actions); panel.Children.Add(_probe); Content = panel;
        Loaded += async (_, _) => await RecheckAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private static TextBlock Status() => new() { Text = "等待检查", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 9) };
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private void AddDependency(Panel panel, string title, TextBlock status, string label, string url)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, Margin = new Thickness(0, 14, 0, 0) }); panel.Children.Add(status);
        var button = Button(label);
        button.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); _summary.Text = "请完成官方安装，再点击“重新检查”。取消安装或需要重启时，环境仍未就绪。"; }
            catch { _summary.Text = "无法打开浏览器，请检查默认浏览器和网络连接后重试。"; }
        };
        panel.Children.Add(button);
    }
    private async Task InstallAsync()
    {
        if (_busy) return; SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await _broker.InstallAsync(timeout.Token); _brokerText.Text = _broker.CurrentState.StatusText;
        }
        catch { _brokerText.Text = "认证组件安装未完成。可重新安装；请使用同一 Windows 用户确认管理员权限。"; }
        finally { SetBusy(false); }
        if (!_lifetime.IsCancellationRequested) await RecheckAsync();
    }
    private async Task RecheckAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return; SetBusy(true);
        _summary.Text = "正在检查环境；不会登录网银或读取 PIN。";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var bank = CcbNativeHostReadiness.IsInstalled();
            _bank.Text = bank ? "已检测到建行组件；实际 UKey 通信会在启动业务时检查。" : "未检测到完整建行组件。请安装官方网银组件及对应 UKey 驱动。";
            bool web; try { await ProbeWebViewAsync(timeout.Token); web = true; } catch { web = false; }
            _web.Text = web ? "网页运行环境已就绪。" : "网页运行环境缺失或不兼容。请安装 / 更新 WebView2 Evergreen Runtime 后重新检查。";
            bool broker; try { broker = await _broker.RefreshHealthAsync(timeout.Token).WaitAsync(timeout.Token); } catch { broker = false; }
            _brokerText.Text = broker ? "本机认证组件已就绪。" : "本机认证组件未就绪，请安装或修复。";
            var ready = new DependencyReadiness(web, bank, broker).IsReady; _next.IsEnabled = ready;
            _summary.Text = ready ? "基础环境检查通过。打开应用后插入 UKey，使用“设置 PIN”完成本机验证。" : "环境尚未就绪。完成上述安装后重新检查；若安装提示重启，请先重启电脑。";
        }
        catch { _summary.Text = "检查未完成，请重新检查。"; _next.IsEnabled = false; }
        finally { SetBusy(false); }
    }
    private async Task ProbeWebViewAsync(CancellationToken token)
    {
        // A blank view verifies extension APIs without bank navigation or native authentication.
        _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        var session = new BrowserSessionDirectory(); CoreWebView2Environment? environment = null;
        using var view = new WebView2(); _probe.Content = view;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(null, session.EnsureCreated(), CcbUKeyWebView2Environment.CreateOptions()).WaitAsync(token);
            environment.BrowserProcessExited += (_, _) => session.RetireAndTryCleanup();
            await view.EnsureCoreWebView2Async(environment).WaitAsync(token);
            await new CcbUKeyBrowserExtensionInstaller().EnsureInstalledAsync(new WebView2BrowserExtensionProfile(view.CoreWebView2.Profile),
                CcbUKeyBrowserExtensionPackage.ResolveFromBaseDirectory(AppContext.BaseDirectory), token);
        }
        finally { _probe.Content = null; if (environment is null) session.RetireAndTryCleanup(); }
    }
    private void SetBusy(bool value) { _busy = value; _recheck.IsEnabled = _install.IsEnabled = !value; if (value) _next.IsEnabled = false; }
}
