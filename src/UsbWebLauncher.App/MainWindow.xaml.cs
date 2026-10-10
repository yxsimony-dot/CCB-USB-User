using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.App.Configuration;
using Microsoft.Win32;
using UsbWebLauncher.App.Updates;

namespace UsbWebLauncher.App;

public partial class MainWindow : Window
{
    private UpdatePresentationController? _updates;
    private UpdateWindow? _updateWindow;

    public void ConfigureUpdates(UpdatePresentationController updates)
    {
        _updates = updates;
        UpdateNotice.DataContext = updates;
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        _updateWindow = new UpdateWindow(_updates) { Owner = this };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        if (string.IsNullOrEmpty(_updates.AvailableVersion)) await _updates.CheckAsync();
    }

    public event EventHandler? RetryRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = "USB Web Launcher " + ProductVersion.Display;
        DataContext = ViewModel;
        RefreshDownloadDirectory();
    }

    public LauncherViewModel ViewModel { get; } = new();

    public DownloadDirectorySettings DownloadSettings { get; } = new();

    public void ShowDownloadDirectoryError(string message) =>
        Dispatcher.BeginInvoke(new Action(() => DownloadDirectoryText.Text = message));

    private void RefreshDownloadDirectory() => DownloadDirectoryText.Text =
        DownloadSettings.LoadError ?? "下载目录：" + DownloadSettings.DirectoryPath;

    private void OnConfigureDownloadDirectoryClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择流水、电子回单 PDF 和其他下载文件的保存文件夹",
            Multiselect = false,
            InitialDirectory = System.IO.Directory.Exists(DownloadSettings.DirectoryPath)
                ? DownloadSettings.DirectoryPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            DownloadSettings.Save(dialog.FolderName);
            RefreshDownloadDirectory();
        }
        catch
        {
            MessageBox.Show(this, "无法保存下载目录。请确认所选文件夹可写、磁盘空间充足后重试。原设置未更改。",
                "下载目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public WebView2 BrowserControl => (WebView2)BrowserContainer.Content;

    public WebView2 CreateBrowserControl()
    {
        if (BrowserContainer.Content is not null) throw new InvalidOperationException("Previous browser is still attached.");
        var browser = new WebView2();
        BrowserContainer.Content = browser;
        return browser;
    }

    public void DetachBrowserControl() => BrowserContainer.Content = null;

    public void ActivateForBrowser()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDependencyPreparationClicked(object sender, RoutedEventArgs e) =>
        new DependencyPreparationWindow(canOpenApplication: false) { Owner = this }.ShowDialog();

    private void OnMaintenanceMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
