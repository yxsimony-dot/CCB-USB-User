using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.App.Updates;

// Called on the UI dispatcher; the service owns network and file I/O.
public sealed class UpdatePresentationController : INotifyPropertyChanged, IDisposable
{
    private readonly IApplicationUpdateService _service;
    private readonly LauncherViewModel _business;
    private readonly Action<string> _launchInstaller;
    private readonly Action _closeApplication;
    private CancellationTokenSource? _pendingSource;
    private Task? _checkTask;
    private UpdateRelease? _release;
    private DownloadedUpdate? _downloaded;
    private long _revision;
    private bool _disposed;
    private bool _startupChecked;
    private bool _handedOff;

    public UpdatePresentationController(IApplicationUpdateService service, LauncherViewModel business,
        string currentVersion, Action<string> launchInstaller, Action closeApplication)
    {
        _service = service;
        _business = business;
        CurrentVersion = currentVersion;
        _launchInstaller = launchInstaller;
        _closeApplication = closeApplication;
        business.PropertyChanged += OnBusinessChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion { get; }
    public string StatusText { get; private set; } = "点击检查更新以获取正式版本。";
    public string AvailableVersion => _release?.Version ?? string.Empty;
    public string ReleaseNotes => _release?.ReleaseNotes ?? string.Empty;
    public string SignatureNotice => _release?.SignatureStatus == "signed"
        ? "清单标记已签名（未验证签名）；SHA-256 仅验证文件完整性。"
        : "安装包未签名；SHA-256 仅验证文件完整性。";
    public string StartupNotice { get; private set; } = string.Empty;
    public bool CanCheck => !_disposed && !IsBusy && !_handedOff;
    public bool CanInstall => CanCheck && _downloaded is not null && _business.CanInstallUpdate;
    public bool CanDownload => CanCheck && _release is not null && _downloaded is null;
    public bool IsBusy { get; private set; }
    public double Progress { get; private set; }
    public string InstallationGuidance =>
        "1.0.0 没有更新入口，需要先手动安装一次支持更新的版本。安装器启动后本程序正常退出；若取消安装，可从原入口打开旧版重试。";
    public string InstallationTargetNotice => GetInstallationTargetNotice(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string GetInstallationTargetNotice(string runningDirectory, string localApplicationData)
    {
        var target = Path.Combine(localApplicationData, "Programs", "UsbWebLauncher");
        var development = !string.Equals(Path.GetFullPath(runningDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        return (development ? "当前从开发或其他目录运行。更新不会覆盖此目录；安装目标：" : "安装目标：") + target +
            "。沿用当前用户，保留 PIN、下载目录和已有 PDF。";
    }

    public static void LaunchInteractiveInstaller(string filePath)
    {
        // No runas verb, alternate credentials, silent switches or process termination.
        using var process = Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        if (process is null) throw new InvalidOperationException("无法启动安装器。");
    }

    public Task CheckOnStartupAsync()
    {
        if (_startupChecked || _disposed) return _checkTask ?? Task.CompletedTask;
        _startupChecked = true;
        return CheckAsync();
    }

    public Task CheckAsync()
    {
        if (_checkTask is { IsCompleted: false }) return _checkTask;
        if (!CanCheck) return Task.CompletedTask;
        _checkTask = CheckCoreAsync();
        return _checkTask;
    }

    private async Task CheckCoreAsync()
    {
        var operation = Begin("正在检查更新…");
        try
        {
            var result = await _service.CheckAsync(operation.Source.Token);
            if (!IsCurrent(operation.Revision)) return;
            _release = result.Release;
            _downloaded = null;
            StatusText = result.Message;
            StartupNotice = result.Release is null ? string.Empty : $"发现新版 {result.Release.Version}，可在维护菜单中选择“检查更新”。";
        }
        catch (OperationCanceledException) { if (IsCurrent(operation.Revision)) StatusText = "检查已取消，可重试。"; }
        catch (Exception ex) { if (IsCurrent(operation.Revision)) StatusText = "检查更新失败：" + ex.Message; }
        finally { End(operation); }
    }

    public async Task DownloadAsync()
    {
        if (!CanDownload || _release is null) return;
        var release = _release;
        var operation = Begin("正在下载安装包…");
        var context = SynchronizationContext.Current;
        var progress = new DownloadProgress(value =>
        {
            void Apply()
            {
                if (!IsCurrent(operation.Revision) || !IsBusy) return;
                Progress = Math.Clamp(value.Percentage, 0, 100);
                Publish();
            }
            if (context is null || SynchronizationContext.Current == context) Apply();
            else context.Post(_ => Apply(), null);
        });
        try
        {
            var downloaded = await _service.DownloadAsync(release, progress, operation.Source.Token);
            if (!IsCurrent(operation.Revision)) return;
            _downloaded = downloaded;
            Progress = 100;
            StatusText = "下载完成，大小和 SHA-256 校验通过。请先中止业务，再选择安装。";
        }
        catch (OperationCanceledException) { if (IsCurrent(operation.Revision)) StatusText = "下载已取消，可重新下载。"; }
        catch (Exception ex) { if (IsCurrent(operation.Revision)) StatusText = "下载失败，可重试：" + ex.Message; }
        finally { End(operation); }
    }

    public async Task InstallAsync()
    {
        if (_disposed || IsBusy || _handedOff || _downloaded is null) return;
        if (!_business.TryBeginUpdateInstall())
        {
            StatusText = "请先中止业务，并等待导出、停止及初始化完成后再安装。";
            Publish();
            return;
        }
        var operation = Begin("正在复核安装包…");
        var verified = false;
        try
        {
            await _service.VerifyDownloadedAsync(_downloaded, operation.Source.Token);
            if (!IsCurrent(operation.Revision)) return;
            operation.Source.Token.ThrowIfCancellationRequested();
            if (!_business.CanContinueUpdateInstall)
                throw new InvalidOperationException("业务状态已变化，请先中止业务后重试。");
            verified = true;
            _launchInstaller(_downloaded.FilePath);
            _handedOff = true;
            StatusText = "安装器已启动，本程序即将正常退出。请在安装器中完成更新；若取消，可从原入口重开旧版重试。";
            Publish();
            _closeApplication();
        }
        catch (OperationCanceledException) { if (IsCurrent(operation.Revision)) StatusText = "安装交接已取消，可重试。"; }
        catch (Exception ex)
        {
            if (IsCurrent(operation.Revision))
            {
                if (!verified) _downloaded = null;
                StatusText = verified ? "安装器未能启动（也可能已取消），可重试：" + ex.Message
                    : "安装包复核未通过，请重新下载：" + ex.Message;
            }
        }
        finally
        {
            if (!_handedOff) _business.EndUpdateInstall();
            End(operation);
        }
    }

    public void CancelPending()
    {
        if (_disposed || _handedOff) return;
        _revision++;
        var source = _pendingSource;
        _pendingSource = null;
        if (source is not null) _ = source.CancelAsync();
        _checkTask = null;
        IsBusy = false;
        StatusText = "更新操作已取消，可重试。";
        Publish();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelPending();
        _disposed = true;
        _revision++;
        _business.PropertyChanged -= OnBusinessChanged;
    }

    private (long Revision, CancellationTokenSource Source) Begin(string message)
    {
        var source = new CancellationTokenSource();
        _pendingSource = source;
        var revision = ++_revision;
        IsBusy = true;
        Progress = 0;
        StatusText = message;
        Publish();
        return (revision, source);
    }

    private void End((long Revision, CancellationTokenSource Source) operation)
    {
        if (IsCurrent(operation.Revision))
        {
            _pendingSource = null;
            IsBusy = false;
            Publish();
        }
        operation.Source.Dispose();
    }

    private bool IsCurrent(long revision) => !_disposed && revision == _revision;
    private void OnBusinessChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.CanInstallUpdate) && !_disposed)
            PropertyChanged?.Invoke(this, new(nameof(CanInstall)));
    }
    private void Publish() { if (!_disposed) PropertyChanged?.Invoke(this, new(null)); }
    private sealed class DownloadProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
