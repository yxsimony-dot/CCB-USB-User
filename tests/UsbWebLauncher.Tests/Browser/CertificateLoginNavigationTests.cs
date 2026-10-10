using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class CertificateLoginNavigationTests
{
    private static readonly Uri Entry = new("https://b2bbank.ccb.com/NCCB/MNECV6B2BMainPlat_UN?TXCODE=N7N010&LOGIN_TYPE=UC");
    private static readonly Uri Login = new("https://b2b3.ccb.com/NCCB/V6/b2bindex.jsp");

    [Theory]
    [InlineData("ready")]
    [InlineData("unsupported")]
    public void AddressChangeDuringInspectionWaitsAndCannotAuthorizeSubmission(string status)
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);

        Assert.Equal(CertificateLoginPageState.WaitingForPage,
            navigation.CompleteInspection(inspection, Login, status, "old-document-ticket"));
        Assert.Null(navigation.TakeTicket(Login));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("unsupported")]
    public void SameAddressReloadDuringInspectionInvalidatesEvenCompletedNavigation(string status)
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);
        navigation.NavigationStarting(7);
        navigation.NavigationCompleted(7);

        Assert.Equal(CertificateLoginPageState.WaitingForPage,
            navigation.CompleteInspection(inspection, Entry, status, "old-document-ticket"));
        Assert.Null(navigation.TakeTicket(Entry));
    }

    [Fact]
    public void EarlierNavigationCompletionCannotMakeCurrentNavigationStable()
    {
        var navigation = new CertificateLoginNavigation();
        navigation.NavigationStarting(7);
        navigation.NavigationStarting(8);
        navigation.NavigationCompleted(7);

        Assert.True(navigation.IsNavigating);
        var inspection = navigation.BeginInspection(Entry);
        Assert.Equal(CertificateLoginPageState.WaitingForPage,
            navigation.CompleteInspection(inspection, Entry, "unsupported", "ticket"));
        navigation.NavigationCompleted(8);
        Assert.False(navigation.IsNavigating);
    }

    [Fact]
    public void NavigationAfterReadyPreventsUsingTicketOnSameAddressNewDocument()
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);
        Assert.Equal(CertificateLoginPageState.Ready,
            navigation.CompleteInspection(inspection, Entry, "ready", "ticket"));
        navigation.NavigationStarting(7);
        navigation.NavigationCompleted(7);

        Assert.Null(navigation.TakeTicket(Entry));
    }

    [Fact]
    public void NewInspectionDiscardsPreviouslyIssuedTicket()
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);
        navigation.CompleteInspection(inspection, Entry, "ready", "ticket");
        navigation.BeginInspection(Entry);

        Assert.Null(navigation.TakeTicket(Entry));
    }

    [Fact]
    public void StableReadyTicketCanBeConsumedOnlyOnce()
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);
        Assert.Equal(CertificateLoginPageState.Ready,
            navigation.CompleteInspection(inspection, Entry, "ready", "ticket"));

        Assert.Equal("ticket", navigation.TakeTicket(Entry));
        Assert.Null(navigation.TakeTicket(Entry));
    }

    [Fact]
    public void WrongAddressConsumesTicketWithoutAuthorizingIt()
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);
        navigation.CompleteInspection(inspection, Entry, "ready", "ticket");

        Assert.Null(navigation.TakeTicket(Login));
        Assert.Null(navigation.TakeTicket(Entry));
    }

    [Theory]
    [InlineData("unsupported", CertificateLoginPageState.Unsupported)]
    [InlineData("waiting", CertificateLoginPageState.WaitingForPage)]
    [InlineData(null, CertificateLoginPageState.WaitingForPage)]
    public void StablePageRetainsItsActualResult(string? status, CertificateLoginPageState expected)
    {
        var navigation = new CertificateLoginNavigation();
        var inspection = navigation.BeginInspection(Entry);

        Assert.Equal(expected, navigation.CompleteInspection(inspection, Entry, status, "ticket"));
        Assert.Null(navigation.TakeTicket(Entry));
    }
}
