using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Authentication;

public interface IClientCertificateBusinessControl
{
    Task SetBusinessSuspendedAsync(bool suspended, CancellationToken cancellationToken = default);
}

public sealed class ClientCertificatePin : IDisposable
{
    private readonly char[] _buffer;

    private ClientCertificatePin(char[] buffer)
    {
        _buffer = buffer;
    }

    public static ClientCertificatePin CopyOf(ReadOnlyMemory<char> value) =>
        new(value.ToArray());

    public static ClientCertificatePin CopyOf(ReadOnlySpan<char> value) =>
        new(value.ToArray());

    public ReadOnlyMemory<char> AsMemory() => _buffer;

    public void Clear() => Array.Clear(_buffer, 0, _buffer.Length);

    public void Dispose() => Clear();

    public override string ToString() => "[REDACTED]";
}

public sealed record ClientCertificateValidationStamp(
    string AuthenticationId,
    string AuthenticationHash)
{
    private const string PendingPrefix = "pending:";

    public static ClientCertificateValidationStamp CreatePending(
        string authenticationId,
        string authenticationHash) =>
        new(authenticationId, $"{PendingPrefix}{authenticationHash}");

    public bool IsPendingFor(string authenticationId, string authenticationHash) =>
        string.Equals(AuthenticationId, authenticationId, StringComparison.Ordinal) &&
        string.Equals(
            AuthenticationHash,
            $"{PendingPrefix}{authenticationHash}",
            StringComparison.Ordinal);
}

public interface IClientCertificatePinStore
{
    ValueTask<ClientCertificatePin?> ReadAsync(
        string targetName,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        string targetName,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string targetName,
        CancellationToken cancellationToken = default);
}

public interface IClientCertificateValidationStampStore
{
    ValueTask<ClientCertificateValidationStamp?> ReadAsync(
        string authenticationId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ClientCertificateValidationStamp stamp,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string authenticationId,
        CancellationToken cancellationToken = default);
}

public enum ClientCertificateTokenUnlockStatus
{
    Success,
    PinRejected,
    DeviceUnavailable,
    ProviderUnavailable,
    Unsupported,
    UnknownFailure
}

public enum ClientCertificateTokenUnlockFailureStage
{
    PinEncoding,
    LoadProviderModule,
    InitializeProviderModule,
    EnumerateToken,
    OpenTokenSession,
    VerifyPin,
    AcquireProvider,
    SetPin,
    GetUserKey,
    CreateHash,
    HashData,
    QuerySignatureLength,
    SignHash,
    Unexpected
}

public interface IClientCertificateTokenUnlockSession : IDisposable
{
}

public sealed record ClientCertificateTokenUnlockResult(
    ClientCertificateTokenUnlockStatus Status,
    IClientCertificateTokenUnlockSession? Session = null,
    int? ProviderErrorCode = null)
{
    public ClientCertificateTokenUnlockFailureStage? FailureStage { get; init; }

    public static ClientCertificateTokenUnlockResult Succeeded(
        IClientCertificateTokenUnlockSession session) =>
        new(ClientCertificateTokenUnlockStatus.Success, session);

    public static ClientCertificateTokenUnlockResult Failed(
        ClientCertificateTokenUnlockStatus status,
        int? providerErrorCode = null) =>
        new(status, null, providerErrorCode);

    public static ClientCertificateTokenUnlockResult Failed(
        ClientCertificateTokenUnlockStatus status,
        int? providerErrorCode,
        ClientCertificateTokenUnlockFailureStage failureStage) =>
        new(status, null, providerErrorCode) { FailureStage = failureStage };
}

public interface IClientCertificateTokenUnlocker
{
    ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
        ClientCertificateAuthenticationOptions authentication,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);
}

public enum ClientCertificateNativePromptArmStatus
{
    Armed,
    BrokerUnavailable,
    ProtocolMismatch,
    PeerIdentityMismatch,
    PolicyMismatch,
    Replayed,
    TimedOut,
    Cancelled,
    UnknownFailure
}

public sealed record ClientCertificateNativePromptArmResult(
    ClientCertificateNativePromptArmStatus Status,
    IClientCertificateNativePromptAttempt? Attempt = null,
    int? PublicErrorCode = null)
{
    public bool IsArmed =>
        Status == ClientCertificateNativePromptArmStatus.Armed && Attempt is not null;
}

public enum ClientCertificateNativePromptStatus
{
    Success,
    TargetMismatch,
    FocusLost,
    InputFailed,
    BrokerUnavailable,
    ProtocolMismatch,
    PeerIdentityMismatch,
    PolicyMismatch,
    Replayed,
    TimedOut,
    Cancelled,
    UnknownFailure
}

public sealed record ClientCertificateNativePromptResult(
    ClientCertificateNativePromptStatus Status,
    int? PublicErrorCode = null)
{
    public bool IsSuccess => Status == ClientCertificateNativePromptStatus.Success;
}

public interface IClientCertificateNativePromptContinuation
{
    ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
        ClientCertificateAuthenticationOptions authentication,
        AuthenticationAttempt attempt,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);
}

public interface IClientCertificateNativePromptAttempt : IAsyncDisposable
{
    Task<ClientCertificateNativePromptResult> Completion { get; }
}

public enum ClientCertificateSetupValidationStatus
{
    Success,
    PinRejected,
    CertificateRejected,
    TlsFailed,
    TimedOut,
    Cancelled,
    Incompatible,
    UnknownFailure
}

public sealed record ClientCertificateSetupValidationResult(
    ClientCertificateSetupValidationStatus Status,
    int? PublicErrorCode = null)
{
    public ClientCertificateTokenUnlockFailureStage? TokenUnlockFailureStage { get; init; }

    public bool IsSuccess => Status == ClientCertificateSetupValidationStatus.Success;

    public static ClientCertificateSetupValidationResult Succeeded() =>
        new(ClientCertificateSetupValidationStatus.Success);
}

public interface IClientCertificateSetupVerifier
{
    ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
        ClientCertificateAuthenticationOptions authentication,
        IClientCertificateTokenUnlockSession session,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);
}

public interface IBrowserClientCertificateRequestHandler
{
    ValueTask<ClientCertificateRequestMatchResult> HandleAsync(
        ClientCertificateRequestSnapshot request,
        CancellationToken cancellationToken = default);
}

public interface IBrowserClientCertificateAuthenticationReporter
{
    Task ReportNavigationStartedAsync(
        long browserOperationId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task<ClientCertificateSelectionAuthorization?> ReportCertificateSelectedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default);

    Task ReportTlsCompletedAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default);

    Task ReportTlsFailureAsync(
        AuthenticationAttempt attempt,
        int? publicErrorCode = null,
        CancellationToken cancellationToken = default);

    Task ReportTimeoutAsync(
        AuthenticationAttempt attempt,
        CancellationToken cancellationToken = default);

    Task ReportNavigationCompletedWithoutCertificateAsync(
        long browserOperationId,
        bool isSuccess,
        CancellationToken cancellationToken = default);
}
