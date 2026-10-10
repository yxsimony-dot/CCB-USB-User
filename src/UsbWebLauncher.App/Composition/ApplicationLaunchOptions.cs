using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

internal sealed record ApplicationLaunchOptions(bool UseEmptyDeviceMonitor)
{
    public InstallerCommand InstallerCommand { get; init; }
    public bool IsHeadless => InstallerCommand is not InstallerCommand.None;
    public bool ShowDependencyPreparation { get; init; }
    internal const string SmokeTestNoDevicesArgument = "--smoke-test-no-devices";
    internal const string SmokeReadyWindowTitle = "USB Web Launcher [Smoke Ready]";

    public static ApplicationLaunchOptions Parse(IEnumerable<string>? arguments)
    {
        var args = arguments?.ToArray() ?? [];
        if (args.Any(value => value.StartsWith("--installer-", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length != 1) return new(false) { InstallerCommand = InstallerCommand.Invalid };
            if (args[0] == "--installer-first-run") return new(false) { ShowDependencyPreparation = true };
            return new(false) { InstallerCommand = args[0] switch
            {
                "--installer-health-check" => InstallerCommand.HealthCheck,
                "--installer-register-broker" => InstallerCommand.RegisterBroker,
                "--installer-uninstall-broker" => InstallerCommand.UninstallBroker,
                "--installer-prepare" => InstallerCommand.Prepare,
                "--installer-backup-broker" => InstallerCommand.BackupBroker,
                "--installer-restore-broker" => InstallerCommand.RestoreBroker,
                "--installer-commit-broker" => InstallerCommand.CommitBroker,
                _ => InstallerCommand.Invalid
            }};
        }
        return new(args.Any(argument => string.Equals(
            argument,
            SmokeTestNoDevicesArgument,
            StringComparison.OrdinalIgnoreCase)));
    }
}

internal enum InstallerCommand { None, Invalid, HealthCheck, RegisterBroker, UninstallBroker, Prepare, BackupBroker, RestoreBroker, CommitBroker }

internal sealed class EmptyUsbDeviceMonitor : IUsbDeviceMonitor
{
    public event UsbDevicesChangedEventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsbDeviceDescriptor>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<UsbDeviceDescriptor>>([]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
