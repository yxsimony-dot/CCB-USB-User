using UsbWebLauncher.App.Presentation;

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
}
