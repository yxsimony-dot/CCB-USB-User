using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class CurrentCompanyStateTests
{
    [Fact]
    public void OnlyReadsAfterDevicePresentAndFreshSuccessfulNavigation()
    {
        var state = new CurrentCompanyState();
        Assert.Null(state.BeginRead());
        state.SetDevicePresent(true);
        Assert.Null(state.BeginRead());
        state.NavigationStarting();
        state.NavigationCompleted(true);
        Assert.NotNull(state.BeginRead());
    }

    [Fact]
    public void RemovalClearsCompanyAndRejectsLateResult()
    {
        var state = Ready();
        var ticket = state.BeginRead()!.Value;
        state.Apply(ticket, "ready", "测试服务有限公司工会");
        Assert.Equal("测试服务有限公司工会", state.Text);
        state.SetDevicePresent(false);
        state.Apply(ticket, "ready", "旧公司");
        Assert.DoesNotContain("公司", state.Text);
        Assert.Null(state.BeginRead());
        state.SetDevicePresent(true);
        Assert.Null(state.BeginRead());
    }

    [Fact]
    public void NavigationRejectsPreviousPageResultAndWaitsForNewPage()
    {
        var state = Ready();
        var ticket = state.BeginRead()!.Value;
        state.Apply(ticket, "ready", "旧公司");
        state.NavigationStarting();
        state.Apply(ticket, "ready", "旧公司");
        Assert.DoesNotContain("旧公司", state.Text);
        Assert.Null(state.BeginRead());
        state.NavigationCompleted(true);
        state.Apply(state.BeginRead()!.Value, "ready", "新公司分公司");
        Assert.Equal("新公司分公司", state.Text);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("unsupported")]
    [InlineData("ambiguous")]
    public void UnconfirmedPageCannotKeepPreviouslyShownCompany(string status)
    {
        var state = Ready();
        var ticket = state.BeginRead()!.Value;
        state.Apply(ticket, "ready", "旧公司");
        state.Apply(ticket, status, null);
        Assert.DoesNotContain("旧公司", state.Text);
    }

    [Fact]
    public void NavigationFinishingAfterRemovalCannotReenableReadingOnReinsert()
    {
        var state = Ready();
        state.NavigationStarting();
        state.SetDevicePresent(false);
        state.SetDevicePresent(true);
        state.NavigationCompleted(true);
        Assert.Null(state.BeginRead());
    }

    [Fact]
    public void UntrustedOrFailedNavigationNeverAllowsRead()
    {
        var state = Ready();
        state.NavigationStarting();
        state.NavigationCompleted(false);
        Assert.Null(state.BeginRead());
    }

    private static CurrentCompanyState Ready()
    {
        var state = new CurrentCompanyState();
        state.SetDevicePresent(true);
        state.NavigationStarting();
        state.NavigationCompleted(true);
        return state;
    }

    [Fact]
    public void DownloadNavigationCanReinspectOnlyTheSameEstablishedConnection()
    {
        var state = Ready();
        state.NavigationStarting();
        state.NavigationCompleted(false, documentRetained: true);
        Assert.NotNull(state.BeginRead());
        state.SetDevicePresent(false);
        state.SetDevicePresent(true);
        state.NavigationStarting();
        state.NavigationCompleted(false, documentRetained: true);
        Assert.Null(state.BeginRead());
    }
}
