using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.App.Composition;

// Deliberately has no dependency on profile, credentials, devices, or browser/runtime composition.
internal sealed class InstallerCommandRunner(
    INativePromptBrokerPrerequisites prerequisites,
    Func<CancellationToken, Task<bool>> prepare,
    Func<string, CancellationToken, Task<bool>> transaction,
    Func<bool>? isOrdinaryUser = null)
{
    public static InstallerCommandRunner Create() => new(
        new WindowsNativePromptBrokerPrerequisites(true),
        async token => (await new WindowsNativePromptBrokerInstaller().PrepareInstallationAsync(token)).IsSuccess,
        async (verb, token) =>
        {
            var result = await new WindowsNativePromptBrokerElevationLauncher().RunAsync(
                Path.Combine(AppContext.BaseDirectory, "broker", WindowsNativePromptBrokerInstaller.BrokerFileName), [verb], token);
            return result.Result == NativePromptBrokerElevationResult.Success;
        });

    public async Task<int> RunAsync(InstallerCommand command)
    {
        using var timeout = new CancellationTokenSource(command == InstallerCommand.HealthCheck
            ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        try
        {
            if (!(isOrdinaryUser?.Invoke() ?? IsOrdinaryInteractiveUser())) return 1;
            var operation = command switch
            {
                InstallerCommand.HealthCheck => prerequisites.RefreshHealthAsync(token),
                InstallerCommand.Prepare => prepare(token),
                InstallerCommand.RegisterBroker => GuardAsync(() => prerequisites.InstallAsync(token), token),
                InstallerCommand.UninstallBroker => GuardAsync(() => prerequisites.UninstallAsync(token), token),
                InstallerCommand.BackupBroker => GuardAsync(() => transaction("--backup", token), token),
                // Recovery authorizes against the protected transaction snapshot in
                // the elevated helper, even when a failed install left no owner/task.
                InstallerCommand.RestoreBroker => transaction("--restore", token),
                InstallerCommand.CommitBroker => transaction("--commit", token),
                _ => Task.FromResult(false)
            };
            // Read-only probes have a hard deadline. A launched elevated mutation
            // must finish before the caller may decide whether to roll back.
            var succeeded = command is InstallerCommand.HealthCheck or InstallerCommand.Prepare
                ? await operation.WaitAsync(token)
                : await operation;
            return succeeded ? 0 : 1;
        }
        catch { return 1; }
    }

    private async Task<bool> GuardAsync(Func<Task<bool>> action, CancellationToken token) =>
        await prepare(token) && await action();

    private static bool IsOrdinaryInteractiveUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return Environment.UserInteractive && process.SessionId != 0 && !identity.IsSystem &&
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
