using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class LauncherViewModelTests
{
    [Theory]
    [InlineData(AuthenticationStage.Failed)]
    [InlineData(AuthenticationStage.Incompatible)]
    public void AuthenticationFailure_ReplacesExplicitContinueWithFailureAndReinsertGuidance(AuthenticationStage stage)
    {
        var viewModel = new LauncherViewModel(new RecordingPinCommands());
        viewModel.ConfigureBusinessCommand(_ => Task.CompletedTask);
        viewModel.UpdateState(new(LauncherStage.UsbDetected, true, "已识别 USB Key。"));
        viewModel.UpdateBusinessState(true, true, true, false, "准备完成后，请点击“启动业务”继续。");

        viewModel.UpdateAuthenticationState(new(stage, 7, Message: "本次认证失败，请检查设备。"));

        Assert.Equal("本次认证失败，请检查设备。", viewModel.StatusText);
        Assert.Contains("认证", viewModel.IdleTitle);
        Assert.DoesNotContain("继续", viewModel.IdleTitle);
        Assert.Contains("重新插入", viewModel.IdleDescription);
        Assert.Equal("本次认证失败，请检查设备。", viewModel.ErrorDetail);
        Assert.Equal(stage, viewModel.AuthenticationStage);
    }

    [Theory]
    [InlineData(AuthenticationStage.Failed)]
    [InlineData(AuthenticationStage.Incompatible)]
    public void AuthenticationFailure_WithoutMessageStillExplainsFailure(AuthenticationStage stage)
    {
        var viewModel = new LauncherViewModel(new RecordingPinCommands());
        viewModel.UpdateBusinessState(true, true, true, false);

        viewModel.UpdateAuthenticationState(new(stage, 1));

        Assert.Contains("认证", viewModel.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorDetail));
        Assert.Contains("重新插入", viewModel.IdleDescription);
    }

    [Fact]
    public void AuthenticationFailure_UpdatesAllVisibleStatusBindingsAndClearsOnDeviceRemoval()
    {
        var viewModel = new LauncherViewModel(new RecordingPinCommands());
        viewModel.UpdateBusinessState(true, true, false, false);
        viewModel.UpdateState(new(LauncherStage.UsbDetected, true, "已识别 USB Key。"));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.UpdateAuthenticationState(new(AuthenticationStage.Failed, 1, Message: "本次认证失败。"));

        Assert.Contains(nameof(LauncherViewModel.StatusText), changedProperties);
        Assert.Contains(nameof(LauncherViewModel.IdleTitle), changedProperties);
        Assert.Contains(nameof(LauncherViewModel.IdleDescription), changedProperties);
        Assert.Contains(nameof(LauncherViewModel.ErrorDetail), changedProperties);

        viewModel.UpdateAuthenticationState(new(AuthenticationStage.WaitingForDevice, null, Message: "请重新插入设备。"));
        viewModel.UpdateState(new(LauncherStage.WaitingForUsb, false, "正在等待指定 USB Key…"));

        Assert.Equal("正在等待指定 USB Key…", viewModel.StatusText);
        Assert.Contains("请插入", viewModel.IdleTitle);
        Assert.Null(viewModel.ErrorDetail);
    }

    [Fact]
    public void AuthenticationFailure_BeforeBusinessStartKeepsPreparationStateAndNotifiesWhenStarted()
    {
        var viewModel = new LauncherViewModel(new RecordingPinCommands());
        viewModel.UpdateBusinessState(true, false, false, false);
        viewModel.UpdateAuthenticationState(new(AuthenticationStage.Failed, 1, Message: "本次认证失败。"));
        Assert.Contains("未启动", viewModel.StatusText);
        Assert.Null(viewModel.ErrorDetail);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.UpdateBusinessState(true, true, true, false);

        Assert.Equal("本次认证失败。", viewModel.StatusText);
        Assert.Contains(nameof(LauncherViewModel.ErrorDetail), changedProperties);
    }

    [Fact]
    public void BeforeInitialization_PreparationCommandsAreUnavailable()
    {
        var viewModel = new LauncherViewModel(new RecordingPinCommands());
        viewModel.UpdateAuthenticationState(new(AuthenticationStage.RequiresPinSetup, null));

        Assert.False(viewModel.CanValidateAndSavePin);
        Assert.False(viewModel.CanResetPin);
        Assert.False(viewModel.CanDeletePin);
    }

    [Fact]
    public void AuthenticationBusy_DisablesEnvironmentMaintenance()
    {
        var viewModel = new LauncherViewModel();
        viewModel.ConfigureLocalPinCommands(new RecordingBrokerPinCommands());
        viewModel.UpdateBusinessState(true, true, false, false);
        viewModel.UpdateAuthenticationState(new(AuthenticationStage.UnlockingToken, 1));

        Assert.False(viewModel.CanInstallNativePromptBroker);
    }

    [Theory]
    [InlineData(AuthenticationStage.WaitingForDevice, true, true, true, false)]
    [InlineData(AuthenticationStage.RequiresPinSetup, true, true, true, false)]
    [InlineData(AuthenticationStage.ValidatingCompatibility, false, false, false, true)]
    [InlineData(AuthenticationStage.UnlockingToken, false, false, false, true)]
    [InlineData(AuthenticationStage.AwaitingCertificate, false, false, false, true)]
    [InlineData(AuthenticationStage.Authenticating, false, false, false, true)]
    [InlineData(AuthenticationStage.Ready, false, true, true, false)]
    [InlineData(AuthenticationStage.Incompatible, true, true, true, false)]
    [InlineData(AuthenticationStage.Failed, true, true, true, false)]
    public void UpdateAuthenticationState_MapsSetupCommandsAndBusyState(
        AuthenticationStage stage,
        bool expectedCanValidateAndSave,
        bool expectedCanReset,
        bool expectedCanDelete,
        bool expectedBusy)
    {
        var commands = new RecordingPinCommands();
        var viewModel = new LauncherViewModel(commands);
        viewModel.UpdateBusinessState(true, true, false, false);

        viewModel.UpdateAuthenticationState(new(
            stage,
            7,
            Message: "公开状态"));

        Assert.Equal(stage, viewModel.AuthenticationStage);
        Assert.Equal("公开状态", viewModel.AuthenticationStatusText);
        Assert.Equal(expectedCanValidateAndSave, viewModel.CanValidateAndSavePin);
        Assert.Equal(expectedCanReset, viewModel.CanResetPin);
        Assert.Equal(expectedCanDelete, viewModel.CanDeletePin);
        Assert.Equal(expectedBusy, viewModel.IsAuthenticationBusy);
    }

    [Fact]
    public async Task Commands_DelegateToPinCommandsWithoutAddingAnotherPinStringProperty()
    {
        var commands = new RecordingPinCommands();
        var viewModel = new LauncherViewModel(commands);
        viewModel.UpdateBusinessState(true, true, false, false);
        viewModel.UpdateAuthenticationState(new(AuthenticationStage.RequiresPinSetup, 1));

        await viewModel.ValidateAndSavePinCommand.ExecuteAsync();
        await viewModel.ResetPinCommand.ExecuteAsync();
        await viewModel.DeletePinCommand.ExecuteAsync();

        Assert.Equal(new[] { "validate", "reset", "delete" }, commands.Calls);
        Assert.DoesNotContain(
            typeof(LauncherViewModel).GetProperties(),
            property => property.PropertyType == typeof(string) &&
                        property.Name.Contains("Pin", StringComparison.OrdinalIgnoreCase) &&
                        property.Name != nameof(LauncherViewModel.CurrentPinText));
    }

    [Fact]
    public async Task ConfigureLocalPinCommands_ReplacesStartupNoOpCommandsAfterComposition()
    {
        var commands = new RecordingPinCommands();
        var viewModel = new LauncherViewModel();
        viewModel.UpdateAuthenticationState(new(AuthenticationStage.RequiresPinSetup, 1));

        viewModel.ConfigureLocalPinCommands(commands);
        viewModel.UpdateBusinessState(true, false, false, false);
        await viewModel.ValidateAndSavePinCommand.ExecuteAsync();

        Assert.Equal(new[] { "validate" }, commands.Calls);
    }

    [Fact]
    public async Task ConfigureLocalPinCommands_ExposesInstallOnlyWhileRequiredBrokerIsUnhealthy()
    {
        var commands = new RecordingBrokerPinCommands();
        var viewModel = new LauncherViewModel();

        viewModel.ConfigureLocalPinCommands(commands);
        viewModel.UpdateBusinessState(true, false, false, false);

        Assert.True(viewModel.CanInstallNativePromptBroker);
        Assert.True(viewModel.IsNativePromptBrokerInstallVisible);
        Assert.False(viewModel.CanUninstallNativePromptBroker);
        Assert.Equal("需要安装本机认证组件。", viewModel.NativePromptBrokerStatusText);

        await viewModel.InstallNativePromptBrokerCommand.ExecuteAsync();

        Assert.True(viewModel.CanInstallNativePromptBroker);
        Assert.False(viewModel.IsNativePromptBrokerInstallVisible);
        Assert.True(viewModel.CanUninstallNativePromptBroker);

        await viewModel.InstallNativePromptBrokerCommand.ExecuteAsync();

        Assert.Equal(2, commands.InstallCalls);
    }

    [Theory]
    [InlineData(LauncherStage.WaitingForUsb, false, false)]
    [InlineData(LauncherStage.UsbDetected, false, false)]
    [InlineData(LauncherStage.OpeningBrowser, true, false)]
    [InlineData(LauncherStage.Ready, true, false)]
    [InlineData(LauncherStage.Error, false, true)]
    public void UpdateState_MapsStageToBrowserAndRetryVisibility(
        LauncherStage stage,
        bool expectedBrowserVisible,
        bool expectedCanRetry)
    {
        var viewModel = new LauncherViewModel();
        viewModel.UpdateBusinessState(true, true, false, false);
        var state = new LauncherState(
            stage,
            stage != LauncherStage.WaitingForUsb,
            "状态消息",
            stage == LauncherStage.Error ? "错误详情" : null);

        viewModel.UpdateState(state);

        Assert.Equal(stage, viewModel.Stage);
        Assert.Equal("状态消息", viewModel.StatusText);
        Assert.Equal(expectedBrowserVisible, viewModel.IsBrowserVisible);
        Assert.Equal(expectedCanRetry, viewModel.CanRetry);
        Assert.Equal(state.IsUsbPresent, viewModel.IsUsbPresent);
        Assert.Equal(state.ErrorDetail, viewModel.ErrorDetail);
    }

    [Fact]
    public void UpdateState_RaisesPropertyChangedForVisibleStatus()
    {
        var viewModel = new LauncherViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.UpdateState(new(
            LauncherStage.Ready,
            true,
            "页面已就绪。"));

        Assert.Contains(nameof(LauncherViewModel.StatusText), changedProperties);
        Assert.Contains(nameof(LauncherViewModel.IsBrowserVisible), changedProperties);
        Assert.Contains(nameof(LauncherViewModel.IsUsbPresent), changedProperties);
    }

    [Fact]
    public void UpdateCurrentPin_FormatsVisibleValueAndRaisesPropertyChanged()
    {
        var viewModel = new LauncherViewModel();
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.UpdateCurrentPin("1234");

        Assert.Equal("当前 PIN：1234", viewModel.CurrentPinText);
        Assert.Contains(nameof(LauncherViewModel.CurrentPinText), changedProperties);

        viewModel.UpdateCurrentPin(null);

        Assert.Equal("当前 PIN：未设置", viewModel.CurrentPinText);
    }

    [Fact]
    public async Task BeforeBusinessStart_PinPreparationIsAvailableWithoutDetectingDevice()
    {
        var commands = new RecordingPinCommands();
        var viewModel = new LauncherViewModel(commands);
        var businessCalls = 0;
        viewModel.ConfigureBusinessCommand(_ => { businessCalls++; return Task.CompletedTask; });
        viewModel.UpdateBusinessState(true, false, false, false);

        await viewModel.ValidateAndSavePinCommand.ExecuteAsync();

        Assert.Equal(new[] { "validate" }, commands.Calls);
        Assert.Equal(0, businessCalls);
        Assert.True(viewModel.StartBusinessCommand.CanExecute(null));
        Assert.Equal("正在检测 USB Key…", viewModel.DeviceStatusText);
        Assert.Contains("未启动", viewModel.StatusText);
    }

    [Fact]
    public async Task SharedOperationBusy_BlocksEveryConflictingCommandUntilReleased()
    {
        var commands = new RecordingBrokerPinCommands();
        var viewModel = new LauncherViewModel(commands);
        var businessCalls = 0;
        viewModel.ConfigureBusinessCommand(_ => { businessCalls++; return Task.CompletedTask; });
        viewModel.UpdateBusinessState(true, false, false, false);
        await viewModel.InstallNativePromptBrokerCommand.ExecuteAsync();
        viewModel.UpdateBusinessState(true, false, false, true);

        var conflictingCommands = new[] {
            viewModel.ValidateAndSavePinCommand, viewModel.ResetPinCommand,
            viewModel.DeletePinCommand, viewModel.InstallNativePromptBrokerCommand,
            viewModel.UninstallNativePromptBrokerCommand, viewModel.StartBusinessCommand
        };
        foreach (var command in conflictingCommands)
        {
            Assert.False(command.CanExecute(null));
            await command.ExecuteAsync();
        }
        Assert.Equal(1, commands.InstallCalls);
        Assert.Equal(0, businessCalls);

        viewModel.UpdateBusinessState(true, false, false, false);
        Assert.All(conflictingCommands, command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task BusinessCommand_OnlyRunsForInitialStartExplicitContinueOrErrorRetry()
    {
        var viewModel = new LauncherViewModel();
        var calls = 0;
        viewModel.ConfigureBusinessCommand(_ => { calls++; return Task.CompletedTask; });
        Assert.False(viewModel.StartBusinessCommand.CanExecute(null));
        viewModel.UpdateBusinessState(true, false, false, false);
        await viewModel.StartBusinessCommand.ExecuteAsync();
        viewModel.UpdateBusinessState(true, true, false, false);
        await viewModel.StartBusinessCommand.ExecuteAsync();
        Assert.Equal(1, calls);
        Assert.False(viewModel.IsBusy);

        viewModel.UpdateBusinessState(true, true, true, false);
        await viewModel.StartBusinessCommand.ExecuteAsync();
        viewModel.UpdateBusinessState(true, true, false, false);
        viewModel.UpdateState(new(LauncherStage.Error, true, "业务失败"));
        await viewModel.StartBusinessCommand.ExecuteAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public void NoAuthenticationConfigured_PreparationIsInapplicableButBusinessCanStart()
    {
        var viewModel = new LauncherViewModel();
        viewModel.ConfigureBusinessCommand(_ => Task.CompletedTask);
        viewModel.UpdateBusinessState(true, false, false, false);

        Assert.False(viewModel.CanValidateAndSavePin);
        Assert.False(viewModel.CanResetPin);
        Assert.False(viewModel.CanDeletePin);
        Assert.False(viewModel.CanInstallNativePromptBroker);
        Assert.False(viewModel.CanUninstallNativePromptBroker);
        Assert.Contains("不适用", viewModel.AuthenticationStatusText);
        Assert.Contains("不适用", viewModel.NativePromptBrokerStatusText);
        Assert.True(viewModel.CanStartBusiness);
    }

    [Fact]
    public void AwaitingExplicitContinue_PreservesReadyBrowserAndActualDeviceState()
    {
        var viewModel = new LauncherViewModel();
        viewModel.UpdateState(new(LauncherStage.Ready, true, "页面已就绪。"));
        viewModel.UpdateBusinessState(true, true, true, false);

        Assert.True(viewModel.IsBrowserVisible);
        Assert.Equal("USB Key 已连接", viewModel.DeviceStatusText);
        Assert.Contains("启动业务", viewModel.StatusText);
    }

    private sealed class RecordingPinCommands : ILocalPinCommands
    {
        public List<string> Calls { get; } = [];

        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("validate");
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("reset");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("delete");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBrokerPinCommands : ILocalPinCommands, INativePromptBrokerCommands
    {
        private NativePromptBrokerPrerequisiteState _state = new(
            true,
            false,
            false,
            "需要安装本机认证组件。");

        public event EventHandler? StateChanged;
        public NativePromptBrokerPrerequisiteState CurrentState => _state;
        public int InstallCalls { get; private set; }
        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            _state = _state with
            {
                IsHealthy = true,
                IsInstalled = true,
                StatusText = "本机认证组件已就绪。"
            };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
