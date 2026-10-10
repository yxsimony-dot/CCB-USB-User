using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.App.Composition;

public interface IManualAuthenticationControl
{
    event EventHandler? StateChanged;
    bool RequiresExplicitStart { get; }
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task AllowBusinessAsync(CancellationToken cancellationToken = default);
}

public sealed class ManualAuthenticationGate
    : ILauncherAuthenticationGate, ILauncherAuthenticationMetadataInvalidationGate,
      IClientCertificatePinSetupLifecycleObserver, IManualAuthenticationControl
{
    private readonly ILauncherAuthenticationGate _innerGate;
    private readonly IClientCertificatePinSetupLifecycleObserver _observer;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private volatile bool _requiresExplicitStart = true;
    private PendingSetupChange _pendingChange;

    public ManualAuthenticationGate(
        ILauncherAuthenticationGate innerGate,
        IClientCertificatePinSetupLifecycleObserver observer)
    {
        _innerGate = innerGate ?? throw new ArgumentNullException(nameof(innerGate));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public event EventHandler? StateChanged;

    public bool RequiresExplicitStart => _requiresExplicitStart;

    public Task InvalidateDeviceMetadataAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync(async () =>
        {
            _requiresExplicitStart = true;
            if (_innerGate is not ILauncherAuthenticationMetadataInvalidationGate invalidationGate)
            {
                throw new InvalidOperationException("Authentication cannot safely invalidate device metadata. Reinsert the USB Key.");
            }

            await invalidationGate.InvalidateDeviceMetadataAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync(async () =>
        {
            _requiresExplicitStart = true;
            await SetBrowserSuspendedAsync(true, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task AllowBusinessAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync(() =>
        {
            // The launcher supplies current presence through its existing start/resume path.
            // Replaying a previously withheld presence here could unlock a removed device.
            _requiresExplicitStart = false;
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<bool> HandleDevicePresenceAsync(
        bool isTargetDevicePresent,
        CancellationToken cancellationToken = default)
    {
        var allowed = false;
        await TransitionAsync(async () =>
        {
            if (!isTargetDevicePresent)
            {
                // Actual removal and shutdown must still release the core's resources.
                allowed = await _innerGate.HandleDevicePresenceAsync(false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (_requiresExplicitStart)
            {
                return;
            }

            try
            {
                var change = _pendingChange;
                _pendingChange = PendingSetupChange.None;
                // Consume before awaiting: a failure must not replay a notification that
                // may already have reset the core's single-attempt protection.
                if (change == PendingSetupChange.Provisioned)
                {
                    await _observer.ReportPinProvisionedAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (change == PendingSetupChange.Succeeded)
                {
                    await _observer.ReportPinSetupSucceededAsync(cancellationToken).ConfigureAwait(false);
                }

                // Applying a saved credential may itself unlock an already-present device.
                // The original gate preserves its current cycle on this repeated true.
                await SetBrowserSuspendedAsync(false, cancellationToken).ConfigureAwait(false);
                allowed = await _innerGate.HandleDevicePresenceAsync(true, cancellationToken)
                    .ConfigureAwait(false);
                if (!allowed)
                {
                    _requiresExplicitStart = true;
                }
            }
            catch
            {
                _requiresExplicitStart = true;
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        return allowed;
    }

    public Task ReportPinProvisionedAsync(CancellationToken cancellationToken = default) =>
        DeferSetupChangeAsync(PendingSetupChange.Provisioned, cancellationToken);

    public Task ReportPinSetupSucceededAsync(CancellationToken cancellationToken = default) =>
        DeferSetupChangeAsync(PendingSetupChange.Succeeded, cancellationToken);

    public Task ReportPinSetupDeletedAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync(async () =>
        {
            _requiresExplicitStart = true;
            _pendingChange = PendingSetupChange.None;
            await _observer.ReportPinSetupDeletedAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private Task DeferSetupChangeAsync(PendingSetupChange change, CancellationToken cancellationToken) =>
        TransitionAsync(async () =>
        {
            _pendingChange = change;
            _requiresExplicitStart = true;
            await SetBrowserSuspendedAsync(true, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private Task SetBrowserSuspendedAsync(bool suspended, CancellationToken cancellationToken) =>
        _observer is IClientCertificateBusinessControl control
            ? control.SetBusinessSuspendedAsync(suspended, cancellationToken)
            : Task.CompletedTask;

    private async Task TransitionAsync(Func<Task> transition, CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requiredBefore = _requiresExplicitStart;
        try
        {
            await transition().ConfigureAwait(false);
        }
        finally
        {
            var changed = requiredBefore != _requiresExplicitStart;
            _transitionGate.Release();
            if (changed)
            {
                PublishStateChanged();
            }
        }
    }

    private void PublishStateChanged()
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch
            {
                // A presentation subscriber must not alter authentication state or cleanup.
            }
        }
    }

    private enum PendingSetupChange
    {
        None,
        Provisioned,
        Succeeded
    }
}
