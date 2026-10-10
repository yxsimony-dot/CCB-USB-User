using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.App.Presentation;

internal enum NativePromptBrokerElevationResult
{
    Success = 0,
    Cancelled = 1,
    Failed = 2
}

internal sealed record NativePromptBrokerElevationOutcome(
    NativePromptBrokerElevationResult Result,
    int? ExitCode = null);

internal interface INativePromptBrokerElevationLauncher
{
    Task<NativePromptBrokerElevationOutcome> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface INativePromptBrokerManager
{
    ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken);
}

internal sealed class NativePromptBrokerManager : INativePromptBrokerManager
{
    private readonly WindowsNativePromptBrokerInstaller _installer = new();

    public ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken) =>
        _installer.CheckHealthAsync(cancellationToken);
}

internal sealed class WindowsNativePromptBrokerElevationLauncher
    : INativePromptBrokerElevationLauncher
{
    private const int ErrorCancelled = 1223;

    public async Task<NativePromptBrokerElevationOutcome> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!IsAbsoluteLocalPath(executablePath) ||
            !string.Equals(
                Path.GetFileName(executablePath),
                WindowsNativePromptBrokerInstaller.BrokerFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedArguments(arguments))
        {
            return new(NativePromptBrokerElevationResult.Failed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(NativePromptBrokerElevationResult.Failed);
            }

            // Once an elevated mutation starts, cancellation must not let an outer
            // installer restore files while this child is still changing them.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new(
                process.ExitCode == 0
                    ? NativePromptBrokerElevationResult.Success
                    : NativePromptBrokerElevationResult.Failed,
                process.ExitCode);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new(NativePromptBrokerElevationResult.Cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(NativePromptBrokerElevationResult.Failed);
        }
    }

    private static bool IsAllowedArguments(IReadOnlyList<string> arguments) =>
        (arguments.Count == 1 &&
         arguments[0] is "--uninstall" or "--backup" or "--restore" or "--commit") ||
        (arguments.Count == 2 &&
         string.Equals(arguments[0], "--install", StringComparison.Ordinal) &&
         IsAbsoluteLocalPath(arguments[1]));

    private static bool IsAbsoluteLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal);
}

internal sealed class WindowsNativePromptBrokerPrerequisites
    : INativePromptBrokerPrerequisites
{
    private const int BrokerStartupProbeAttempts = 12;
    private static readonly TimeSpan BrokerStartupProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BrokerStartupProbeTimeout = TimeSpan.FromMilliseconds(750);
    private readonly string _brokerExecutablePath;
    private readonly string _policyPath;
    private readonly INativePromptBrokerManager _manager;
    private readonly INativePromptBrokerElevationLauncher _elevation;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private NativePromptBrokerPrerequisiteState _state;

    public WindowsNativePromptBrokerPrerequisites(bool isRequired)
        : this(
            isRequired,
            Path.Combine(
                AppContext.BaseDirectory,
                "broker",
                WindowsNativePromptBrokerInstaller.BrokerFileName),
            Path.Combine(
                AppContext.BaseDirectory,
                "broker",
                WindowsNativePromptBrokerInstaller.PolicyFileName),
            new NativePromptBrokerManager(),
            new WindowsNativePromptBrokerElevationLauncher())
    {
    }

    internal WindowsNativePromptBrokerPrerequisites(
        bool isRequired,
        string brokerExecutablePath,
        string policyPath,
        INativePromptBrokerManager manager,
        INativePromptBrokerElevationLauncher elevation)
    {
        _brokerExecutablePath = Path.GetFullPath(brokerExecutablePath);
        _policyPath = Path.GetFullPath(policyPath);
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _state = isRequired
            ? new(true, false, false, "正在检查本机认证组件。")
            : NativePromptBrokerPrerequisiteState.NotRequired;
    }

    public event EventHandler? StateChanged;

    public NativePromptBrokerPrerequisiteState CurrentState => Volatile.Read(ref _state);

    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentState.IsRequired)
        {
            return true;
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            Publish(CurrentState with { StatusText = "正在请求管理员确认。" });
            var result = await _elevation.RunAsync(
                _brokerExecutablePath,
                ["--install", _policyPath],
                cancellationToken).ConfigureAwait(true);
            if (result.Result == NativePromptBrokerElevationResult.Success)
            {
                Publish(CurrentState with
                {
                    IsInstalled = true,
                    IsHealthy = false,
                    StatusText = "本机认证组件已安装，正在检查状态。"
                });
                var isHealthy = await WaitForHealthyBrokerAsync(cancellationToken)
                    .ConfigureAwait(true);
                Publish(CurrentState with
                {
                    IsInstalled = true,
                    IsHealthy = isHealthy,
                    StatusText = isHealthy
                        ? "本机认证组件已就绪。"
                        : "本机认证组件已安装，但代理未能通过启动检查。"
                });
                return isHealthy;
            }

            Publish(CurrentState with
            {
                IsHealthy = false,
                StatusText = result.Result == NativePromptBrokerElevationResult.Cancelled
                    ? "已取消安装本机认证组件，可稍后重试。"
                    : result.ExitCode is int exitCode
                        ? $"本机认证组件安装失败（退出码：{exitCode}），可重试安装。"
                        : "本机认证组件安装失败，可重试安装。"
            });
            return false;
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<bool> WaitForHealthyBrokerAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < BrokerStartupProbeAttempts; attempt++)
        {
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            probeCancellation.CancelAfter(BrokerStartupProbeTimeout);
            try
            {
                var health = await _manager.CheckHealthAsync(probeCancellation.Token)
                    .ConfigureAwait(true);
                if (health.IsHealthy &&
                    health.ProtocolVersion == WindowsNativePromptBrokerInstaller.CurrentProtocolVersion)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (
                probeCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (attempt + 1 < BrokerStartupProbeAttempts)
            {
                await Task.Delay(BrokerStartupProbeInterval, cancellationToken)
                    .ConfigureAwait(true);
            }
        }

        return false;
    }

    public async Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentState.IsRequired)
        {
            return true;
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var health = await _manager.CheckHealthAsync(cancellationToken).ConfigureAwait(true);
            var versionMatches =
                health.ProtocolVersion == WindowsNativePromptBrokerInstaller.CurrentProtocolVersion;
            var isHealthy = health.IsHealthy && versionMatches;
            Publish(CurrentState with
            {
                IsInstalled = health.ProtocolVersion > 0,
                IsHealthy = isHealthy,
                StatusText = isHealthy
                    ? "本机认证组件已就绪。"
                    : health.IsHealthy
                        ? "本机认证组件版本不匹配，请重新安装。"
                        : "需要安装或修复本机认证组件。"
            });
            return isHealthy;
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!CurrentState.IsRequired)
        {
            return true;
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            Publish(CurrentState with { StatusText = "正在请求管理员确认卸载。" });
            var result = await _elevation.RunAsync(
                _brokerExecutablePath,
                ["--uninstall"],
                cancellationToken).ConfigureAwait(true);
            if (result.Result != NativePromptBrokerElevationResult.Success)
            {
                Publish(CurrentState with
                {
                    StatusText = result.Result == NativePromptBrokerElevationResult.Cancelled
                        ? "已取消卸载本机认证组件。"
                        : result.ExitCode is int exitCode
                            ? $"本机认证组件卸载失败（退出码：{exitCode}）。"
                            : "本机认证组件卸载失败。"
                });
                return false;
            }

            Publish(new(true, false, false, "本机认证组件已卸载。"));
            return true;
        }
        finally
        {
            _operation.Release();
        }
    }

    private void Publish(NativePromptBrokerPrerequisiteState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
