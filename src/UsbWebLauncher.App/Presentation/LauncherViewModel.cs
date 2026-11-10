using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Presentation;

public enum PinCredentialStatus
{
    NotSet,
    Set,
    ReadFailed
}

public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private BusinessSessionState _sessionState;
    private Func<CancellationToken, Task>? _stopBusiness;
    private readonly object _handoffGate = new();
    private bool _businessStartRunning;
    private bool _updateInstallInProgress;
    private BankFlowDefinition _selectedBankFlow = BankFlowCatalog.ChinaConstructionBank;
    private bool _bankFlowSwitching;
    private string? _bankFlowUnavailableMessage;

    public bool UpdateInstallInProgress => _updateInstallInProgress;
    private bool IsIdleForUpdate => _initialized && _sessionState == BusinessSessionState.Idle &&
        !_started && !IsBusy && !_businessStartRunning;
    public bool CanInstallUpdate => IsIdleForUpdate && !_updateInstallInProgress;
    public bool CanContinueUpdateInstall => IsIdleForUpdate && _updateInstallInProgress;

    public IReadOnlyList<BankFlowDefinition> BankFlows => BankFlowCatalog.All;

    public BankFlowDefinition SelectedBankFlow
    {
        get => _selectedBankFlow;
        set
        {
            if (value is null || value == _selectedBankFlow)
            {
                return;
            }

            if (!CanChangeBank)
            {
                RaisePropertyChanged();
                return;
            }

            var previous = _selectedBankFlow;
            _selectedBankFlow = value;
            RaisePropertyChanged();
            BankFlowChangeRequested?.Invoke(this, new(previous, value));
        }
    }

    public bool IsBankFlowSwitching => _bankFlowSwitching;

    public bool CanChangeBank => !_bankFlowSwitching &&
        _sessionState == BusinessSessionState.Idle &&
        !IsBusy &&
        !_businessStartRunning &&
        !_updateInstallInProgress;

    public bool TryBeginUpdateInstall()
    {
        lock (_handoffGate)
        {
            if (!CanInstallUpdate) return false;
            _updateInstallInProgress = true;
        }
        PublishCommandAvailability();
        return true;
    }

    public void EndUpdateInstall()
    {
        lock (_handoffGate) _updateInstallInProgress = false;
        PublishCommandAvailability();
    }

    private bool TryBeginBusinessStart()
    {
        lock (_handoffGate)
        {
            if (!CanStartBusiness || _businessStartRunning) return false;
            _businessStartRunning = true;
            return true;
        }
    }

    private void EndBusinessStart()
    {
        lock (_handoffGate) _businessStartRunning = false;
        RaisePropertyChanged(nameof(CanInstallUpdate));
        RaisePropertyChanged(nameof(CanChangeBank));
    }

    public BusinessSessionState SessionState => _sessionState;
    public string BusinessButtonText => _sessionState switch
    {
        BusinessSessionState.Active => "中止业务",
        BusinessSessionState.Stopping => "正在中止…",
        BusinessSessionState.StopFailed => "中止失败",
        BusinessSessionState.Closing => "正在关闭…",
        _ => "启动业务"
    };
    public ICommand BusinessButtonCommand => _sessionState == BusinessSessionState.Idle
        ? StartBusinessCommand : StopBusinessCommand;
    public AsyncDelegateCommand StopBusinessCommand { get; }
    public bool CanStopBusiness => _stopBusiness is not null && _sessionState == BusinessSessionState.Active;

    public void ConfigureStopBusinessCommand(Func<CancellationToken, Task> stop)
    {
        _stopBusiness = stop ?? throw new ArgumentNullException(nameof(stop));
        PublishCommandAvailability();
    }

    public void UpdateSessionState(BusinessSessionState state)
    {
        if (_sessionState == BusinessSessionState.Closing || _sessionState == state) return;
        _sessionState = state;
        RaisePropertyChanged(nameof(SessionState));
        PublishBusinessState();
    }

    public void ResetSessionPresentation()
    {
        _state = new(LauncherStage.WaitingForUsb, false, "业务未启动，请点击“启动业务”。");
        _authenticationState = new(AuthenticationStage.WaitingForDevice, null);
        _started = false;
        _awaitingExplicitStart = false;
        _operationBusy = false;
        _businessMessage = null;
        _exportBusy = false;
        Volatile.Write(ref _exportCommandRunning, 0);
        _exportStatusText = string.Empty;
        UpdateCurrentCompany("启动业务并登录后显示");
        foreach (var property in new[] { nameof(Stage), nameof(AuthenticationStage), nameof(IsAuthenticationBusy),
            nameof(IsUsbPresent), nameof(IsBrowserVisible), nameof(IsExportBusy), nameof(ExportStatusText) })
            RaisePropertyChanged(property);
        PublishBusinessState();
    }

    private bool TryBeginStopCommand()
    {
        if (!CanStopBusiness) return false;
        UpdateSessionState(BusinessSessionState.Stopping);
        return true;
    }

    private bool SessionAllowsOperations => _stopBusiness is null ||
        _sessionState is BusinessSessionState.Idle or BusinessSessionState.Active;
    private bool CanMaintain => CanOperate && (_stopBusiness is null || _sessionState == BusinessSessionState.Idle);

    private string? SessionStatusText => _sessionState switch
    {
        BusinessSessionState.Stopping => "正在中止业务并关闭本轮页面…",
        BusinessSessionState.StopFailed => "中止业务未完成，请关闭程序后重试。",
        BusinessSessionState.Closing => "正在关闭程序…",
        _ => null
    };
    private ILocalPinCommands _pinCommands;
    private INativePromptBrokerCommands _brokerCommands = NullNativePromptBrokerCommands.Instance;
    private LauncherState _state = new(
        LauncherStage.WaitingForUsb,
        false,
        "正在等待指定 USB Key…");

    private AuthenticationState _authenticationState = new(
        AuthenticationStage.WaitingForDevice,
        null,
        Message: "正在等待安全设备。");
    private bool _isAuthenticationConfigured;
    private bool _initialized;
    private bool _started;
    private bool _awaitingExplicitStart;
    private bool _operationBusy;
    private string? _businessMessage;
    private Func<CancellationToken, Task>? _startBusiness;
    private int _selectedExportNumber = 1;
    private Func<int, CancellationToken, Task>? _exportDocuments;
    private int _exportCommandRunning;
    private int _activeExportMonth;
    private bool _exportBusy;
    private int _maintenanceCommandRunning;
    private string _exportStatusText = string.Empty;
    private string _currentPinText = "当前 PIN：未设置";
    private string _currentCompanyText = "启动业务并登录后显示";

    private UsbKeyDetectionStatus? _usbKeyDetectionStatus;
    private bool _keyDetectionCompleted;
    private bool ShowIdleKeyHint => !_started && _sessionState == BusinessSessionState.Idle;
    private bool IsPostalSavingsBank =>
        SelectedBankFlow.Kind == BankFlowKind.PostalSavingsBank;
    public string CompanyCardTitle => ShowIdleKeyHint ? "USB Key 状态" : "当前公司";
    public string CurrentCompanyText => _bankFlowSwitching
        ? $"正在切换到{SelectedBankFlow.DisplayName}…"
        : _bankFlowUnavailableMessage is not null
            ? $"{SelectedBankFlow.DisplayName} Key 识别待配置"
        : ShowIdleKeyHint
        ? !_keyDetectionCompleted ? "正在检测 USB Key…"
            : GetIdleUsbKeyHint()
        : _currentCompanyText;

    public void UpdateUsbKeyPresence(bool? present)
    {
        UpdateUsbKeyDetectionStatus(present switch
        {
            true => UsbKeyDetectionStatus.Ready,
            false => UsbKeyDetectionStatus.Absent,
            null => null
        });
    }

    public void UpdateUsbKeyDetectionStatus(UsbKeyDetectionStatus? status)
    {
        if (_bankFlowUnavailableMessage is not null)
        {
            return;
        }

        _usbKeyDetectionStatus = status;
        _keyDetectionCompleted = true;
        RaisePropertyChanged(nameof(CurrentCompanyText));
        RaisePropertyChanged(nameof(DeviceStatusText));
    }

    private string GetIdleUsbKeyHint()
    {
        if (IsPostalSavingsBank)
        {
            return _usbKeyDetectionStatus switch
            {
                UsbKeyDetectionStatus.Ready =>
                    "可用邮储 Key 已就绪，点击启动业务开始",
                UsbKeyDetectionStatus.PresentButEnvironmentUnavailable =>
                    "邮储 Key 已连接，但驱动/服务环境不可用",
                UsbKeyDetectionStatus.Absent =>
                    "未检测到受支持的邮储 Key，请插入",
                null => "邮储 Key 检测失败，请重新插拔或重启程序",
                _ => "邮储 Key 检测失败，请重新插拔或重启程序"
            };
        }

        return _usbKeyDetectionStatus switch
        {
            UsbKeyDetectionStatus.Ready =>
                "已检测到可用 USB Key，点击启动业务开始",
            UsbKeyDetectionStatus.Absent =>
                "未检测到可用 USB Key，请插入",
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable =>
                "USB Key 已连接，但运行环境不可用",
            null => "USB Key 检测失败，请重新插拔或重启程序",
            _ => "USB Key 检测失败，请重新插拔或重启程序"
        };
    }

    public void UpdateCurrentCompany(string text)
    {
        if (_currentCompanyText == text) return;
        _currentCompanyText = text;
        RaisePropertyChanged(nameof(CurrentCompanyText));
    }

    public LauncherViewModel()
        : this(NullLocalPinCommands.Instance)
    {
    }

    public LauncherViewModel(ILocalPinCommands pinCommands)
    {
        _pinCommands = pinCommands ?? throw new ArgumentNullException(nameof(pinCommands));
        ValidateAndSavePinCommand = new AsyncDelegateCommand(
            cancellationToken => _pinCommands.ValidateAndSaveAsync(cancellationToken),
            () => CanValidateAndSavePin,
            TryBeginMaintenanceCommand,
            EndMaintenanceCommand);
        ResetPinCommand = new AsyncDelegateCommand(
            cancellationToken => _pinCommands.ResetAsync(cancellationToken),
            () => CanResetPin,
            TryBeginMaintenanceCommand,
            EndMaintenanceCommand);
        DeletePinCommand = new AsyncDelegateCommand(
            cancellationToken => _pinCommands.DeleteAsync(cancellationToken),
            () => CanDeletePin,
            TryBeginMaintenanceCommand,
            EndMaintenanceCommand);
        InstallNativePromptBrokerCommand = new AsyncDelegateCommand(
            cancellationToken => _brokerCommands.InstallNativePromptBrokerAsync(cancellationToken),
            () => CanInstallNativePromptBroker,
            TryBeginMaintenanceCommand,
            EndMaintenanceCommand);
        UninstallNativePromptBrokerCommand = new AsyncDelegateCommand(
            cancellationToken => _brokerCommands.UninstallNativePromptBrokerAsync(cancellationToken),
            () => CanUninstallNativePromptBroker,
            TryBeginMaintenanceCommand,
            EndMaintenanceCommand);
        StartBusinessCommand = new AsyncDelegateCommand(
            cancellationToken => _startBusiness!(cancellationToken),
            () => CanStartBusiness, TryBeginBusinessStart, EndBusinessStart);
        StopBusinessCommand = new AsyncDelegateCommand(
            cancellationToken => _stopBusiness!(cancellationToken),
            () => CanStopBusiness, TryBeginStopCommand);
        ExportDocumentsCommand = new AsyncDelegateCommand(
            token => ExecuteExportAsync(_exportDocuments!, token),
            () => CanExportDocuments, TryBeginExportCommand, EndExportCommand);
        if (pinCommands is not NullLocalPinCommands)
        {
            ConfigureLocalPinCommands(pinCommands);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<BankFlowChangedEventArgs>? BankFlowChangeRequested;

    public IReadOnlyList<int> ExportNumbers { get; } = Array.AsReadOnly(Enumerable.Range(1, 12).ToArray());

    public int SelectedExportNumber
    {
        get => _selectedExportNumber;
        set
        {
            if (value is < 1 or > 12 || value == _selectedExportNumber)
            {
                return;
            }

            _selectedExportNumber = value;
            RaisePropertyChanged();
        }
    }

    public LauncherStage Stage => _state.Stage;

    private bool HasAuthenticationFailure => _started && _authenticationState.Stage is
        AuthenticationStage.Failed or AuthenticationStage.Incompatible;

    private bool HasPsbcConnectionFailure => IsPostalSavingsBank &&
        _authenticationState.ErrorCategory == AuthenticationErrorCategory.TlsFailed;

    private string AuthenticationFailureMessage => HasPsbcConnectionFailure
        ? "银行安全连接未完成，请查看下方页面错误码。"
        : !string.IsNullOrWhiteSpace(_authenticationState.Message)
        ? _authenticationState.Message
        : "本次认证未完成，请检查 USB Key 后重新插入。";

    public string StatusText => SessionStatusText ??
        (_bankFlowSwitching ? $"正在切换到{SelectedBankFlow.DisplayName}流程…" :
        _bankFlowUnavailableMessage ??
        (HasAuthenticationFailure ? AuthenticationFailureMessage : _businessMessage ??
        (!_initialized ? (_state.CanRetry ? _state.Message : "正在初始化…") :
        !_started ? "业务未启动，请点击“启动业务”。" :
        _awaitingExplicitStart ? "准备完成后，请点击“启动业务”继续。" : _state.Message)));

    public string DeviceStatusText => _bankFlowSwitching ? "正在切换银行" :
        _bankFlowUnavailableMessage is not null ? $"{SelectedBankFlow.DisplayName}待配置" :
        !_started ? !_keyDetectionCompleted ? "正在检测 USB Key…" :
        GetIdleDeviceStatusText() :
        IsUsbPresent ? "USB Key 已连接" : "等待 USB Key";

    private string GetIdleDeviceStatusText()
    {
        if (IsPostalSavingsBank)
        {
            return _usbKeyDetectionStatus switch
            {
                UsbKeyDetectionStatus.Ready => "可用邮储 Key 已就绪",
                UsbKeyDetectionStatus.PresentButEnvironmentUnavailable =>
                    "邮储 Key 驱动/服务环境不可用",
                UsbKeyDetectionStatus.Absent => "等待邮储 Key",
                null => "邮储 Key 检测失败",
                _ => "邮储 Key 检测失败"
            };
        }

        return _usbKeyDetectionStatus switch
        {
            UsbKeyDetectionStatus.Ready => "USB Key 已连接",
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable =>
                "USB Key 环境不可用",
            UsbKeyDetectionStatus.Absent => "等待 USB Key",
            null => "USB Key 检测失败",
            _ => "USB Key 检测失败"
        };
    }

    public string IdleTitle => _bankFlowSwitching ? "正在切换银行流程" :
        _bankFlowUnavailableMessage is not null ? $"{SelectedBankFlow.DisplayName}流程待配置" :
        HasAuthenticationFailure ? "本次认证未完成" :
        !_initialized ? (_state.CanRetry ? "初始化未完成" : "正在初始化") :
        !_started ? "点击“启动业务”开始" :
        _awaitingExplicitStart ? "等待您继续业务" : "请插入已配置的 USB Key";

    public string IdleDescription => _bankFlowSwitching
        ? "正在停止当前银行流程并清理相关状态。"
        : _bankFlowUnavailableMessage is not null
            ? $"请先配置{SelectedBankFlow.DisplayName} Key 的识别信息、登录地址和认证参数。"
        : HasAuthenticationFailure
        ? HasPsbcConnectionFailure
            ? "此错误不等同于 PIN 错误，请勿反复提交。"
            : "请检查设备和 PIN 设置，再拔出并重新插入 USB Key。"
        : !_initialized ? "请等待初始化完成；如遇错误，可点击重试。" :
        !_started ? "可先完成所需准备，再点击上方按钮启动业务。" :
        _awaitingExplicitStart ? "完成所需准备后，再次点击“启动业务”。" :
        "识别成功后，目标页面会在此窗口自动打开。";

    public string? ErrorDetail => _state.ErrorDetail ??
        (HasAuthenticationFailure ? AuthenticationFailureMessage : null);

    public bool IsUsbPresent => _state.IsUsbPresent;

    public bool IsBrowserVisible => _state.IsBrowserVisible;

    public bool CanRetry => _state.CanRetry && !IsBusy && !_updateInstallInProgress;

    public bool CanStartBusiness => CanOperate && _startBusiness is not null &&
        (_stopBusiness is null || _sessionState == BusinessSessionState.Idle) &&
        (!_started || _awaitingExplicitStart || _state.CanRetry);

    public bool IsBusy => _operationBusy ||
        IsAuthenticationBusy ||
        IsExportBusy ||
        Volatile.Read(ref _maintenanceCommandRunning) != 0;

    public bool CanExportDocuments => CanOperate && _exportDocuments is not null &&
        (_stopBusiness is null || _sessionState == BusinessSessionState.Active);

    public bool IsExportBusy => Volatile.Read(ref _exportCommandRunning) != 0 || _exportBusy;

    public string ExportStatusText => _exportStatusText;

    public string CurrentPinText => _currentPinText;

    private bool CanOperate => _initialized &&
        !_bankFlowSwitching &&
        _bankFlowUnavailableMessage is null &&
        !IsBusy &&
        SessionAllowsOperations &&
        !_updateInstallInProgress;

    public AuthenticationStage AuthenticationStage => _authenticationState.Stage;

    public string AuthenticationStatusText => _bankFlowUnavailableMessage is not null
        ? $"PIN 设置不适用：{SelectedBankFlow.DisplayName}认证流程尚未配置。"
        : !_isAuthenticationConfigured
        ? "PIN 设置不适用：当前配置未启用认证。"
        : !_started ? "可提前设置 PIN；保存后请手动启动业务。"
        : HasPsbcConnectionFailure ? AuthenticationFailureMessage
        : _authenticationState.Message ?? string.Empty;

    public bool IsAuthenticationBusy => _authenticationState.Stage is
        AuthenticationStage.ValidatingCompatibility or
        AuthenticationStage.UnlockingToken or
        AuthenticationStage.AwaitingCertificate or
        AuthenticationStage.Authenticating;

    public bool CanValidateAndSavePin => CanMaintain && _isAuthenticationConfigured &&
        (!_started || _authenticationState.Stage is AuthenticationStage.WaitingForDevice or
        AuthenticationStage.RequiresPinSetup or
        AuthenticationStage.Incompatible or
        AuthenticationStage.Failed);

    public bool CanResetPin => CanMaintain && _isAuthenticationConfigured &&
        (!_started || _authenticationState.Stage is AuthenticationStage.WaitingForDevice or
        AuthenticationStage.RequiresPinSetup or
        AuthenticationStage.Ready or
        AuthenticationStage.Incompatible or
        AuthenticationStage.Failed);

    public bool CanDeletePin => CanMaintain && _isAuthenticationConfigured;

    public bool IsAuthenticationConfigured => _isAuthenticationConfigured;

    public bool CanInstallNativePromptBroker =>
        CanMaintain && _isAuthenticationConfigured && _brokerCommands.CurrentState.IsRequired;

    public bool IsNativePromptBrokerInstallVisible =>
        CanInstallNativePromptBroker &&
        !_brokerCommands.CurrentState.IsHealthy;

    public bool CanUninstallNativePromptBroker =>
        CanMaintain && _isAuthenticationConfigured &&
        _brokerCommands.CurrentState is { IsRequired: true, IsInstalled: true };

    public string NativePromptBrokerStatusText => _bankFlowUnavailableMessage is not null
        ? $"环境安装不适用：{SelectedBankFlow.DisplayName}认证流程尚未配置。"
        : !_isAuthenticationConfigured
        ? "环境安装不适用：当前配置未启用认证。"
        : !_brokerCommands.CurrentState.IsRequired ? "当前认证方式无需安装环境。"
        : _brokerCommands.CurrentState.StatusText;

    public AsyncDelegateCommand StartBusinessCommand { get; }

    public AsyncDelegateCommand ExportDocumentsCommand { get; }

    public AsyncDelegateCommand ValidateAndSavePinCommand { get; }

    public AsyncDelegateCommand ResetPinCommand { get; }

    public AsyncDelegateCommand DeletePinCommand { get; }

    public AsyncDelegateCommand InstallNativePromptBrokerCommand { get; }

    public AsyncDelegateCommand UninstallNativePromptBrokerCommand { get; }

    public void UpdateState(LauncherState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        RaisePropertyChanged(nameof(Stage));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(ErrorDetail));
        RaisePropertyChanged(nameof(IsUsbPresent));
        RaisePropertyChanged(nameof(IsBrowserVisible));
        RaisePropertyChanged(nameof(CanRetry));
        PublishBusinessState();
    }

    public void UpdateAuthenticationState(AuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _authenticationState = state;
        RaisePropertyChanged(nameof(AuthenticationStage));
        RaisePropertyChanged(nameof(IsAuthenticationBusy));
        PublishBusinessState();
    }

    public void ConfigureBusinessCommand(Func<CancellationToken, Task> start)
    {
        _startBusiness = start ?? throw new ArgumentNullException(nameof(start));
        PublishCommandAvailability();
    }

    public void ConfigureDocumentsExportCommand(Func<int, CancellationToken, Task> export)
    {
        _exportDocuments = export ?? throw new ArgumentNullException(nameof(export));
        PublishCommandAvailability();
    }

    public void UpdateExportState(bool busy, string? message)
    {
        _exportBusy = busy;
        _exportStatusText = message ?? string.Empty;
        RaisePropertyChanged(nameof(IsExportBusy));
        RaisePropertyChanged(nameof(ExportStatusText));
        PublishCommandAvailability();
    }

    public void UpdateCurrentPin(ClientCertificatePin? pin)
    {
        if (pin is null || pin.AsMemory().IsEmpty)
        {
            UpdateCurrentPinStatus(PinCredentialStatus.NotSet);
            return;
        }

        _currentPinText = "当前 PIN：" + new string(pin.AsMemory().Span);
        RaisePropertyChanged(nameof(CurrentPinText));
    }

    public void UpdateCurrentPinStatus(PinCredentialStatus status)
    {
        _currentPinText = status switch
        {
            PinCredentialStatus.NotSet => "当前 PIN：未设置",
            PinCredentialStatus.Set => "当前 PIN：已设置",
            PinCredentialStatus.ReadFailed => "当前 PIN：读取失败",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
        RaisePropertyChanged(nameof(CurrentPinText));
    }

    public void BeginBankFlowSwitch()
    {
        _bankFlowSwitching = true;
        _bankFlowUnavailableMessage = null;
        ClearBankRuntimeBindings();
        _state = new(LauncherStage.WaitingForUsb, false, "正在切换银行流程。");
        _authenticationState = new(
            AuthenticationStage.WaitingForDevice,
            null,
            Message: "正在等待银行流程初始化。");
        _sessionState = BusinessSessionState.Idle;
        _initialized = false;
        _started = false;
        _awaitingExplicitStart = true;
        _operationBusy = true;
        _businessMessage = null;
        _businessStartRunning = false;
        _usbKeyDetectionStatus = null;
        _keyDetectionCompleted = false;
        _selectedExportNumber = 1;
        _exportBusy = false;
        Volatile.Write(ref _exportCommandRunning, 0);
        Volatile.Write(ref _maintenanceCommandRunning, 0);
        _exportStatusText = string.Empty;
        _currentPinText = "当前 PIN：未设置";
        _currentCompanyText = "启动业务并登录后显示";
        PublishBankFlowState();
    }

    public void CompleteBankFlowSwitch(string? unavailableMessage = null)
    {
        _bankFlowSwitching = false;
        _bankFlowUnavailableMessage = string.IsNullOrWhiteSpace(unavailableMessage)
            ? null
            : unavailableMessage;
        if (_bankFlowUnavailableMessage is not null)
        {
            _initialized = true;
            _operationBusy = false;
            _awaitingExplicitStart = true;
        }
        PublishBankFlowState();
    }

    private void ClearBankRuntimeBindings()
    {
        _brokerCommands.StateChanged -= OnNativePromptBrokerStateChanged;
        _pinCommands = NullLocalPinCommands.Instance;
        _brokerCommands = NullNativePromptBrokerCommands.Instance;
        _isAuthenticationConfigured = false;
        _startBusiness = null;
        _stopBusiness = null;
        _exportDocuments = null;
    }

    private void PublishBankFlowState()
    {
        foreach (var property in new[]
        {
            nameof(IsBankFlowSwitching),
            nameof(CanChangeBank),
            nameof(SessionState),
            nameof(Stage),
            nameof(AuthenticationStage),
            nameof(IsAuthenticationBusy),
            nameof(IsUsbPresent),
            nameof(IsBrowserVisible),
            nameof(IsExportBusy),
            nameof(ExportStatusText),
            nameof(CurrentPinText),
            nameof(SelectedExportNumber),
            nameof(IsAuthenticationConfigured)
        })
        {
            RaisePropertyChanged(property);
        }
        PublishBusinessState();
    }

    private bool TryBeginExportCommand()
    {
        if (!CanOperate || Interlocked.CompareExchange(ref _exportCommandRunning, 1, 0) != 0)
            return false;
        _activeExportMonth = SelectedExportNumber;
        return true;
    }

    private bool TryBeginMaintenanceCommand()
    {
        if (!CanMaintain ||
            Interlocked.CompareExchange(ref _maintenanceCommandRunning, 1, 0) != 0)
        {
            return false;
        }

        PublishCommandAvailability();
        return true;
    }

    private void EndMaintenanceCommand()
    {
        Volatile.Write(ref _maintenanceCommandRunning, 0);
        PublishCommandAvailability();
    }

    private void EndExportCommand()
    {
        Volatile.Write(ref _exportCommandRunning, 0);
        UpdateExportState(false, _exportStatusText);
    }

    private Task ExecuteExportAsync(Func<int, CancellationToken, Task> export, CancellationToken cancellationToken)
    {
        var month = _activeExportMonth;
        UpdateExportState(true, null);
        return export(month, cancellationToken);
    }

    public void UpdateBusinessState(
        bool initialized,
        bool started,
        bool awaitingExplicitStart,
        bool operationBusy,
        string? message = null)
    {
        _initialized = initialized;
        _started = started;
        _awaitingExplicitStart = awaitingExplicitStart;
        _operationBusy = operationBusy;
        _businessMessage = message;
        PublishBusinessState();
    }

    private void PublishBusinessState()
    {
        RaisePropertyChanged(nameof(CurrentCompanyText));
        RaisePropertyChanged(nameof(CompanyCardTitle));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(DeviceStatusText));
        RaisePropertyChanged(nameof(IdleTitle));
        RaisePropertyChanged(nameof(IdleDescription));
        RaisePropertyChanged(nameof(ErrorDetail));
        RaisePropertyChanged(nameof(AuthenticationStatusText));
        PublishCommandAvailability();
    }

    private void PublishCommandAvailability()
    {
        RaisePropertyChanged(nameof(CanInstallUpdate));
        RaisePropertyChanged(nameof(UpdateInstallInProgress));
        RaisePropertyChanged(nameof(IsBusy));
        RaisePropertyChanged(nameof(CanRetry));
        RaisePropertyChanged(nameof(CanChangeBank));
        RaisePropertyChanged(nameof(CanStartBusiness));
        RaisePropertyChanged(nameof(CanStopBusiness));
        RaisePropertyChanged(nameof(BusinessButtonText));
        RaisePropertyChanged(nameof(BusinessButtonCommand));
        RaisePropertyChanged(nameof(CanExportDocuments));
        RaisePropertyChanged(nameof(CanValidateAndSavePin));
        RaisePropertyChanged(nameof(CanResetPin));
        RaisePropertyChanged(nameof(CanDeletePin));
        ValidateAndSavePinCommand.NotifyCanExecuteChanged();
        ResetPinCommand.NotifyCanExecuteChanged();
        DeletePinCommand.NotifyCanExecuteChanged();
        StartBusinessCommand.NotifyCanExecuteChanged();
        StopBusinessCommand.NotifyCanExecuteChanged();
        ExportDocumentsCommand.NotifyCanExecuteChanged();
        PublishNativePromptBrokerState();
    }

    public void ConfigureLocalPinCommands(ILocalPinCommands pinCommands)
    {
        _brokerCommands.StateChanged -= OnNativePromptBrokerStateChanged;
        _pinCommands = pinCommands ?? throw new ArgumentNullException(nameof(pinCommands));
        _brokerCommands = pinCommands as INativePromptBrokerCommands ??
            NullNativePromptBrokerCommands.Instance;
        _brokerCommands.StateChanged += OnNativePromptBrokerStateChanged;
        _isAuthenticationConfigured = true;
        RaisePropertyChanged(nameof(IsAuthenticationConfigured));
        RaisePropertyChanged(nameof(AuthenticationStatusText));
        PublishCommandAvailability();
    }

    private void OnNativePromptBrokerStateChanged(object? sender, EventArgs eventArgs) =>
        PublishNativePromptBrokerState();

    private void PublishNativePromptBrokerState()
    {
        RaisePropertyChanged(nameof(CanInstallNativePromptBroker));
        RaisePropertyChanged(nameof(IsNativePromptBrokerInstallVisible));
        RaisePropertyChanged(nameof(CanUninstallNativePromptBroker));
        RaisePropertyChanged(nameof(NativePromptBrokerStatusText));
        InstallNativePromptBrokerCommand.NotifyCanExecuteChanged();
        UninstallNativePromptBrokerCommand.NotifyCanExecuteChanged();
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public interface ILocalPinCommands
{
    Task ValidateAndSaveAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public sealed class AsyncDelegateCommand(
    Func<CancellationToken, Task> execute,
    Func<bool> canExecute,
    Func<bool>? tryBeginExecution = null,
    Action? endExecution = null) : ICommand
{
    private int _executing;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _executing) == 0 && canExecute();

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!CanExecute(null) || Interlocked.Exchange(ref _executing, 1) != 0)
        {
            return;
        }

        var entered = false;
        try
        {
            // A shared operation gate must be owned before command notifications can
            // reenter another command that uses the same browser workflow.
            if (tryBeginExecution is not null && !tryBeginExecution()) return;
            entered = true;
            NotifyCanExecuteChanged();
            await execute(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            try { if (entered) endExecution?.Invoke(); }
            finally
            {
                Volatile.Write(ref _executing, 0);
                NotifyCanExecuteChanged();
            }
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class NullLocalPinCommands : ILocalPinCommands
{
    public static NullLocalPinCommands Instance { get; } = new();
    public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NullNativePromptBrokerCommands : INativePromptBrokerCommands
{
    public static NullNativePromptBrokerCommands Instance { get; } = new();
    public event EventHandler? StateChanged { add { } remove { } }
    public NativePromptBrokerPrerequisiteState CurrentState =>
        NativePromptBrokerPrerequisiteState.NotRequired;
    public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
