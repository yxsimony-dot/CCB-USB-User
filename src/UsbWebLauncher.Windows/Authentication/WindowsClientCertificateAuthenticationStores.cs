using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

public sealed class WindowsClientCertificateAuthenticationStores
{
    private WindowsClientCertificateAuthenticationStores(
        string credentialTargetName,
        IClientCertificatePinStore pinStore,
        IClientCertificateValidationStampStore validationStampStore)
    {
        CredentialTargetName = credentialTargetName;
        PinStore = pinStore;
        ValidationStampStore = validationStampStore;
    }

    public string CredentialTargetName { get; }

    public IClientCertificatePinStore PinStore { get; }

    public IClientCertificateValidationStampStore ValidationStampStore { get; }

    public static WindowsClientCertificateAuthenticationStores Create(
        string authenticationId,
        bool useValidationStamps = true) =>
        Create(
            authenticationId,
            new WindowsCredentialManager(new WindowsCredentialNativeApi()),
            null,
            useValidationStamps);

    internal static WindowsClientCertificateAuthenticationStores Create(
        string authenticationId,
        WindowsCredentialManager credentialManager,
        string? localApplicationDataRoot,
        bool useValidationStamps = true)
    {
        var targetName = WindowsCredentialTarget.Create(authenticationId);
        IClientCertificateValidationStampStore stampStore = useValidationStamps
            ? new WindowsValidationStampStore(
                WindowsCredentialTarget.ApplicationNamespace,
                localApplicationDataRoot)
            : new NoOpValidationStampStore();
        var pinStore = new WindowsCredentialPinStore(authenticationId, credentialManager, stampStore);
        return new(targetName, pinStore, stampStore);
    }

    private sealed class NoOpValidationStampStore : IClientCertificateValidationStampStore
    {
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(
            string authenticationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ClientCertificateValidationStamp?>(null);
        }

        public ValueTask SaveAsync(
            ClientCertificateValidationStamp stamp,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string authenticationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
