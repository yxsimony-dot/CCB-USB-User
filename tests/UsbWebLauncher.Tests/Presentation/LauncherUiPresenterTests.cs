using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class LauncherUiPresenterTests
{
    [Fact]
    public async Task Present_QueuedOldSessionEventsCannotOverwriteNewSessionOrActivateWindow()
    {
        using var oldSession = new BusinessSessionScope(CancellationToken.None);
        using var newSession = new BusinessSessionScope(CancellationToken.None);
        var viewModel = new LauncherViewModel();
        viewModel.ConfigureLocalPinCommands(NullLocalPinCommands.Instance);
        viewModel.UpdateBusinessState(true, true, false, false);
        var oldDispatcher = new RecordingDispatcher();
        var newDispatcher = new RecordingDispatcher();
        var activations = 0;
        var oldPresenter = new LauncherUiPresenter(viewModel, oldDispatcher, () => activations++, () => oldSession.IsCurrent);
        var newPresenter = new LauncherUiPresenter(viewModel, newDispatcher, () => activations++, () => newSession.IsCurrent);
        oldPresenter.Present(new(LauncherStage.UsbDetected, true, "旧轮设备"));
        oldPresenter.PresentAuthentication(new(AuthenticationStage.Failed, 1, Message: "旧轮登录失败"));

        await oldSession.RevokeAsync();
        newPresenter.Present(new(LauncherStage.Ready, true, "新轮页面"));
        newPresenter.PresentAuthentication(new(AuthenticationStage.Ready, 2, Message: "新轮认证"));
        newDispatcher.RunPending();
        oldDispatcher.RunPending();

        Assert.Equal("新轮页面", viewModel.StatusText);
        Assert.Equal("新轮认证", viewModel.AuthenticationStatusText);
        Assert.Equal(AuthenticationStage.Ready, viewModel.AuthenticationStage);
        Assert.Equal(0, activations);
    }

    [Fact]
    public void Present_UpdatesViewModelOnlyAfterDispatcherRunsAction()
    {
        var viewModel = new LauncherViewModel();
        viewModel.UpdateBusinessState(true, true, false, false);
        var dispatcher = new RecordingDispatcher();
        var presenter = new LauncherUiPresenter(viewModel, dispatcher, () => { });

        presenter.Present(new(
            LauncherStage.Ready,
            true,
            "页面已就绪。"));

        Assert.Equal(LauncherStage.WaitingForUsb, viewModel.Stage);

        dispatcher.RunPending();

        Assert.Equal(LauncherStage.Ready, viewModel.Stage);
        Assert.Equal("页面已就绪。", viewModel.StatusText);
    }

    [Fact]
    public void Present_ActivatesWindowOnlyForUsbDetectedState()
    {
        var viewModel = new LauncherViewModel();
        var dispatcher = new RecordingDispatcher();
        var activationCount = 0;
        var presenter = new LauncherUiPresenter(
            viewModel,
            dispatcher,
            () => activationCount++);

        presenter.Present(new(LauncherStage.WaitingForUsb, false, "等待"));
        presenter.Present(new(LauncherStage.UsbDetected, true, "已识别"));
        presenter.Present(new(LauncherStage.OpeningBrowser, true, "正在打开"));
        dispatcher.RunPending();

        Assert.Equal(1, activationCount);
    }

    [Fact]
    public void PresentAuthentication_UpdatesAuthenticationStateOnDispatcher()
    {
        var viewModel = new LauncherViewModel();
        viewModel.ConfigureLocalPinCommands(NullLocalPinCommands.Instance);
        viewModel.UpdateBusinessState(true, true, false, false);
        var dispatcher = new RecordingDispatcher();
        var presenter = new LauncherUiPresenter(viewModel, dispatcher, () => { });

        presenter.PresentAuthentication(new(
            AuthenticationStage.ValidatingCompatibility,
            3,
            Message: "正在验证兼容性。"));

        Assert.Equal(AuthenticationStage.WaitingForDevice, viewModel.AuthenticationStage);

        dispatcher.RunPending();

        Assert.Equal(AuthenticationStage.ValidatingCompatibility, viewModel.AuthenticationStage);
        Assert.Equal("正在验证兼容性。", viewModel.AuthenticationStatusText);
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunPending()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
