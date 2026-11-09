using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BankCredentialTargetResolverTests
{
    [Theory]
    [InlineData(
        BankAuthenticationRuntimeKind.ChinaConstructionBank,
        "ccb-client-certificate",
        "UsbWebLauncher/ClientCertificate/ccb-client-certificate")]
    [InlineData(
        BankAuthenticationRuntimeKind.PostalSavingsBank,
        "psbc-client-certificate",
        "UsbWebLauncher/ClientCertificate/psbc-client-certificate")]
    public void Resolve_UsesBankSpecificAuthenticationPair(
        BankAuthenticationRuntimeKind runtimeKind,
        string authenticationId,
        string expectedTarget) =>
        Assert.Equal(
            expectedTarget,
            BankCredentialTargetResolver.Resolve(runtimeKind, authenticationId));

    [Theory]
    [InlineData(
        BankAuthenticationRuntimeKind.ChinaConstructionBank,
        "psbc-client-certificate")]
    [InlineData(
        BankAuthenticationRuntimeKind.PostalSavingsBank,
        "ccb-client-certificate")]
    [InlineData(
        BankAuthenticationRuntimeKind.PostalSavingsBank,
        "other-client-certificate")]
    public void Resolve_RejectsAuthenticationIdOwnedByAnotherBank(
        BankAuthenticationRuntimeKind runtimeKind,
        string authenticationId) =>
        Assert.Throws<InvalidOperationException>(
            () => BankCredentialTargetResolver.Resolve(
                runtimeKind,
                authenticationId));
}
