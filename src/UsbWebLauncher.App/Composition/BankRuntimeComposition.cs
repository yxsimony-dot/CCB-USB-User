using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Composition;

public enum BankAuthenticationRuntimeKind
{
    ChinaConstructionBank,
    PostalSavingsBank
}

public enum BankRuntimeComponent
{
    Runtime,
    ClientCertificateAuthentication,
    CcbPageCoordination
}

public enum BankRuntimeComponentState
{
    Selected,
    Created,
    Skipped
}

public sealed record BankRuntimeCompositionEvent(
    BankFlowKind Bank,
    BankAuthenticationRuntimeKind AuthenticationRuntime,
    BankRuntimeComponent Component,
    BankRuntimeComponentState State);

public interface IBankRuntimeCompositionObserver
{
    void Record(BankRuntimeCompositionEvent observation);
}

public sealed record BankRuntimeComposition(
    BankFlowKind Bank,
    BankAuthenticationRuntimeKind AuthenticationRuntime,
    bool EnableCcbPageCoordination,
    bool EnablePsbcClientCertificateHandling)
{
    public static BankRuntimeComposition Resolve(BankFlowDefinition bankFlow)
    {
        ArgumentNullException.ThrowIfNull(bankFlow);

        var capabilities = bankFlow.BrowserCapabilities;
        var (expected, expectedCapabilities) = bankFlow.Kind switch
        {
            BankFlowKind.ChinaConstructionBank => (
                new BankRuntimeComposition(
                    bankFlow.Kind,
                    BankAuthenticationRuntimeKind.ChinaConstructionBank,
                    EnableCcbPageCoordination: true,
                    EnablePsbcClientCertificateHandling: false),
                BankBrowserCapabilities.ChinaConstructionBank),
            BankFlowKind.PostalSavingsBank => (
                new BankRuntimeComposition(
                    bankFlow.Kind,
                    BankAuthenticationRuntimeKind.PostalSavingsBank,
                    EnableCcbPageCoordination: false,
                    EnablePsbcClientCertificateHandling: true),
                BankBrowserCapabilities.PostalSavingsBank),
            _ => throw new ArgumentOutOfRangeException(
                nameof(bankFlow),
                bankFlow.Kind,
                "Unsupported bank flow.")
        };

        if (capabilities != expectedCapabilities)
        {
            throw new InvalidOperationException(
                "The selected bank has an invalid browser capability composition.");
        }

        return expected;
    }
}

public sealed class BankRuntimeComponentComposer
{
    private readonly IBankRuntimeCompositionObserver _observer;

    public BankRuntimeComponentComposer(
        BankFlowDefinition bankFlow,
        IBankRuntimeCompositionObserver? observer = null)
    {
        Composition = BankRuntimeComposition.Resolve(bankFlow);
        _observer = observer ?? CertificateLoginBankRuntimeCompositionObserver.Instance;
        Record(BankRuntimeComponent.Runtime, BankRuntimeComponentState.Selected);
    }

    public BankRuntimeComposition Composition { get; }

    public T CreateClientCertificateAuthentication<T>(
        Func<BankAuthenticationRuntimeKind, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        var component = factory(Composition.AuthenticationRuntime) ??
            throw new InvalidOperationException(
                "The authentication runtime factory returned no runtime.");
        Record(
            BankRuntimeComponent.ClientCertificateAuthentication,
            BankRuntimeComponentState.Created);
        return component;
    }

    public void RecordAuthenticationNotConfigured() =>
        Record(
            BankRuntimeComponent.ClientCertificateAuthentication,
            BankRuntimeComponentState.Skipped);

    public T? CreateCcbPageCoordinator<T>(Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!Composition.EnableCcbPageCoordination)
        {
            Record(
                BankRuntimeComponent.CcbPageCoordination,
                BankRuntimeComponentState.Skipped);
            return null;
        }

        var component = factory() ??
            throw new InvalidOperationException(
                "The CCB page coordinator factory returned no coordinator.");
        Record(
            BankRuntimeComponent.CcbPageCoordination,
            BankRuntimeComponentState.Created);
        return component;
    }

    private void Record(
        BankRuntimeComponent component,
        BankRuntimeComponentState state)
    {
        try
        {
            _observer.Record(new(
                Composition.Bank,
                Composition.AuthenticationRuntime,
                component,
                state));
        }
        catch
        {
            // Runtime classification is diagnostic and must not change behavior.
        }
    }
}

internal sealed class CertificateLoginBankRuntimeCompositionObserver :
    IBankRuntimeCompositionObserver
{
    public static CertificateLoginBankRuntimeCompositionObserver Instance { get; } =
        new();

    public void Record(BankRuntimeCompositionEvent observation) =>
        CertificateLoginDiagnostics.Write(
            "bank-runtime-component",
            new
            {
                bank = observation.Bank.ToString(),
                authenticationRuntime = observation.AuthenticationRuntime.ToString(),
                component = observation.Component.ToString(),
                state = observation.State.ToString()
            });
}
