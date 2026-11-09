using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BankRuntimeCompositionTests
{
    [Fact]
    public void ConstructionBankCreatesOnlyConstructionBankComponents()
    {
        var observer = new RecordingObserver();
        var components = new BankRuntimeComponentComposer(
            BankFlowCatalog.ChinaConstructionBank,
            observer);
        var pageFactoryCalls = 0;

        var runtimeKind = components.CreateClientCertificateAuthentication(
            kind => new RuntimeMarker(kind));
        var pageCoordinator = components.CreateCcbPageCoordinator(() =>
        {
            pageFactoryCalls++;
            return new PageMarker();
        });

        Assert.Equal(
            BankAuthenticationRuntimeKind.ChinaConstructionBank,
            runtimeKind.Kind);
        Assert.NotNull(pageCoordinator);
        Assert.Equal(1, pageFactoryCalls);
        Assert.DoesNotContain(
            observer.Events,
            item =>
                item.AuthenticationRuntime ==
                BankAuthenticationRuntimeKind.PostalSavingsBank);
        Assert.Contains(
            observer.Events,
            item =>
                item.Component == BankRuntimeComponent.CcbPageCoordination &&
                item.State == BankRuntimeComponentState.Created);
    }

    [Fact]
    public void PostalSavingsBankCreatesOnlyPostalSavingsAuthentication()
    {
        var observer = new RecordingObserver();
        var components = new BankRuntimeComponentComposer(
            BankFlowCatalog.PostalSavingsBank,
            observer);
        var pageFactoryCalls = 0;

        var runtimeKind = components.CreateClientCertificateAuthentication(
            kind => new RuntimeMarker(kind));
        var pageCoordinator = components.CreateCcbPageCoordinator(() =>
        {
            pageFactoryCalls++;
            return new PageMarker();
        });

        Assert.Equal(
            BankAuthenticationRuntimeKind.PostalSavingsBank,
            runtimeKind.Kind);
        Assert.Null(pageCoordinator);
        Assert.Equal(0, pageFactoryCalls);
        Assert.DoesNotContain(
            observer.Events,
            item =>
                item.Component == BankRuntimeComponent.CcbPageCoordination &&
                item.State == BankRuntimeComponentState.Created);
        Assert.Contains(
            observer.Events,
            item =>
                item.Component == BankRuntimeComponent.CcbPageCoordination &&
                item.State == BankRuntimeComponentState.Skipped);
    }

    [Fact]
    public void ResolveRejectsOverlappingBankCapabilities()
    {
        var invalid = BankFlowCatalog.PostalSavingsBank with
        {
            BrowserCapabilities = BankBrowserCapabilities.PostalSavingsBank with
            {
                EnableCcbPageCoordination = true
            }
        };

        Assert.Throws<InvalidOperationException>(
            () => BankRuntimeComposition.Resolve(invalid));
    }

    private sealed record RuntimeMarker(
        BankAuthenticationRuntimeKind Kind);

    private sealed class PageMarker;

    private sealed class RecordingObserver : IBankRuntimeCompositionObserver
    {
        public List<BankRuntimeCompositionEvent> Events { get; } = [];

        public void Record(BankRuntimeCompositionEvent observation) =>
            Events.Add(observation);
    }
}
