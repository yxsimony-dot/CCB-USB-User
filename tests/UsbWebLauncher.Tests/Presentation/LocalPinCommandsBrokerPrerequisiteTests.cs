using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class LocalPinCommandsBrokerPrerequisiteTests
{
    [Fact]
    public async Task InstallAsync_UsesOnlyFixedInstallVerbAndAbsolutePolicyPath()
    {
        var launcher = new RecordingElevationLauncher();
        var prerequisites = Create(launcher: launcher);

        var installed = await prerequisites.InstallAsync();

        Assert.True(installed);
        Assert.Equal(BrokerPath, launcher.ExecutablePath);
        Assert.Equal(["--install", PolicyPath], launcher.Arguments);
        Assert.DoesNotContain(
            launcher.Arguments,
            value => value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("pin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallAsync_UserCancelledUacPublishesSafeRetryableState()
    {
        var launcher = new RecordingElevationLauncher
        {
            Result = NativePromptBrokerElevationResult.Cancelled
        };
        var prerequisites = Create(launcher: launcher);

        var installed = await prerequisites.InstallAsync();

        Assert.False(installed);
        Assert.False(prerequisites.CurrentState.IsHealthy);
        Assert.Contains("取消", prerequisites.CurrentState.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", prerequisites.CurrentState.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_ElevatedInstallerFailurePublishesExitCodeWithoutSecret()
    {
        var launcher = new RecordingElevationLauncher
        {
            Result = NativePromptBrokerElevationResult.Failed,
            ExitCode = 16
        };
        var prerequisites = Create(launcher: launcher);

        var installed = await prerequisites.InstallAsync();

        Assert.False(installed);
        Assert.Contains("16", prerequisites.CurrentState.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("pin", prerequisites.CurrentState.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", prerequisites.CurrentState.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_WaitsForNewBrokerToBecomeHealthyBeforeReportingSuccess()
    {
        var manager = new RecordingBrokerManager();
        manager.HealthResults.Enqueue(new(false, 0));
        manager.HealthResults.Enqueue(new(false, 0));
        manager.HealthResults.Enqueue(new(true, NativePromptProtocol.ProtocolVersion));
        var prerequisites = Create(manager: manager);

        var installed = await prerequisites.InstallAsync();

        Assert.True(installed);
        Assert.True(prerequisites.CurrentState.IsInstalled);
        Assert.True(prerequisites.CurrentState.IsHealthy);
        Assert.Equal(3, manager.HealthCount);
    }

    [Fact]
    public async Task RefreshHealthAsync_VersionMismatchRemainsUnhealthy()
    {
        var manager = new RecordingBrokerManager
        {
            Health = new(true, NativePromptProtocol.ProtocolVersion + 1)
        };
        var prerequisites = Create(manager: manager);

        var healthy = await prerequisites.RefreshHealthAsync();

        Assert.False(healthy);
        Assert.True(prerequisites.CurrentState.IsInstalled);
        Assert.False(prerequisites.CurrentState.IsHealthy);
        Assert.Contains("版本", prerequisites.CurrentState.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallAsync_UsesOnlyFixedVerbAndClearsHealthyState()
    {
        var launcher = new RecordingElevationLauncher();
        var manager = new RecordingBrokerManager();
        var prerequisites = Create(manager, launcher);
        _ = await prerequisites.RefreshHealthAsync();

        var removed = await prerequisites.UninstallAsync();

        Assert.True(removed);
        Assert.Equal(["--uninstall"], launcher.Arguments);
        Assert.False(prerequisites.CurrentState.IsInstalled);
        Assert.False(prerequisites.CurrentState.IsHealthy);
    }

    [Fact]
    public async Task NotRequired_PerformsNoElevationOrHealthCalls()
    {
        var launcher = new RecordingElevationLauncher();
        var manager = new RecordingBrokerManager();
        var prerequisites = new WindowsNativePromptBrokerPrerequisites(
            isRequired: false,
            BrokerPath,
            PolicyPath,
            manager,
            launcher);

        Assert.True(await prerequisites.InstallAsync());
        Assert.True(await prerequisites.RefreshHealthAsync());
        Assert.True(await prerequisites.UninstallAsync());
        Assert.Equal(0, launcher.CallCount);
        Assert.Equal(0, manager.HealthCount);
    }

    private const string BrokerPath = @"D:\Bundle\broker\UsbWebLauncher.NativePromptBroker.exe";
    private const string PolicyPath = @"D:\Bundle\broker\native-prompt-policy.json";

    private static WindowsNativePromptBrokerPrerequisites Create(
        RecordingBrokerManager? manager = null,
        RecordingElevationLauncher? launcher = null) =>
        new(
            isRequired: true,
            BrokerPath,
            PolicyPath,
            manager ?? new RecordingBrokerManager(),
            launcher ?? new RecordingElevationLauncher());

    private sealed class RecordingBrokerManager : INativePromptBrokerManager
    {
        public NativePromptBrokerHealthResult Health { get; set; } =
            new(true, NativePromptProtocol.ProtocolVersion);
        public Queue<NativePromptBrokerHealthResult> HealthResults { get; } = new();
        public int HealthCount { get; private set; }

        public ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
            CancellationToken cancellationToken)
        {
            HealthCount++;
            return ValueTask.FromResult(
                HealthResults.Count == 0 ? Health : HealthResults.Dequeue());
        }
    }

    private sealed class RecordingElevationLauncher : INativePromptBrokerElevationLauncher
    {
        public NativePromptBrokerElevationResult Result { get; set; } =
            NativePromptBrokerElevationResult.Success;
        public int? ExitCode { get; set; }
        public int CallCount { get; private set; }
        public string? ExecutablePath { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<NativePromptBrokerElevationOutcome> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ExecutablePath = executablePath;
            Arguments = arguments.ToArray();
            return Task.FromResult(new NativePromptBrokerElevationOutcome(Result, ExitCode));
        }
    }
}
