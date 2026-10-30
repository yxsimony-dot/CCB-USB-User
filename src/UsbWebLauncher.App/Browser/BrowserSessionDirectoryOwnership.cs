namespace UsbWebLauncher.App.Browser;

internal enum BrowserSessionDirectoryOwnershipState
{
    NotCreated,
    PendingEnvironment,
    WaitingForBrowserExit,
    Retired
}

internal sealed class BrowserSessionDirectoryOwnership(
    BrowserSessionDirectory directory,
    Action retire)
{
    private readonly object _sync = new();
    private bool _browserProcessExited;
    private bool _disposeRequested;
    private BrowserSessionDirectoryOwnershipState _state =
        BrowserSessionDirectoryOwnershipState.NotCreated;

    public BrowserSessionDirectoryOwnershipState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    public string EnsureCreated()
    {
        lock (_sync)
        {
            if (_state is BrowserSessionDirectoryOwnershipState.PendingEnvironment or
                BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit)
                return directory.Path;
            if (_state == BrowserSessionDirectoryOwnershipState.Retired)
                throw new ObjectDisposedException(nameof(BrowserSessionDirectoryOwnership));

            var path = directory.EnsureCreated();
            _state = BrowserSessionDirectoryOwnershipState.PendingEnvironment;
            return path;
        }
    }

    public void EnvironmentCreated()
    {
        var shouldRetire = false;
        lock (_sync)
        {
            if (_state == BrowserSessionDirectoryOwnershipState.Retired) return;
            if (_state != BrowserSessionDirectoryOwnershipState.PendingEnvironment)
                throw new InvalidOperationException("Browser environment creation was not pending.");

            _state = BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit;
            shouldRetire = _disposeRequested && _browserProcessExited;
            if (shouldRetire) _state = BrowserSessionDirectoryOwnershipState.Retired;
        }

        if (shouldRetire) retire();
    }

    public void EnvironmentCreationFailed()
    {
        var shouldRetire = false;
        lock (_sync)
        {
            if (_state == BrowserSessionDirectoryOwnershipState.PendingEnvironment)
            {
                _state = BrowserSessionDirectoryOwnershipState.Retired;
                shouldRetire = true;
            }
        }

        if (shouldRetire) retire();
    }

    public void DisposeRequested()
    {
        var shouldRetire = false;
        lock (_sync)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;

            if (_state == BrowserSessionDirectoryOwnershipState.NotCreated)
            {
                _state = BrowserSessionDirectoryOwnershipState.Retired;
            }
            else if (_state == BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit &&
                     _browserProcessExited)
            {
                _state = BrowserSessionDirectoryOwnershipState.Retired;
                shouldRetire = true;
            }
        }

        if (shouldRetire) retire();
    }

    public void BrowserProcessExited()
    {
        var shouldRetire = false;
        lock (_sync)
        {
            _browserProcessExited = true;
            if (_state == BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit &&
                _disposeRequested)
            {
                _state = BrowserSessionDirectoryOwnershipState.Retired;
                shouldRetire = true;
            }
        }

        if (shouldRetire) retire();
    }
}
