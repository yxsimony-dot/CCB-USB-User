using Microsoft.Web.WebView2.Core;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Browser;

// The profile is shared by the main page and its download/popup windows.
internal sealed class WpfDownloadDirectory : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly DownloadDirectorySettings settings;
    private readonly Action<string> reportError;

    public WpfDownloadDirectory(CoreWebView2 core, DownloadDirectorySettings settings, Action<string> reportError)
    {
        this.core = core;
        this.settings = settings;
        this.reportError = reportError;
        Apply();
        settings.Changed += OnChanged;
        core.DownloadStarting += OnDownloadStarting;
    }

    private void OnChanged(object? sender, EventArgs args) => Apply();

    private void Apply()
    {
        try { core.Profile.DefaultDownloadFolderPath = settings.DirectoryPath; }
        catch { reportError("浏览器无法使用所选下载目录，请点击“下载目录”重新选择。"); }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try { settings.GetWritableDirectory(); }
        catch
        {
            args.Cancel = true;
            args.Handled = true;
            reportError("下载目录不存在或无法写入，请点击“下载目录”重新选择后重试。");
        }
    }

    public void Dispose()
    {
        settings.Changed -= OnChanged;
        core.DownloadStarting -= OnDownloadStarting;
    }
}
