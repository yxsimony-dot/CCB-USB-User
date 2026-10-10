using UsbWebLauncher.Core.Browser;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Core.Coordination;

public sealed class LauncherCoordinator : IAsyncDisposable
{
    private readonly IUsbDeviceMonitor _deviceMonitor;
    private readonly IBrowserHost _browserHost;
    private readonly LauncherProfile _profile;
    private readonly ILauncherAuthenticationGate? _authenticationGate;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateSync = new();
    private IReadOnlyList<UsbDeviceDescriptor> _lastDevices = [];
    private readonly HashSet<string> _matchedPhysicalInstanceIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _deviceMetadataInvalidated;
    private FailedOperation _failedOperation;
    private Exception? _monitoringFault;
    private bool _started;
    private bool _monitorStarted;
    private bool _browserInitialized;
    private bool _browserOpening;
    private bool _navigationReady;
    private bool _authenticationNavigationInvalidated;
    private CancellationTokenSource? _activeNavigationCancellation;
    private bool _disposed;

    public LauncherCoordinator(
        IUsbDeviceMonitor deviceMonitor,
        IBrowserHost browserHost,
        LauncherProfile profile,
        ILauncherAuthenticationGate? authenticationGate = null)
    {
        _deviceMonitor = deviceMonitor ?? throw new ArgumentNullException(nameof(deviceMonitor));
        _browserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _authenticationGate = authenticationGate;
    }

    public event EventHandler<LauncherStateChangedEventArgs>? StateChanged;

