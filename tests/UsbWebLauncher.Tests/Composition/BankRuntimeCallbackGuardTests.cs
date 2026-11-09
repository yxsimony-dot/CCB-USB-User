using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BankRuntimeCallbackGuardTests
{
    [Fact]
    public async Task SwitchingFromPsbcToCcbRejectsDelayedStateAndPinCallbacks()
    {
        using var psbcSession = new BusinessSessionScope(CancellationToken.None);
        using var ccbSession = new BusinessSessionScope(CancellationToken.None);
        var revision = 1L;
        BankFlowKind? currentBank = BankFlowKind.PostalSavingsBank;
        BankRuntimeOwner? currentOwner = new(
            psbcSession,
            revision,
            BankFlowKind.PostalSavingsBank);
        var psbcGuard = CreateGuard(
            currentOwner,
            () => currentOwner,
            () => revision,
            () => currentBank);

        await psbcSession.RevokeAsync();
        revision++;
        currentBank = BankFlowKind.ChinaConstructionBank;
        currentOwner = new(
            ccbSession,
            revision,
            BankFlowKind.ChinaConstructionBank);

        var constructionBankStateChanges = 0;
        var pinSubmissions = 0;

        Assert.False(psbcGuard.TryPublish(
            () => constructionBankStateChanges++));
        Assert.False(psbcGuard.TryPublish(
            () => pinSubmissions++));
        Assert.Equal(0, constructionBankStateChanges);
        Assert.Equal(0, pinSubmissions);
    }

    [Fact]
    public void RevisionInvalidationRejectsCallbacksBeforeCleanupCompletes()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var revision = 7L;
        BankFlowKind? currentBank = BankFlowKind.PostalSavingsBank;
        BankRuntimeOwner? currentOwner = new(
            session,
            revision,
            BankFlowKind.PostalSavingsBank);
        var guard = CreateGuard(
            currentOwner,
            () => currentOwner,
            () => revision,
            () => currentBank);

        revision++;

        Assert.True(session.IsCurrent);
        Assert.False(guard.OwnsCurrentRuntime);
        Assert.False(guard.IsCurrent);
    }

    [Fact]
    public async Task SessionRevocationRejectsCallbacksWithUnchangedOwnerAndBank()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var revision = 3L;
        BankFlowKind? currentBank = BankFlowKind.PostalSavingsBank;
        BankRuntimeOwner? currentOwner = new(
            session,
            revision,
            BankFlowKind.PostalSavingsBank);
        var guard = CreateGuard(
            currentOwner,
            () => currentOwner,
            () => revision,
            () => currentBank);

        await session.RevokeAsync();

        Assert.True(guard.OwnsCurrentRuntime);
        Assert.False(guard.IsCurrent);
        Assert.False(guard.TryPublish(
            () => throw new InvalidOperationException("must not publish")));
    }

    [Fact]
    public void BankIdentityMismatchRejectsOtherwiseMatchingOwner()
    {
        using var session = new BusinessSessionScope(CancellationToken.None);
        var revision = 4L;
        BankFlowKind? currentBank = BankFlowKind.ChinaConstructionBank;
        BankRuntimeOwner? currentOwner = new(
            session,
            revision,
            BankFlowKind.PostalSavingsBank);
        var guard = CreateGuard(
            currentOwner,
            () => currentOwner,
            () => revision,
            () => currentBank);

        Assert.False(guard.OwnsCurrentRuntime);
        Assert.False(guard.IsCurrent);
    }

    private static BankRuntimeCallbackGuard CreateGuard(
        BankRuntimeOwner owner,
        Func<BankRuntimeOwner?> currentOwner,
        Func<long> currentRevision,
        Func<BankFlowKind?> currentBank) =>
        new(owner, currentOwner, currentRevision, currentBank);
}
