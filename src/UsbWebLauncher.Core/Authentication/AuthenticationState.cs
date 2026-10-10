namespace UsbWebLauncher.Core.Authentication;

public enum AuthenticationStage
{
    WaitingForDevice,
    RequiresPinSetup,
    ValidatingCompatibility,
    UnlockingToken,
    AwaitingCertificate,
    Authenticating,
    Ready,
    Incompatible,
    Failed
}

public enum AuthenticationErrorCategory
{
    None,
    CredentialsUnavailable,
    ValidationStampInvalid,
    PinRejected,
    DeviceUnavailable,
    ProviderUnavailable,
    Unsupported,
    UnknownFailure,
    InvalidCertificateRequest,
    UnauthorizedTarget,
    CertificateMissing,
    CertificateConflict,
    TlsFailed,
    TimedOut
}

public sealed record AuthenticationAttempt(
    long InsertionCycleId,
    long CertificateRequestId,
    long? BrowserOperationId = null);

public sealed record AuthenticationState(
    AuthenticationStage Stage,
    long? InsertionCycleId,
    AuthenticationErrorCategory ErrorCategory = AuthenticationErrorCategory.None,
    int? PublicErrorCode = null,
    string? Message = null,
    AuthenticationAttempt? Attempt = null);

public sealed class AuthenticationStateChangedEventArgs(AuthenticationState state) : EventArgs
{
    public AuthenticationState State { get; } = state;
}
