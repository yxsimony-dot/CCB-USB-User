using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.App.Composition;

internal sealed class BankRuntimeOwner
{
    public BankRuntimeOwner(
        BusinessSessionScope session,
        long revision,
        BankFlowKind bank)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The bank runtime revision must be positive.");
        }

        Session = session;
        SessionId = session.Id;
        Revision = revision;
        Bank = bank;
    }

    public BusinessSessionScope Session { get; }

    public long SessionId { get; }

    public long Revision { get; }

    public BankFlowKind Bank { get; }

    public bool Matches(
        BankRuntimeOwner? currentOwner,
        long currentRevision,
        BankFlowKind? currentBank) =>
        ReferenceEquals(this, currentOwner) &&
        Session.Id == SessionId &&
        Revision == currentRevision &&
        Bank == currentBank;
}

internal sealed class BankRuntimeCallbackGuard
{
    private readonly Func<BankRuntimeOwner?> _currentOwner;
    private readonly Func<long> _currentRevision;
    private readonly Func<BankFlowKind?> _currentBank;

    public BankRuntimeCallbackGuard(
        BankRuntimeOwner owner,
        Func<BankRuntimeOwner?> currentOwner,
        Func<long> currentRevision,
        Func<BankFlowKind?> currentBank)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _currentOwner = currentOwner ??
            throw new ArgumentNullException(nameof(currentOwner));
        _currentRevision = currentRevision ??
            throw new ArgumentNullException(nameof(currentRevision));
        _currentBank = currentBank ??
            throw new ArgumentNullException(nameof(currentBank));
    }

    public BankRuntimeOwner Owner { get; }

    public long CurrentSessionId => _currentOwner()?.Session.Id ?? 0;

    public long CurrentRevision => _currentRevision();

    public BankFlowKind? CurrentBank => _currentBank();

    public bool OwnsCurrentRuntime =>
        Owner.Matches(_currentOwner(), CurrentRevision, CurrentBank);

    public bool IsCurrent =>
        OwnsCurrentRuntime &&
        Owner.Session.IsCurrent;

    public bool TryPublish(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!IsCurrent)
        {
            return false;
        }

        callback();
        return true;
    }
}
