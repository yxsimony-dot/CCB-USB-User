using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class UsbKeyPresencePresentationTests
{
    [Fact]
    public void IdlePresenceUpdatesHintWithoutStartingBusinessOrOpeningBrowser()
    {
        var model = new LauncherViewModel();
        var starts = 0;
        model.ConfigureBusinessCommand(_ => { starts++; return Task.CompletedTask; });
        model.UpdateBusinessState(true, false, false, false);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        model.UpdateUsbKeyPresence(true);
        Assert.Contains("已检测到可用 USB Key", model.CurrentCompanyText);
        Assert.Contains(nameof(model.CurrentCompanyText), changed);
        Assert.False(model.IsBrowserVisible);
        Assert.Equal(BusinessSessionState.Idle, model.SessionState);
        Assert.True(model.CanStartBusiness);
        Assert.Equal(0, starts);

        model.UpdateUsbKeyPresence(false);
        Assert.Contains("未检测到", model.CurrentCompanyText);
        model.UpdateUsbKeyPresence(null);
        Assert.Contains("检测失败", model.CurrentCompanyText);
        model.UpdateUsbKeyPresence(true);
        Assert.Contains("已检测到可用 USB Key", model.CurrentCompanyText);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void BusinessCompanyHasPriorityAndStopRestoresLatestPresenceWithoutOldCompany()
    {
        var model = new LauncherViewModel();
        model.UpdateUsbKeyPresence(true);
        model.UpdateSessionState(BusinessSessionState.Active);
        model.UpdateCurrentCompany("测试公司");
        model.UpdateUsbKeyPresence(false);
        Assert.Equal("测试公司", model.CurrentCompanyText);

        model.UpdateSessionState(BusinessSessionState.Stopping);
        model.ResetSessionPresentation();
        model.UpdateSessionState(BusinessSessionState.Idle);
        Assert.Contains("未检测到", model.CurrentCompanyText);
        Assert.DoesNotContain("测试公司", model.CurrentCompanyText);
        model.UpdateUsbKeyPresence(true);
        Assert.Contains("已检测到可用 USB Key", model.CurrentCompanyText);
        Assert.False(model.IsBrowserVisible);
    }

    [Theory]
    [InlineData(
        UsbKeyDetectionStatus.Absent,
        "未检测到受支持的邮储 Key",
        "等待邮储 Key")]
    [InlineData(
        UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
        "驱动/服务环境不可用",
        "驱动/服务环境不可用")]
    [InlineData(
        UsbKeyDetectionStatus.Ready,
        "可用邮储 Key 已就绪",
        "可用邮储 Key 已就绪")]
    public void PostalSavingsDetectionStatesHaveDistinctSanitizedPresentation(
        UsbKeyDetectionStatus status,
        string expectedHint,
        string expectedDeviceStatus)
    {
        var model = CreatePostalSavingsModel();

        model.UpdateUsbKeyDetectionStatus(status);

        Assert.Contains(expectedHint, model.CurrentCompanyText);
        Assert.Contains(expectedDeviceStatus, model.DeviceStatusText);
        var presentation = model.CurrentCompanyText + model.DeviceStatusText;
        Assert.DoesNotContain("Tendyron", presentation);
        Assert.DoesNotContain("0123456789", presentation);
        Assert.DoesNotContain(
            nameof(UsbKeyDiagnosticCode.ProviderUnavailable),
            presentation);
    }

    [Fact]
    public void PostalSavingsDetectionFailureIsDistinctFromAbsence()
    {
        var model = CreatePostalSavingsModel();

        model.UpdateUsbKeyDetectionStatus(null);

        Assert.Contains("检测失败", model.CurrentCompanyText);
        Assert.DoesNotContain("未检测到", model.CurrentCompanyText);
    }

    [Fact]
    public void SnapshotDuringBankSwitchIsRetainedWithoutStartingBusiness()
    {
        var model = new LauncherViewModel();
        var starts = 0;
        model.BankFlowChangeRequested += (_, _) =>
        {
            model.BeginBankFlowSwitch();
            model.UpdateUsbKeyDetectionStatus(UsbKeyDetectionStatus.Ready);
            model.ConfigureBusinessCommand(_ =>
            {
                starts++;
                return Task.CompletedTask;
            });
            model.UpdateBusinessState(true, false, false, false);
            model.CompleteBankFlowSwitch();
        };

        model.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;
        model.UpdateUsbKeyDetectionStatus(UsbKeyDetectionStatus.Ready);

        Assert.Contains("可用邮储 Key 已就绪", model.CurrentCompanyText);
        Assert.Equal(0, starts);
        Assert.False(model.IsBrowserVisible);
        Assert.Equal(BusinessSessionState.Idle, model.SessionState);
    }

    private static LauncherViewModel CreatePostalSavingsModel()
    {
        var model = new LauncherViewModel();
        model.BankFlowChangeRequested += (_, _) =>
        {
            model.BeginBankFlowSwitch();
            model.UpdateBusinessState(true, false, false, false);
            model.CompleteBankFlowSwitch();
        };
        model.SelectedBankFlow = BankFlowCatalog.PostalSavingsBank;
        return model;
    }
}
