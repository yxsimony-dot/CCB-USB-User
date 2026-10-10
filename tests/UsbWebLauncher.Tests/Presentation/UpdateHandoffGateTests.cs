using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class UpdateHandoffGateTests
{
    [Theory]
    [InlineData(BusinessSessionState.Active)]
    [InlineData(BusinessSessionState.Stopping)]
    [InlineData(BusinessSessionState.StopFailed)]
    [InlineData(BusinessSessionState.Closing)]
    public void NonIdleSessionCannotInstallEvenWhenNotBusy(BusinessSessionState state)
    {
        var vm = Ready();
        vm.UpdateSessionState(state);
        Assert.False(vm.IsBusy);
        Assert.False(vm.TryBeginUpdateInstall());
    }

    [Fact]
    public async Task HandoffReservesStartBeforeNotificationsAndSurvivesKeyChanges()
    {
        var vm = Ready();
        var starts = 0;
        vm.ConfigureBusinessCommand(_ => { starts++; return Task.CompletedTask; });
        EventHandler reenter = (_, _) => vm.StartBusinessCommand.Execute(null);
        vm.StartBusinessCommand.CanExecuteChanged += reenter;
        Assert.True(vm.TryBeginUpdateInstall());
        vm.UpdateUsbKeyPresence(false);
        vm.UpdateUsbKeyPresence(true);
        await vm.StartBusinessCommand.ExecuteAsync();
        Assert.Equal(0, starts);
        Assert.False(vm.CanStartBusiness);
        vm.StartBusinessCommand.CanExecuteChanged -= reenter;
        vm.EndUpdateInstall();
        await vm.StartBusinessCommand.ExecuteAsync();
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task StartReservesBeforeNotificationsSoInstallCannotWinReentrantly()
    {
        var vm = Ready();
        var attempted = false;
        vm.StartBusinessCommand.CanExecuteChanged += (_, _) =>
        {
            if (attempted) return;
            attempted = true;
            Assert.False(vm.TryBeginUpdateInstall());
        };
        await vm.StartBusinessCommand.ExecuteAsync();
        Assert.True(attempted);
    }

    [Fact]
    public void ExportAndInitializationBlockInstall()
    {
        var vm = Ready();
        vm.UpdateExportState(true, null);
        Assert.False(vm.TryBeginUpdateInstall());
        vm.UpdateExportState(false, null);
        vm.UpdateBusinessState(false, false, true, false);
        Assert.False(vm.TryBeginUpdateInstall());
    }

    internal static LauncherViewModel Ready()
    {
        var vm = new LauncherViewModel();
        vm.ConfigureBusinessCommand(_ => Task.CompletedTask);
        vm.ConfigureStopBusinessCommand(_ => Task.CompletedTask);
        vm.UpdateBusinessState(true, false, false, false);
        return vm;
    }
}
