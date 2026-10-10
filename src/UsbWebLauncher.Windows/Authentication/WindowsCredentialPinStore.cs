using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

internal sealed class WindowsCredentialPinStore : IClientCertificatePinStore
{
    private readonly WindowsCredentialManager _credentialManager;
    private readonly IClientCertificateValidationStampStore _stampStore;
    private readonly string _authenticationId;
    private readonly string _targetName;

    public WindowsCredentialPinStore(
        string authenticationId,
        WindowsCredentialManager credentialManager,
        IClientCertificateValidationStampStore stampStore)
    {
        _authenticationId = authenticationId ?? throw new ArgumentNullException(nameof(authenticationId));
        _targetName = WindowsCredentialTarget.Create(authenticationId);
        _credentialManager = credentialManager ?? throw new ArgumentNullException(nameof(credentialManager));
        _stampStore = stampStore ?? throw new ArgumentNullException(nameof(stampStore));
    }

    internal string TargetName => _targetName;

    public ValueTask<ClientCertificatePin?> ReadAsync(
        string targetName,
        CancellationToken cancellationToken = default) =>
        _credentialManager.ReadAsync(RequireCanonicalTarget(targetName), cancellationToken);

    public async ValueTask SaveAsync(
        string targetName,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        var canonicalTarget = RequireCanonicalTarget(targetName);
        await _stampStore.DeleteAsync(_authenticationId, cancellationToken).ConfigureAwait(false);
        await _credentialManager.WriteAsync(canonicalTarget, pin, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(
        string targetName,
        CancellationToken cancellationToken = default)
    {
        var canonicalTarget = RequireCanonicalTarget(targetName);
        await _stampStore.DeleteAsync(_authenticationId, cancellationToken).ConfigureAwait(false);
        await _credentialManager.DeleteAsync(canonicalTarget, cancellationToken).ConfigureAwait(false);
    }

    private string RequireCanonicalTarget(string targetName)
    {
        if (!string.Equals(targetName, _targetName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential target is not valid for this authentication configuration.", nameof(targetName));
        }

        return _targetName;
    }
}
