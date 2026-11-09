using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class BankFlowSelectionTests
{
    [Fact]
    public void DefaultsToConstructionBankAndListsConfiguredIndependentPostalSavingsFlow()
    {
        var model = new LauncherViewModel();

        Assert.Equal(BankFlowKind.ChinaConstructionBank, model.SelectedBankFlow.Kind);
        Assert.Collection(
            model.BankFlows,
            flow =>
            {
                Assert.Equal("建设银行", flow.DisplayName);
                Assert.True(flow.IsConfigured);
            },
            flow =>
            {
                Assert.Equal("邮储银行", flow.DisplayName);
                Assert.True(flow.IsConfigured);
                Assert.Equal("psbc-launcher-profile.json", flow.ProfileFileName);
                Assert.NotEqual(
                    BankFlowCatalog.ChinaConstructionBank.ProfileFileName,
                    flow.ProfileFileName);
            });
    }

    [Fact]
    public void SwitchingToPostalSavingsFlowClearsConstructionBankRuntimeState()
    {
        var model = CreateReadyConstructionBankModel();
        model.SelectedExportNumber = 9;
        model.UpdateUsbKeyPresence(true);
        model.UpdateCurrentPinStatus(PinCredentialStatus.Set);
        model.UpdateCurrentCompany("建行测试公司");
        model.UpdateExportState(false, "建行导出完成");
        model.BankFlowChangeRequested += (_, eventArgs) =>
        {
            model.BeginBankFlowSwitch();
            model.ConfigureBusinessCommand(_ => Task.CompletedTask);
            model.UpdateBusinessState(true, false, false, false);
            model.CompleteBankFlowSwitch();
        };

        model.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;

        Assert.Equal(BankFlowKind.PostalSavingsBank, model.SelectedBankFlow.Kind);
        Assert.False(model.IsBankFlowSwitching);
        Assert.True(model.CanChangeBank);
        Assert.True(model.CanStartBusiness);
        Assert.False(model.CanExportDocuments);
        Assert.False(model.CanValidateAndSavePin);
        Assert.False(model.IsAuthenticationConfigured);
        Assert.False(model.IsBrowserVisible);
        Assert.False(model.IsUsbPresent);
        Assert.Equal(1, model.SelectedExportNumber);
        Assert.Equal("当前 PIN：未设置", model.CurrentPinText);
        Assert.Equal(string.Empty, model.ExportStatusText);
        Assert.DoesNotContain("待配置", model.StatusText);
        Assert.Equal("正在检测 USB Key…", model.DeviceStatusText);
        Assert.Equal("正在检测 USB Key…", model.CurrentCompanyText);
        Assert.Contains("当前配置未启用认证", model.AuthenticationStatusText);
        Assert.DoesNotContain("建行测试公司", model.CurrentCompanyText);
        Assert.DoesNotContain("建行导出完成", model.ExportStatusText);

        model.UpdateUsbKeyPresence(true);

        Assert.Contains("可用邮储 Key 已就绪", model.DeviceStatusText);
    }

    [Fact]
    public void ActiveOrBusyBusinessCannotChangeBank()
    {
        var activeModel = CreateReadyConstructionBankModel();
        var activeRequests = 0;
        activeModel.BankFlowChangeRequested += (_, _) => activeRequests++;
        activeModel.UpdateSessionState(BusinessSessionState.Active);
        activeModel.UpdateBusinessState(true, true, false, false);

        activeModel.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;

        Assert.Equal(BankFlowKind.ChinaConstructionBank, activeModel.SelectedBankFlow.Kind);
        Assert.False(activeModel.CanChangeBank);
        Assert.Equal(0, activeRequests);

        var busyModel = CreateReadyConstructionBankModel();
        var busyRequests = 0;
        busyModel.BankFlowChangeRequested += (_, _) => busyRequests++;
        busyModel.UpdateBusinessState(true, false, false, true);

        busyModel.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;

        Assert.Equal(BankFlowKind.ChinaConstructionBank, busyModel.SelectedBankFlow.Kind);
        Assert.False(busyModel.CanChangeBank);
        Assert.Equal(0, busyRequests);
    }

    [Fact]
    public async Task RunningPinMaintenancePreventsBankChangeUntilItFinishes()
    {
        var commands = new BlockingPreparationCommands();
        var model = new LauncherViewModel(commands);
        model.ConfigureBusinessCommand(_ => Task.CompletedTask);
        model.UpdateBusinessState(true, false, false, false);
        var requests = 0;
        model.BankFlowChangeRequested += (_, _) => requests++;

        var maintenance = model.ValidateAndSavePinCommand.ExecuteAsync();

        Assert.True(model.IsBusy);
        Assert.False(model.CanChangeBank);
        model.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;
        Assert.Equal(BankFlowKind.ChinaConstructionBank, model.SelectedBankFlow.Kind);
        Assert.Equal(0, requests);

        commands.Complete();
        await maintenance;

        Assert.False(model.IsBusy);
        Assert.True(model.CanChangeBank);
    }

    [Fact]
    public void ReturningToConstructionBankStartsWithFreshState()
    {
        var model = CreateReadyConstructionBankModel();
        model.BankFlowChangeRequested += (_, eventArgs) =>
        {
            model.BeginBankFlowSwitch();
            model.ConfigureBusinessCommand(_ => Task.CompletedTask);
            model.UpdateBusinessState(true, false, false, false);
            model.CompleteBankFlowSwitch();
        };

        model.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;
        model.SelectedBankFlow = BankFlowCatalog.ChinaConstructionBank;

        Assert.Equal(BankFlowKind.ChinaConstructionBank, model.SelectedBankFlow.Kind);
        Assert.True(model.CanStartBusiness);
        Assert.True(model.CanChangeBank);
        Assert.Equal("正在检测 USB Key…", model.DeviceStatusText);
        Assert.DoesNotContain("邮储银行", model.StatusText);
        Assert.DoesNotContain("待配置", model.CurrentCompanyText);
    }

    private static LauncherViewModel CreateReadyConstructionBankModel()
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
        public NativePromptBrokerPrerequisiteState CurrentState =>
            new(true, true, true, "建行认证组件已就绪。");
        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class BlockingPreparationCommands : ILocalPinCommands
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) =>
            _completion.Task.WaitAsync(cancellationToken);
        public Task ResetAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Complete() => _completion.TrySetResult();
    }
}
