using System.ComponentModel;
using System.Reflection;
using UsbWebLauncher.App.Updates;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.App;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class UpdatePresentationControllerTests
{
    [Fact]
    public async Task UpdateWindowCanBindReadOnlyProgressAndRender()
    {
        var completion = new TaskCompletionSource();
        var disposal = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            UpdateWindow? window = null;
            UpdatePresentationController? ui = null;
            try
            {
                ui = Controller(new ControlledService());
                window = new UpdateWindow(ui) { ShowInTaskbar = false, Left = -10000, Top = -10000,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
                window.Show();
                window.UpdateLayout();
                Assert.True(window.IsVisible);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                window?.Close();
                disposal.TrySetResult(
                    ui?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await (await disposal.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static readonly UpdateRelease Release = new("1.1.10", "<script>plain text</script>", "setup.exe", 4,
        new string('a', 64), new Uri("https://github.com/yxsimony-dot/CCB-USB-User/releases/download/v1.1.10/setup.exe"),
        new string('b', 40), "unsigned");

    [Fact]
    public async Task StartupAndRepeatedManualChecksShareOneRequestWithoutDownloadOrInstall()
    {
        var service = new ControlledService();
        var vm = UpdateHandoffGateTests.Ready();
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => throw new Exception("unexpected launch"), () => { });
        var first = ui.CheckOnStartupAsync();
        var second = ui.CheckAsync();
        service.Check.SetResult(new(UpdateCheckStatus.UpdateAvailable, "发现新版", Release));
        await Task.WhenAll(first, second, ui.CheckOnStartupAsync());
        Assert.Equal(1, service.CheckCount);
        Assert.Equal("1.1.10", ui.AvailableVersion);
        Assert.Equal(Release.ReleaseNotes, ui.ReleaseNotes);
        Assert.Contains("1.1.10", ui.StartupNotice);
        Assert.True(ui.CanDownload);
        Assert.False(ui.CanInstall);
        Assert.Equal(0, service.DownloadCount);
    }

    [Fact]
    public async Task ClosedWindowIgnoresLateCheckAndDownloadProgress()
    {
        var service = new ControlledService();
        await using var ui = Controller(service);
        var pending = ui.CheckAsync();
        ui.CancelPending();
        var status = ui.StatusText;
        service.Check.SetResult(new(UpdateCheckStatus.UpdateAvailable, "late", Release));
        await pending;
        Assert.Equal(status, ui.StatusText);
        Assert.Empty(ui.AvailableVersion);
        await ui.CheckAsync();
        await ui.DownloadAsync();
        ui.CancelPending();
        var progress = ui.Progress;
        service.Progress!.Report(new(4, 4));
        Assert.Equal(progress, ui.Progress);
    }

    [Theory]
    [InlineData(BusinessSessionState.Active)]
    [InlineData(BusinessSessionState.StopFailed)]
    public async Task DownloadDuringBusinessCannotLaunchUntilIdle(BusinessSessionState state)
    {
        var service = new ControlledService();
        var vm = UpdateHandoffGateTests.Ready();
        var launched = false;
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => launched = true, () => { });
        await ReadyDownload(ui, service);
        vm.UpdateSessionState(state);
        await ui.InstallAsync();
        Assert.False(launched);
        Assert.Contains("中止业务", ui.StatusText);
        Assert.False(vm.UpdateInstallInProgress);
    }

    [Fact]
    public async Task ReverifyLocksBusinessAndLaunchFailureRestoresRetry()
    {
        var service = new ControlledService { Verification = new() };
        var vm = UpdateHandoffGateTests.Ready();
        var closes = 0;
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => throw new IOException("launch failed"), () => closes++);
        await ReadyDownload(ui, service);
        var install = ui.InstallAsync();
        Assert.False(vm.CanStartBusiness);
        vm.UpdateUsbKeyPresence(false);
        vm.UpdateUsbKeyPresence(true);
        Assert.False(vm.CanStartBusiness);
        service.Verification.SetResult();
        await install;
        Assert.True(vm.CanStartBusiness);
        Assert.True(ui.CanInstall);
        Assert.Equal(0, closes);
        Assert.Contains("launch failed", ui.StatusText);
    }

    [Fact]
    public async Task ShutdownDuringVerificationNeverLaunchesInstaller()
    {
        var service = new ControlledService { Verification = new() };
        var vm = UpdateHandoffGateTests.Ready();
        var launched = false;
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => launched = true, () => { });
        await ReadyDownload(ui, service);
        var pending = ui.InstallAsync();
        vm.UpdateSessionState(BusinessSessionState.Closing);
        var dispose = ui.DisposeAsync().AsTask();
        service.Verification.SetResult();
        await Task.WhenAll(pending, dispose);
        Assert.False(launched);
    }

    [Fact]
    public async Task ChangedCacheIsNotLaunchedAndRequiresRedownload()
    {
        var service = new ControlledService { Verification = new() };
        var launched = false;
        await using var ui = new UpdatePresentationController(service, UpdateHandoffGateTests.Ready(), "1.1.0", _ => launched = true, () => { });
        await ReadyDownload(ui, service);
        service.Verification.SetException(new InvalidDataException("changed cache"));
        await ui.InstallAsync();
        Assert.False(launched);
        Assert.False(ui.CanInstall);
        Assert.True(ui.CanDownload);
    }

    [Fact]
    public async Task InitializationInvalidatedDuringVerificationDoesNotLaunch()
    {
        var service = new ControlledService { Verification = new() };
        var vm = UpdateHandoffGateTests.Ready();
        var launched = false;
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => launched = true, () => { });
        await ReadyDownload(ui, service);
        var pending = ui.InstallAsync();
        vm.UpdateBusinessState(false, false, false, false);
        service.Verification.SetResult();
        await pending;
        Assert.False(launched);
        Assert.False(vm.UpdateInstallInProgress);
    }

    [Fact]
    public async Task SuccessfulLaunchRequestsNormalCloseAndRetainsStartLock()
    {
        var service = new ControlledService();
        var vm = UpdateHandoffGateTests.Ready();
        var events = new List<string>();
        await using var ui = new UpdatePresentationController(service, vm, "1.1.0", path => events.Add(path), () => events.Add("close"));
        await ReadyDownload(ui, service);
        await ui.InstallAsync();
        Assert.Equal(new[] { "verified-setup.exe", "close" }, events);
        Assert.False(vm.CanStartBusiness);
        Assert.Contains("安装器已启动", ui.StatusText);
        Assert.DoesNotContain("升级完成", ui.StatusText);
    }

    [Fact]
    public async Task DisposeWaitsForPendingWorkSuppressesLatePublicationAndDisposesServiceOnce()
    {
        var service = new ControlledService();
        var vm = UpdateHandoffGateTests.Ready();
        var ui = new UpdatePresentationController(service, vm, "1.1.0", _ => { }, () => { });
        var publications = 0;
        ui.PropertyChanged += (_, _) => publications++;
        Assert.True(HasBusinessSubscription(vm, ui));

        var pending = ui.CheckAsync();
        var beforeDispose = publications;
        var firstDispose = ui.DisposeAsync().AsTask();
        var secondDispose = ui.DisposeAsync().AsTask();

        Assert.Same(firstDispose, secondDispose);
        Assert.False(firstDispose.IsCompleted);
        Assert.False(HasBusinessSubscription(vm, ui));

        service.Check.SetResult(new(UpdateCheckStatus.UpdateAvailable, "late", Release));
        await Task.WhenAll(pending, firstDispose);
        vm.UpdateSessionState(BusinessSessionState.Active);

        Assert.Equal(beforeDispose, publications);
        Assert.Empty(ui.AvailableVersion);
        Assert.Equal(1, service.DisposeCount);
    }

    private static UpdatePresentationController Controller(ControlledService service) =>
        new(service, UpdateHandoffGateTests.Ready(), "1.1.0", _ => { }, () => { });

    private static async Task ReadyDownload(UpdatePresentationController ui, ControlledService service)
    {
        service.Check.SetResult(new(UpdateCheckStatus.UpdateAvailable, "发现新版", Release));
        await ui.CheckAsync();
        await ui.DownloadAsync();
    }

    private sealed class ControlledService : IApplicationUpdateService
    {
        public TaskCompletionSource<UpdateCheckResult> Check { get; } = new();
        public TaskCompletionSource? Verification { get; init; }
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public IProgress<UpdateDownloadProgress>? Progress { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) { CheckCount++; return Check.Task; }
        public Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            Progress = progress;
            return Task.FromResult(new DownloadedUpdate(release, "verified-setup.exe"));
        }
        public Task VerifyDownloadedAsync(DownloadedUpdate downloaded, CancellationToken cancellationToken) => Verification?.Task ?? Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static bool HasBusinessSubscription(
        LauncherViewModel business,
        UpdatePresentationController controller)
    {
        var field = typeof(LauncherViewModel).GetField(
            nameof(INotifyPropertyChanged.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handlers = (PropertyChangedEventHandler?)field?.GetValue(business);
        return handlers?.GetInvocationList().Any(handler =>
            ReferenceEquals(handler.Target, controller)) == true;
    }
}
