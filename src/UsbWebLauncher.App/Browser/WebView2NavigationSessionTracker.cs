namespace UsbWebLauncher.App.Browser;

internal sealed class WebView2NavigationSessionTracker : IDisposable
{
    private readonly object _sync = new();
    private Session? _active;
    private bool _disposed;

    public void BeginNavigation()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
            {
                throw new InvalidOperationException("A physical WebView2 navigation is already active.");
            }

            _active = new Session(Task.CompletedTask);
        }
    }

    public bool TryBindNavigationId(long navigationId)
    {
        lock (_sync)
        {
            if (_disposed || _active is null || _active.IsTerminal)
            {
                return false;
            }

            if (_active.NavigationId == navigationId)
            {
                return true;
            }

            if (_active.NavigationId is not null)
            {
                return false;
            }

            _active.NavigationId = navigationId;
            return true;
        }
    }

    public bool TryBeginUntrackedNavigation(
        long navigationId,
        Task authenticationBindingTask)
    {
        ArgumentNullException.ThrowIfNull(authenticationBindingTask);

        lock (_sync)
        {
            if (_disposed || _active is not null)
            {
                return false;
            }

            _active = new Session(authenticationBindingTask)
            {
                NavigationId = navigationId
            };
            return true;
        }
    }

    public bool TryEnterCertificateRequest(
        out IDisposable? lease,
        out Task authenticationBindingTask)
    {
        lock (_sync)
        {
            lease = null;
            authenticationBindingTask = Task.FromException(
                new InvalidOperationException("There is no active browser navigation."));
            if (_disposed || _active is null || _active.IsTerminal)
            {
                return false;
            }

            authenticationBindingTask = _active.AuthenticationBindingTask;
            _active.InflightCertificateRequests++;
            lease = new CertificateRequestLease(this, _active);
            return true;
        }
    }

    public Task<bool> CompleteNavigationAsync(long navigationId)
    {
        TaskCompletionSource<bool>? completed = null;
        Task<bool> result;
        lock (_sync)
        {
            if (_disposed || _active is null ||
                _active.NavigationId != navigationId)
            {
                return Task.FromResult(false);
            }

            _active.IsTerminal = true;
            result = _active.Terminal.Task;
            if (_active.InflightCertificateRequests == 0)
            {
                completed = _active.Terminal;
                _active = null;
            }
        }

        completed?.TrySetResult(true);
        return result;
    }

    public void AbortNavigation()
    {
        TaskCompletionSource<bool>? terminal;
        lock (_sync)
        {
            terminal = _active?.Terminal;
            _active = null;
        }

        terminal?.TrySetResult(false);
    }

    public void Dispose()
    {
        TaskCompletionSource<bool>? terminal;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            terminal = _active?.Terminal;
            _active = null;
        }

        terminal?.TrySetResult(false);
    }

    private void ExitCertificateRequest(Session session)
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_sync)
        {
            if (session.InflightCertificateRequests > 0)
            {
                session.InflightCertificateRequests--;
            }

            if (!_disposed && ReferenceEquals(_active, session) && session.IsTerminal &&
                session.InflightCertificateRequests == 0)
            {
                completed = session.Terminal;
                _active = null;
            }
        }

        completed?.TrySetResult(true);
    }

    private sealed class Session
    {
        public Session(Task authenticationBindingTask)
        {
            AuthenticationBindingTask = authenticationBindingTask;
        }

        public Task AuthenticationBindingTask { get; }

        public long? NavigationId { get; set; }
        public int InflightCertificateRequests { get; set; }
        public bool IsTerminal { get; set; }
        public TaskCompletionSource<bool> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CertificateRequestLease(
        WebView2NavigationSessionTracker owner,
        Session session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitCertificateRequest(session);
            }
        }
    }
}
