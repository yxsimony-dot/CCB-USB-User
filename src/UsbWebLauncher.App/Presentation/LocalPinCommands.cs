using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Presentation;

public interface ILocalPinDialog
{
    Task<ClientCertificatePin?> ShowAsync(CancellationToken cancellationToken = default);
}

public sealed record NativePromptBrokerPrerequisiteState(
    bool IsRequired,
    bool IsHealthy,
    bool IsInstalled,
    string StatusText)
{
    public static NativePromptBrokerPrerequisiteState NotRequired { get; } = new(
        false,
        true,
        false,
        string.Empty);
}

public interface INativePromptBrokerPrerequisites
{
    event EventHandler? StateChanged;

    NativePromptBrokerPrerequisiteState CurrentState { get; }

    Task<bool> InstallAsync(CancellationToken cancellationToken = default);

    Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(CancellationToken cancellationToken = default);
}

public interface INativePromptBrokerCommands
{
    event EventHandler? StateChanged;

    NativePromptBrokerPrerequisiteState CurrentState { get; }

    Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default);

    Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalPinCommands : ILocalPinCommands, INativePromptBrokerCommands
{
    private readonly Func<ILocalPinDialog> _dialogFactory;
    private readonly IClientCertificatePinSetupService _setupService;
    private readonly INativePromptBrokerPrerequisites _prerequisites;
    private string? _operationStatus;

    public LocalPinCommands(
        ILocalPinDialog dialog,
        IClientCertificatePinSetupService setupService,
        INativePromptBrokerPrerequisites? prerequisites = null)
        : this(() => dialog, setupService, prerequisites)
    {
    }

    public LocalPinCommands(
        Func<ILocalPinDialog> dialogFactory,
        IClientCertificatePinSetupService setupService,
        INativePromptBrokerPrerequisites? prerequisites = null)
    {
        _dialogFactory = dialogFactory ?? throw new ArgumentNullException(nameof(dialogFactory));
        _setupService = setupService ?? throw new ArgumentNullException(nameof(setupService));
        _prerequisites = prerequisites ?? NullNativePromptBrokerPrerequisites.Instance;
        _prerequisites.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StateChanged;

    public NativePromptBrokerPrerequisiteState CurrentState => _operationStatus is { } status
        ? _prerequisites.CurrentState with { StatusText = status }
        : _prerequisites.CurrentState;

    public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync(cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        RunSetupAsync(cancellationToken);

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        SetOperationStatus(null);
        await _setupService.DeleteAsync(cancellationToken).ConfigureAwait(true);
        SetOperationStatus("本机保存的 PIN 已删除，请重新设置 PIN。");
    }

    public async Task InstallNativePromptBrokerAsync(
        CancellationToken cancellationToken = default)
    {
        SetOperationStatus(null);
        if (!_prerequisites.CurrentState.IsRequired ||
            !await _prerequisites.InstallAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        _ = await _prerequisites.RefreshHealthAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task UninstallNativePromptBrokerAsync(
        CancellationToken cancellationToken = default)
    {
        SetOperationStatus(null);
        if (_prerequisites.CurrentState.IsRequired)
        {
            _ = await _prerequisites.UninstallAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task RunSetupAsync(CancellationToken cancellationToken)
    {
        SetOperationStatus(null);
        if (_prerequisites.CurrentState.IsRequired &&
            !await _prerequisites.RefreshHealthAsync(cancellationToken).ConfigureAwait(true))
        {
            SetOperationStatus("请先点击安装环境，环境就绪后再设置 PIN。");
            return;
        }

        var pin = await _dialogFactory().ShowAsync(cancellationToken).ConfigureAwait(true);
        if (pin is null)
        {
            return;
        }

        try
        {
            var saved = await _setupService.SaveFixedAsync(pin, cancellationToken).ConfigureAwait(true);
            if (saved)
            {
                SetOperationStatus("PIN 已保存，请点击启动业务。");
            }
        }
        finally
        {
            pin.Dispose();
        }
    }

    private void SetOperationStatus(string? status)
    {
        if (_operationStatus == status)
        {
            return;
        }

        _operationStatus = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class NullNativePromptBrokerPrerequisites : INativePromptBrokerPrerequisites
{
    public static NullNativePromptBrokerPrerequisites Instance { get; } = new();

    public event EventHandler? StateChanged { add { } remove { } }

    public NativePromptBrokerPrerequisiteState CurrentState =>
        NativePromptBrokerPrerequisiteState.NotRequired;

    public Task<bool> InstallAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> UninstallAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
