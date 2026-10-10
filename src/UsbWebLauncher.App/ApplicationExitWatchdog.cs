using System.Diagnostics;

namespace UsbWebLauncher.App;

/// <summary>
/// Last-resort deadline independent of the dispatcher and thread pool. Arm this
/// before cancellation callbacks or native browser disposal can block the UI.
/// </summary>
internal sealed class ApplicationExitWatchdog : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private bool _armed;
    private bool _disarmed;
    private long _started;

    public ApplicationExitWatchdog(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public TimeSpan RemainingTime
    {
        get
        {
            lock (_sync)
            {
                return RemainingTimeUnderLock();
            }
        }
    }

    public void Arm()
    {
        lock (_sync)
        {
            if (_armed || _disarmed)
            {
                return;
            }

            _armed = true;
            _started = Stopwatch.GetTimestamp();
            new Thread(WaitForDeadline)
            {
                IsBackground = true,
                Name = "Application exit deadline"
            }.Start();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disarmed = true;
            Monitor.PulseAll(_sync);
        }
    }

    private TimeSpan RemainingTimeUnderLock()
    {
        var remaining = _armed ? _timeout - Stopwatch.GetElapsedTime(_started) : _timeout;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void WaitForDeadline()
    {
        lock (_sync)
        {
            while (!_disarmed)
            {
                var remaining = RemainingTimeUnderLock();
                if (remaining == TimeSpan.Zero)
                {
                    break;
                }

                Monitor.Wait(_sync, remaining);
            }

            if (_disarmed)
            {
                return;
            }
        }

        // Never enumerate or terminate WebView/vendor processes by name. This
        // affects only the launcher process that owns this watchdog instance.
        Environment.Exit(0);
    }
}
