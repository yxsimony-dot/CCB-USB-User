using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class BusinessSessionViewModelTests
{
    [Fact]
    public async Task ActiveStartTaskDoesNotPreventStopAndStopOwnsStateBeforeReentrantNotifications()
    {
        var model = Ready();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        model.ConfigureBusinessCommand(_ => starting.Task);
        model.ConfigureStopBusinessCommand(_ => { stops++; return stopping.Task; });
        var start = model.StartBusinessCommand.ExecuteAsync();
        model.UpdateSessionState(BusinessSessionState.Active);
        model.UpdateBusinessState(true, true, false, true);
        var observed = false;
        model.StopBusinessCommand.CanExecuteChanged += (_, _) =>
        {
            if (model.SessionState != BusinessSessionState.Stopping) return;
            observed = true;
            Assert.False(model.BusinessButtonCommand.CanExecute(null));
            model.StopBusinessCommand.Execute(null);
        };
        try
        {
            Assert.Equal("中止业务", model.BusinessButtonText);
            Assert.True(model.BusinessButtonCommand.CanExecute(null));
            var stop = model.StopBusinessCommand.ExecuteAsync();
            Assert.Equal(BusinessSessionState.Stopping, model.SessionState);
            Assert.True(observed);
            Assert.Equal(1, stops);
            stopping.SetResult();
            await stop;
        }
        finally
        {
            starting.TrySetResult();
            stopping.TrySetResult();
            await start;
        }
    }

    [Theory]
    [InlineData(AuthenticationStage.UnlockingToken, false)]
    [InlineData(AuthenticationStage.Authenticating, false)]
    [InlineData(AuthenticationStage.Ready, true)]
    [InlineData(AuthenticationStage.Failed, false)]
    public void StopRemainsAvailableDuringAuthenticationExportAndFailure(AuthenticationStage authentication, bool exporting)
    {
        var model = Ready();
        model.UpdateSessionState(BusinessSessionState.Active);
        model.UpdateBusinessState(true, true, false, false);
        model.UpdateAuthenticationState(new(authentication, 1));
        model.UpdateExportState(exporting, "本轮进度");

        Assert.True(model.StopBusinessCommand.CanExecute(null));
        Assert.Same(model.StopBusinessCommand, model.BusinessButtonCommand);
        Assert.False(model.StartBusinessCommand.CanExecute(null));
        Assert.False(model.CanValidateAndSavePin);
        Assert.False(model.CanResetPin);
        Assert.False(model.CanDeletePin);
        Assert.False(model.CanInstallNativePromptBroker);
        Assert.False(model.CanUninstallNativePromptBroker);
    }

    [Theory]
    [InlineData(BusinessSessionState.Stopping)]
    [InlineData(BusinessSessionState.StopFailed)]
    [InlineData(BusinessSessionState.Closing)]
    public void CleanupStatesRejectLateBusinessUpdatesAndNewOperations(BusinessSessionState state)
    {
        var model = Ready();
        model.UpdateSessionState(state);
        model.UpdateBusinessState(true, true, true, false, "旧轮登录成功");

        Assert.Equal(state, model.SessionState);
        Assert.False(model.BusinessButtonCommand.CanExecute(null));
        Assert.False(model.CanStartBusiness);
        Assert.False(model.CanExportDocuments);
        Assert.False(model.CanResetPin);
        Assert.DoesNotContain("旧轮", model.StatusText);
    }

    [Fact]
    public void ClosingHasPriorityOverLateStopCompletion()
    {
        var model = Ready();
        model.UpdateSessionState(BusinessSessionState.Closing);
        model.ResetSessionPresentation();
        model.UpdateSessionState(BusinessSessionState.Idle);

        Assert.Equal(BusinessSessionState.Closing, model.SessionState);
        Assert.False(model.BusinessButtonCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetClearsSessionPresentationButRetainsSettingsAndOnlyExplicitStartRunsNextSession()
    {
        var model = Ready();
        var starts = 0;
        model.ConfigureBusinessCommand(_ => { starts++; return Task.CompletedTask; });
        model.SelectedExportNumber = 9;
        model.UpdateCurrentPin("ExampleOnly");
        model.UpdateSessionState(BusinessSessionState.Active);
        model.UpdateState(new(LauncherStage.Ready, true, "旧轮页面", "旧轮错误"));
        model.UpdateBusinessState(true, true, true, true, "旧轮登录");
        model.UpdateAuthenticationState(new(AuthenticationStage.Failed, 1, Message: "旧轮认证"));
        model.UpdateCurrentCompany("旧轮公司");
        model.UpdateExportState(true, "旧轮导出");
        model.UpdateSessionState(BusinessSessionState.Stopping);

        model.ResetSessionPresentation();
        model.UpdateSessionState(BusinessSessionState.Idle);

        Assert.False(model.IsBrowserVisible);
        Assert.False(model.IsBusy);
        Assert.False(model.IsUsbPresent);
        Assert.Null(model.ErrorDetail);
        Assert.Equal(string.Empty, model.ExportStatusText);
        Assert.DoesNotContain("旧轮", model.StatusText + model.AuthenticationStatusText + model.CurrentCompanyText);
        Assert.Equal("当前 PIN：ExampleOnly", model.CurrentPinText);
        Assert.Equal(9, model.SelectedExportNumber);
        Assert.True(model.CanResetPin);
        Assert.False(model.CanExportDocuments);
        Assert.Equal("启动业务", model.BusinessButtonText);
        Assert.Equal(0, starts);
        await model.StartBusinessCommand.ExecuteAsync();
        Assert.Equal(1, starts);
    }

    private static LauncherViewModel Ready()
    {
        var model = new LauncherViewModel(new PreparationCommands());
        model.ConfigureBusinessCommand(_ => Task.CompletedTask);
        model.ConfigureStopBusinessCommand(_ => Task.CompletedTask);
        model.ConfigureDocumentsExportCommand((_, _) => Task.CompletedTask);
        model.UpdateBusinessState(true, false, false, false);
        return model;
    }

    private sealed class PreparationCommands : ILocalPinCommands, INativePromptBrokerCommands
    {
        public event EventHandler? StateChanged { add { } remove { } }
        public NativePromptBrokerPrerequisiteState CurrentState => new(true, true, true, "已安装");
        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
