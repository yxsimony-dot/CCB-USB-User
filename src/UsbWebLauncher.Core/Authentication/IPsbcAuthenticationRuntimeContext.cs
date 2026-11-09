using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Core.Authentication;

public sealed record PsbcAuthenticationRuntimeSnapshot(
    long OwnerSessionId,
    long OwnerRevision,
    long CurrentSessionId,
    long CurrentRevision,
    bool IsPostalSavingsBank,
    bool IsSessionCurrent,
    bool OwnsCurrentRuntime,
    UsbKeyDetectionStatus DetectionStatus,
    UsbKeyDetectionResult? ReadyKey,
    long ContextRevision)
{
    public bool IsValid =>
        OwnerSessionId > 0 &&
        OwnerRevision > 0 &&
        OwnerSessionId == CurrentSessionId &&
        OwnerRevision == CurrentRevision &&
        IsPostalSavingsBank &&
        IsSessionCurrent &&
        OwnsCurrentRuntime &&
        ReadyKey is
        {
            Status: UsbKeyDetectionStatus.Ready,
            VendorFamily: not null
        } &&
        !string.IsNullOrWhiteSpace(ReadyKey.PhysicalInstanceId) &&
        !string.IsNullOrWhiteSpace(ReadyKey.TokenIdentity);
}

public interface IPsbcAuthenticationRuntimeContext
{
    event EventHandler? Changed;

    PsbcAuthenticationRuntimeSnapshot Current { get; }
}
