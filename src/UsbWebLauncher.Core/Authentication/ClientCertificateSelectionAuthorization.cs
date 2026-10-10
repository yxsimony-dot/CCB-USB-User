namespace UsbWebLauncher.Core.Authentication;

public sealed class ClientCertificateSelectionAuthorization
{
    private readonly object _sync = new();
    private bool _revoked;

    public bool TryCommit(Action applySelection)
    {
        ArgumentNullException.ThrowIfNull(applySelection);

        lock (_sync)
        {
            if (_revoked)
            {
                return false;
            }

            applySelection();
            return true;
        }
    }

    internal void Revoke()
    {
        lock (_sync)
        {
            _revoked = true;
        }
    }
}
