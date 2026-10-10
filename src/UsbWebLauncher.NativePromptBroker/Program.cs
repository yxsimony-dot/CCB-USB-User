using UsbWebLauncher.Windows.Authentication.NativePrompt;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    if (!NativePromptBrokerCommand.TryParse(args, out var command))
    {
        return 2;
    }

    try
    {
        var api = new WindowsNativePromptBrokerInstallationApi();
        var installer = new WindowsNativePromptBrokerInstaller(api);
        switch (command.Kind)
        {
            case NativePromptBrokerCommandKind.Install:
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) || command.PolicyPath is null)
                {
                    return 3;
                }

                var result = await installer.InstallAsync(
                    new(executable, command.PolicyPath),
                    CancellationToken.None).ConfigureAwait(false);
                return result.IsSuccess ? 0 : 10 + (int)result.Status;
            }
            case NativePromptBrokerCommandKind.Uninstall:
            {
                var result = await installer.UninstallAsync(CancellationToken.None).ConfigureAwait(false);
                return result.IsSuccess ? 0 : 30 + (int)result.Status;
            }
            case NativePromptBrokerCommandKind.Backup:
            case NativePromptBrokerCommandKind.Restore:
            case NativePromptBrokerCommandKind.Commit:
            {
                var result = command.Kind switch
                {
                    NativePromptBrokerCommandKind.Backup => await installer.BackupInstallationAsync(),
                    NativePromptBrokerCommandKind.Restore => await installer.RestoreInstallationAsync(),
                    _ => await installer.CommitInstallationAsync()
                };
                return result.IsSuccess ? 0 : 50 + (int)result.Status;
            }
            case NativePromptBrokerCommandKind.Server:
            {
                var installDirectory = Path.GetFullPath(Path.Combine(
                    api.ProgramFilesPath,
                    "UsbWebLauncher",
                    "NativePromptBroker"));
                var expectedExecutable = Path.Combine(
                    installDirectory,
                    WindowsNativePromptBrokerInstaller.BrokerFileName);
                var currentExecutable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(currentExecutable) ||
                    !string.Equals(
                        Path.GetFullPath(currentExecutable),
                        expectedExecutable,
                        StringComparison.OrdinalIgnoreCase) ||
                    !api.IsHighIntegrity)
                {
                    return 4;
                }

                NativePromptBrokerFailureLog.Clear();
                using var shutdown = new CancellationTokenSource();
                EventHandler processExit = (_, _) =>
                    NativePromptBrokerProcessLifetime.TryCancel(shutdown);
                AppDomain.CurrentDomain.ProcessExit += processExit;
                try
                {
                    var policyPath = Path.Combine(
                        installDirectory,
                        WindowsNativePromptBrokerInstaller.PolicyFileName);
                    return await WindowsNativePromptBrokerRuntime.RunAsync(
                        policyPath,
                        shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    AppDomain.CurrentDomain.ProcessExit -= processExit;
                }
            }
            default:
                return 2;
        }
    }
    catch (OperationCanceledException)
    {
        return 5;
    }
    catch (Exception exception)
    {
        if (command.Kind == NativePromptBrokerCommandKind.Server)
        {
            NativePromptBrokerFailureLog.Write(exception);
        }
        return 6;
    }
}