    public LauncherState CurrentState { get; private set; } = new(
        LauncherStage.WaitingForUsb,
        false,
        "正在等待指定 USB Key…");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _deviceMonitor.DevicesChanged += OnDevicesChangedAsync;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);

        if (!await StartMonitoringSafelyAsync(linkedCancellation.Token).ConfigureAwait(false))
        {
            return;
        }

        await RefreshDevicesSafelyAsync(linkedCancellation.Token).ConfigureAwait(false);
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        FailedOperation failedOperation;
        bool monitorStarted;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CurrentState.Stage != LauncherStage.Error)
            {
                return;
            }

            failedOperation = _failedOperation;
            monitorStarted = _monitorStarted;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);

        if (failedOperation == FailedOperation.Monitoring || !monitorStarted)
        {
            if (!await StartMonitoringSafelyAsync(linkedCancellation.Token).ConfigureAwait(false))
            {
                return;
            }

            await RefreshDevicesSafelyAsync(linkedCancellation.Token).ConfigureAwait(false);
            return;
        }

        if (failedOperation == FailedOperation.Enumeration)
        {
            await RefreshDevicesSafelyAsync(linkedCancellation.Token).ConfigureAwait(false);
            return;
        }

        await HandleDevicesAsync(GetLastDevices(), linkedCancellation.Token).ConfigureAwait(false);
    }

    public void ReportExternalNavigationReady()
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_deviceMetadataInvalidated)
            {
                SetMetadataInvalidatedStateCore();
                return;
            }
            _browserInitialized = true;
            _browserOpening = false;
            var stillPresent = ContainsMatchingDevice(_lastDevices);
            _navigationReady = stillPresent || _authenticationGate is null;
            SetOperationalStateCore(_navigationReady
                ? new(
                    LauncherStage.Ready,
                    stillPresent,
                    stillPresent ? "页面已就绪。" : "页面已就绪；USB Key 已拔出。")
                : new(
                    LauncherStage.WaitingForUsb,
                    false,
                    "正在等待指定 USB Key…"));
        }
    }

    public async Task ResumeNavigationAfterAuthenticationSetupAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                return;
            }
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await HandleDevicesAsync(GetLastDevices(), linkedCancellation.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _deviceMonitor.DevicesChanged -= OnDevicesChangedAsync;
        await _deviceMonitor.DisposeAsync().ConfigureAwait(false);

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        _transitionGate.Release();
        _transitionGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<bool> StartMonitoringSafelyAsync(CancellationToken cancellationToken)
    {
        Exception? faultBeforeStart;
        lock (_stateSync)
        {
            faultBeforeStart = _monitoringFault;
        }

        try
        {
            await _deviceMonitor.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                if (!ReferenceEquals(_monitoringFault, faultBeforeStart))
                {
                    _monitorStarted = false;
                    return false;
                }

                _monitoringFault = null;
                _monitorStarted = true;
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFailure(FailedOperation.Monitoring, exception);
            return false;
        }
    }

    private async Task OnDevicesChangedAsync(
        object? sender,
        UsbDevicesChangedEventArgs eventArgs)
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            if (eventArgs.Error is not null)
            {
                _monitorStarted = false;
                _monitoringFault = eventArgs.Error;
                SetFailureCore(FailedOperation.Monitoring, eventArgs.Error);
                return;
            }

            _lastDevices = eventArgs.Devices;
        }

        try
        {
            await HandleDevicesAsync(eventArgs.Devices, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshDevicesSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _deviceMonitor
                .GetConnectedDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (_stateSync)
            {
                _lastDevices = devices;
            }

            await HandleDevicesAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFailure(FailedOperation.Enumeration, exception);
        }
    }

    private async Task HandleDevicesAsync(
        IReadOnlyList<UsbDeviceDescriptor> devices,
        CancellationToken cancellationToken)
    {
        var matchingDevicePresent = ContainsMatchingDevice(devices);
        var physicalDeviceRemoved = false;
        var metadataInvalidated = false;
        if (_profile.UsbDetectionStrategy == "ccbUsbStor" && _authenticationGate is not null)
        {
            lock (_stateSync)
            {
                var currentIds = devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var previouslyPresent = _matchedPhysicalInstanceIds.Count > 0;
                _matchedPhysicalInstanceIds.IntersectWith(currentIds);
                physicalDeviceRemoved = previouslyPresent && _matchedPhysicalInstanceIds.Count == 0;
                if (physicalDeviceRemoved)
                {
                    _deviceMetadataInvalidated = false;
                }

                foreach (var device in devices.Where(UsbDeviceMatcher.IsCcbUsbStorMatch))
                {
                    _matchedPhysicalInstanceIds.Add(device.InstanceId);
                }

                if (!matchingDevicePresent && _matchedPhysicalInstanceIds.Count > 0)
                {
                    _deviceMetadataInvalidated = true;
                }

                metadataInvalidated = _deviceMetadataInvalidated;
            }
        }
        CancellationTokenSource? navigationToCancel = null;
        if (_authenticationGate is not null && !matchingDevicePresent)
        {
            lock (_stateSync)
            {
                if (_browserOpening)
                {
                    _authenticationNavigationInvalidated = true;
                    navigationToCancel = _activeNavigationCancellation;
                }
            }

            try
            {
                navigationToCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The navigation completed between observing it and requesting cancellation.
            }
        }

        var waitForInvalidatedNavigation = false;
        if (_authenticationGate is not null && matchingDevicePresent)
        {
            lock (_stateSync)
            {
                waitForInvalidatedNavigation =
                    _browserOpening && _authenticationNavigationInvalidated;
            }
        }

        if (waitForInvalidatedNavigation)
        {
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _transitionGate.Release();
            await HandleDevicesAsync(GetLastDevices(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (matchingDevicePresent &&
            _browserHost is IBrowserHostAvailability { IsAvailable: false })
        {
            SetFailure(
                FailedOperation.Initialization,
                new InvalidOperationException(
                    "浏览器已在无法安全停止旧导航后关闭；请重新启动应用。"));
            return;
        }

        var authenticationAllowsNavigation = true;
        if (_authenticationGate is not null)
        {
            try
            {
                if (metadataInvalidated)
                {
                    if (_authenticationGate is not ILauncherAuthenticationMetadataInvalidationGate invalidationGate)
                    {
                        lock (_stateSync)
                        {
                            _navigationReady = false;
                        }
                        throw new InvalidOperationException("无法安全释放认证资源；设备信息不可用，请重新插拔 USB Key。");
                    }

                    await invalidationGate.InvalidateDeviceMetadataAsync(cancellationToken).ConfigureAwait(false);
                    lock (_stateSync)
                    {
                        SetMetadataInvalidatedStateCore();
                    }
                    return;
                }

                if (physicalDeviceRemoved && matchingDevicePresent)
                {
                    await _authenticationGate.HandleDevicePresenceAsync(false, cancellationToken).ConfigureAwait(false);
                }
                authenticationAllowsNavigation = await _authenticationGate
                    .HandleDevicePresenceAsync(matchingDevicePresent, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetFailure(FailedOperation.Authentication, exception);
                return;
            }
        }

        lock (_stateSync)
        {
            if (_monitoringFault is not null)
            {
                SetFailureCore(FailedOperation.Monitoring, _monitoringFault);
                return;
            }

            if (_deviceMetadataInvalidated)
            {
                SetMetadataInvalidatedStateCore();
                return;
            }

            if (!matchingDevicePresent)
            {
                if (_authenticationGate is not null && _browserOpening)
                {
                    _authenticationNavigationInvalidated = true;
                }

                if (_authenticationGate is not null && !_browserOpening)
                {
                    _navigationReady = false;
                    SetOperationalStateCore(new(
                        LauncherStage.WaitingForUsb,
                        false,
                        "正在等待指定 USB Key…"));
                    return;
                }

                if (_navigationReady)
                {
                    SetOperationalStateCore(new(
                        LauncherStage.Ready,
                        false,
                        "页面已就绪；USB Key 已拔出。"));
                }
                else if (_browserOpening)
                {
                    SetOperationalStateCore(CurrentState with
                    {
                        IsUsbPresent = false,
                        Message = "正在打开页面；USB Key 已拔出。"
                    });
                }
                else
                {
                    SetOperationalStateCore(new(
                        LauncherStage.WaitingForUsb,
                        false,
                        "正在等待指定 USB Key…"));
                }

                return;
            }

            if (_navigationReady)
            {
                SetOperationalStateCore(new(
                    LauncherStage.Ready,
                    true,
                    "页面已就绪。"));
                return;
            }

            if (!authenticationAllowsNavigation)
            {
                SetOperationalStateCore(new(
                    LauncherStage.UsbDetected,
                    true,
                    "已识别 USB Key；等待本机认证设置。"));
                return;
            }

            if (_browserOpening)
            {
                if (_authenticationGate is not null && _authenticationNavigationInvalidated)
                {
                    waitForInvalidatedNavigation = true;
                }

                if (!CurrentState.IsUsbPresent)
                {
                    SetOperationalStateCore(CurrentState with { IsUsbPresent = true });
                }

                if (!waitForInvalidatedNavigation)
                {
                    return;
                }
            }
        }

        if (waitForInvalidatedNavigation)
        {
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _transitionGate.Release();
            await HandleDevicesAsync(GetLastDevices(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? navigationCancellation = null;
        try
        {
            navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateSync)
            {
                if (_monitoringFault is not null)
                {
                    SetFailureCore(FailedOperation.Monitoring, _monitoringFault);
                    return;
                }

                if (!ContainsMatchingDevice(_lastDevices))
                {
                    _navigationReady = false;
                    SetOperationalStateCore(new(
                        LauncherStage.WaitingForUsb,
                        false,
                        "正在等待指定 USB Key…"));
                    return;
                }

                if (_deviceMetadataInvalidated)
                {
                    SetMetadataInvalidatedStateCore();
                    return;
                }

                if (_navigationReady || _browserOpening)
                {
                    return;
                }

                _browserOpening = true;
                _authenticationNavigationInvalidated = false;
                _activeNavigationCancellation = navigationCancellation;
                SetOperationalStateCore(new(
                    LauncherStage.UsbDetected,
                    true,
                    "已识别 USB Key。"));
                SetOperationalStateCore(new(
                    LauncherStage.OpeningBrowser,
                    true,
                    "正在打开页面…"));
            }

            bool browserInitialized;
            lock (_stateSync)
            {
                browserInitialized = _browserInitialized;
            }

            if (!browserInitialized)
            {
                try
                {
                    await _browserHost.InitializeAsync(navigationCancellation.Token).ConfigureAwait(false);
                    lock (_stateSync)
                    {
                        _browserInitialized = true;
                    }
                }
                catch (OperationCanceledException) when (
                    navigationCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    SetInvalidatedAuthenticationNavigationState();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (!SetInvalidatedAuthenticationNavigationState())
                    {
                        await ReleaseAuthenticationAfterBrowserFailureAsync().ConfigureAwait(false);
                        SetFailure(FailedOperation.Initialization, exception);
                    }

                    return;
                }
            }

            try
            {
                await _browserHost
                    .NavigateAsync(new Uri(_profile.TargetUrl), navigationCancellation.Token)
                    .ConfigureAwait(false);
                lock (_stateSync)
                {
                    if (_deviceMetadataInvalidated)
                    {
                        SetMetadataInvalidatedStateCore();
                        return;
                    }
                    var stillPresent = ContainsMatchingDevice(_lastDevices);
                    if (_authenticationGate is not null && _authenticationNavigationInvalidated)
                    {
                        _navigationReady = false;
                        SetOperationalStateCore(stillPresent
                            ? new(
                                LauncherStage.UsbDetected,
                                true,
                                "已识别 USB Key；正在重新开始认证导航。")
                            : new(
                                LauncherStage.WaitingForUsb,
                                false,
                                "正在等待指定 USB Key…"));
                    }
                    else
                    {
                        _navigationReady = stillPresent || _authenticationGate is null;
                        SetOperationalStateCore(_navigationReady
                            ? new(
                                LauncherStage.Ready,
                                stillPresent,
                                stillPresent
                                    ? "页面已就绪。"
                                    : "页面已就绪；USB Key 已拔出。")
                            : new(
                                LauncherStage.WaitingForUsb,
                                false,
                                "正在等待指定 USB Key…"));
                    }
                }
            }
            catch (OperationCanceledException) when (
                navigationCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                SetInvalidatedAuthenticationNavigationState();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!SetInvalidatedAuthenticationNavigationState())
                {
                    await ReleaseAuthenticationAfterBrowserFailureAsync().ConfigureAwait(false);
                    SetFailure(FailedOperation.Navigation, exception);
                }
            }
        }
        finally
        {
            lock (_stateSync)
            {
                if (ReferenceEquals(_activeNavigationCancellation, navigationCancellation))
                {
                    _activeNavigationCancellation = null;
                }

                _browserOpening = false;
            }

            navigationCancellation?.Dispose();
            _transitionGate.Release();
        }
    }

    private bool SetInvalidatedAuthenticationNavigationState()
    {
        lock (_stateSync)
        {
            if (_authenticationGate is null || !_authenticationNavigationInvalidated)
            {
                return false;
            }

            _navigationReady = false;
            if (_deviceMetadataInvalidated)
            {
                SetMetadataInvalidatedStateCore();
                return true;
            }
            var stillPresent = ContainsMatchingDevice(_lastDevices);
            SetOperationalStateCore(stillPresent
                ? new(
                    LauncherStage.UsbDetected,
                    true,
                    "已识别 USB Key；正在重新开始认证导航。")
                : new(
                    LauncherStage.WaitingForUsb,
                    false,
                    "正在等待指定 USB Key…"));
            return true;
        }
    }

    private async Task ReleaseAuthenticationAfterBrowserFailureAsync()
    {
        if (_authenticationGate is null)
        {
            return;
        }

        try
        {
            await _authenticationGate
                .HandleDevicePresenceAsync(false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the browser failure as the public failure while ensuring
            // authentication cleanup is best effort and never exposes secrets.
        }
    }

    private IReadOnlyList<UsbDeviceDescriptor> GetLastDevices()
    {
        lock (_stateSync)
        {
            return _lastDevices;
        }
    }

    private void SetMetadataInvalidatedStateCore()
    {
        _navigationReady = false;
        SetOperationalStateCore(new(
            LauncherStage.UsbDetected,
            ContainsMatchingDevice(_lastDevices),
            "设备信息不可用；请重新插拔 USB Key 后继续。"));
    }

    private bool ContainsMatchingDevice(IEnumerable<UsbDeviceDescriptor> devices) =>
        devices.Any(device => UsbDeviceMatcher.IsMatch(device, _profile));

    private void SetFailure(FailedOperation operation, Exception exception)
    {
        lock (_stateSync)
        {
            SetFailureCore(operation, exception);
        }
    }

    private void SetFailureCore(FailedOperation operation, Exception exception)
    {
        if (operation == FailedOperation.Monitoring)
        {
            _monitoringFault = exception;
        }
        else if (_monitoringFault is not null)
        {
            operation = FailedOperation.Monitoring;
            exception = _monitoringFault;
        }

        _failedOperation = operation;
        SetStateCore(new(
            LauncherStage.Error,
            ContainsMatchingDevice(_lastDevices),
            "操作失败，可以重试。",
            exception.Message));
    }

    private void SetOperationalStateCore(LauncherState state)
    {
        if (_monitoringFault is not null)
        {
            SetFailureCore(FailedOperation.Monitoring, _monitoringFault);
            return;
        }

        _failedOperation = FailedOperation.None;
        SetStateCore(state);
    }

    private void SetStateCore(LauncherState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new(state));
    }

    private enum FailedOperation
    {
        None,
        Monitoring,
        Enumeration,
        Initialization,
        Navigation,
        Authentication
    }
}

public interface ILauncherAuthenticationGate
{
    Task<bool> HandleDevicePresenceAsync(
        bool isTargetDevicePresent,
        CancellationToken cancellationToken = default);
}

public interface ILauncherAuthenticationMetadataInvalidationGate
{
    Task InvalidateDeviceMetadataAsync(CancellationToken cancellationToken = default);
}

public sealed class LauncherStateChangedEventArgs(LauncherState state) : EventArgs
{
    public LauncherState State { get; } = state;
}
