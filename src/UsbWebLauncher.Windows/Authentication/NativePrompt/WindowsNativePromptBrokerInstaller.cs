using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal enum NativePromptBrokerCommandKind
{
    Server = 0,
    Install = 1,
    Uninstall = 2,
    Backup = 3,
    Restore = 4,
    Commit = 5
}

internal sealed record NativePromptBrokerCommand(
    NativePromptBrokerCommandKind Kind,
    string? PolicyPath = null)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out NativePromptBrokerCommand command)
    {
        command = new(NativePromptBrokerCommandKind.Server);
        if (args.Count == 1 && string.Equals(args[0], "--server", StringComparison.Ordinal))
        {
            return true;
        }

        if (args.Count == 1 && string.Equals(args[0], "--uninstall", StringComparison.Ordinal))
        {
            command = new(NativePromptBrokerCommandKind.Uninstall);
            return true;
        }

        if (args.Count == 2 && string.Equals(args[0], "--install", StringComparison.Ordinal) &&
            IsAbsoluteLocalPath(args[1]))
        {
            command = new(NativePromptBrokerCommandKind.Install, args[1]);
            return true;
        }

        if (args.Count == 1)
        {
            var kind = args[0] switch { "--backup" => NativePromptBrokerCommandKind.Backup,
                "--restore" => NativePromptBrokerCommandKind.Restore,
                "--commit" => NativePromptBrokerCommandKind.Commit, _ => (NativePromptBrokerCommandKind?)null };
            if (kind.HasValue) { command = new(kind.Value); return true; }
        }
        return false;
    }

    private static bool IsAbsoluteLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal);
}

public enum NativePromptBrokerInstallationStatus
{
    Success = 0,
    NotElevated = 1,
    InvalidPackage = 2,
    InvalidInstallPath = 3,
    CopyFailed = 4,
    ProtectionFailed = 5,
    TaskRegistrationFailed = 6,
    TaskStartFailed = 7,
    HealthCheckFailed = 8,
    UninstallFailed = 9,
    RollbackFailed = 10,
    UnknownFailure = 11,
    TaskInspectionFailed = 12,
    DirectoryCreationFailed = 13,
    PolicyWriteFailed = 14,
    ExistingInstallationPreparationFailed = 15,
    OwnershipMismatch = 16,
    OwnershipInspectionFailed = 17,
    TransactionFailed = 18
}

public sealed record NativePromptBrokerInstallationResult(
    NativePromptBrokerInstallationStatus Status)
{
    public bool IsSuccess => Status == NativePromptBrokerInstallationStatus.Success;
}

public sealed record NativePromptBrokerPackage(
    string BrokerExecutablePath,
    string PolicyFilePath);

public sealed record NativePromptBrokerHealthResult(
    bool IsHealthy,
    int ProtocolVersion);

internal enum BrokerRunLevel
{
    Limited = 0,
    Highest = 1
}

internal sealed record BrokerScheduledTaskDefinition(
    string Name,
    string ExecutablePath,
    string Arguments,
    string UserName,
    bool RunOnlyForInteractiveUser,
    BrokerRunLevel RunLevel);

internal interface INativePromptBrokerInstallationApi
{
    ValueTask<NativePromptBrokerInstallationStatus> CheckOwnershipAsync(CancellationToken cancellationToken);
    ValueTask WriteOwnerAsync(byte[] policy, CancellationToken cancellationToken);
    ValueTask ExecuteTransactionAsync(NativePromptBrokerCommandKind command, CancellationToken cancellationToken);

    bool IsHighIntegrity { get; }

    string ProgramFilesPath { get; }

    string CurrentInteractiveUser { get; }

    string CurrentProcessPath { get; }

    string ResolvePath(string path);

    ValueTask<byte[]?> ReadValidatedPolicyAsync(string path, CancellationToken cancellationToken);

    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    ValueTask CopyFileAsync(string source, string destination, CancellationToken cancellationToken);

    ValueTask PrepareUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken);

    ValueTask RestoreUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken);

    ValueTask DeleteUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken);

    ValueTask WritePolicyFileAsync(
        byte[] validatedPolicy,
        string destination,
        CancellationToken cancellationToken);

    ValueTask ProtectDirectoryAsync(string path, CancellationToken cancellationToken);

    ValueTask RegisterTaskAsync(
        BrokerScheduledTaskDefinition definition,
        CancellationToken cancellationToken);

    ValueTask StartTaskAsync(string taskName, CancellationToken cancellationToken);

    ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
        string pipeName,
        CancellationToken cancellationToken);

    ValueTask<bool> TaskExistsAsync(string taskName, CancellationToken cancellationToken);

    ValueTask<bool> IsSameBinaryIdentityAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken);

    ValueTask StopTaskAsync(string taskName, CancellationToken cancellationToken);

    ValueTask DeleteTaskAsync(string taskName, CancellationToken cancellationToken);

    ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken);
}

internal interface INativePromptScheduledTaskRegistrar
{
    ValueTask RegisterAsync(
        BrokerScheduledTaskDefinition definition,
        CancellationToken cancellationToken);
}

public sealed class WindowsNativePromptBrokerInstaller
{
    public const string BrokerFileName = "UsbWebLauncher.NativePromptBroker.exe";
    public const string PolicyFileName = "native-prompt-policy.json";
    public const string ScheduledTaskName = "UsbWebLauncher Native Prompt Broker";
    public const string BrokerPipeName = "UsbWebLauncher.NativePromptBroker.v1";
    public const int CurrentProtocolVersion = 1;

    private const string ProductDirectory = "UsbWebLauncher";
    private const string BrokerDirectory = "NativePromptBroker";
    private readonly INativePromptBrokerInstallationApi _api;

    public WindowsNativePromptBrokerInstaller()
        : this(new WindowsNativePromptBrokerInstallationApi())
    {
    }

    internal WindowsNativePromptBrokerInstaller(INativePromptBrokerInstallationApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        _api.CheckHealthAsync(BrokerPipeName, cancellationToken);

    /// <summary>Read-only check for the original interactive user and existing machine-wide broker ownership.</summary>
    public async ValueTask<NativePromptBrokerInstallationResult> PrepareInstallationAsync(CancellationToken cancellationToken = default)
    {
        try { return new(await _api.CheckOwnershipAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(NativePromptBrokerInstallationStatus.OwnershipInspectionFailed); }
    }

    public ValueTask<NativePromptBrokerInstallationResult> BackupInstallationAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(NativePromptBrokerCommandKind.Backup, cancellationToken);
    public ValueTask<NativePromptBrokerInstallationResult> RestoreInstallationAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(NativePromptBrokerCommandKind.Restore, cancellationToken);
    public ValueTask<NativePromptBrokerInstallationResult> CommitInstallationAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync(NativePromptBrokerCommandKind.Commit, cancellationToken);

    private async ValueTask<NativePromptBrokerInstallationResult> ExecuteTransactionAsync(NativePromptBrokerCommandKind command, CancellationToken cancellationToken)
    {
        if (!_api.IsHighIntegrity) return new(NativePromptBrokerInstallationStatus.NotElevated);
        try
        {
            await _api.ExecuteTransactionAsync(command, cancellationToken).ConfigureAwait(false);
            return new(NativePromptBrokerInstallationStatus.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(NativePromptBrokerInstallationStatus.TransactionFailed); }
    }

    internal ValueTask FinalizeUpdateAsync(CancellationToken cancellationToken = default) =>
        _api.DeleteUpdateBackupAsync(ExpectedInstallDirectory(), cancellationToken);

    public async ValueTask<NativePromptBrokerInstallationResult> InstallAsync(
        NativePromptBrokerPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!_api.IsHighIntegrity)
        {
            return new(NativePromptBrokerInstallationStatus.NotElevated);
        }

        var ownership = await PrepareInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (!ownership.IsSuccess) return ownership;

        if (!TryGetValidatedPaths(
                package,
                out var installDirectory,
                out var brokerSource,
                out var policySource))
        {
            return new(NativePromptBrokerInstallationStatus.InvalidPackage);
        }

        var brokerDestination = Path.Combine(installDirectory, BrokerFileName);
        var policyDestination = Path.Combine(installDirectory, PolicyFileName);
        byte[]? validatedPolicy;
        try
        {
            validatedPolicy = await _api.ReadValidatedPolicyAsync(
                policySource,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(NativePromptBrokerInstallationStatus.InvalidPackage);
        }

        if (validatedPolicy is null)
        {
            return new(NativePromptBrokerInstallationStatus.InvalidPackage);
        }

        var failure = NativePromptBrokerInstallationStatus.TaskInspectionFailed;
        var mutationStarted = false;
        var hadExistingTask = false;
        var updateBackupPrepared = false;
        BrokerScheduledTaskDefinition? task = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            task = new BrokerScheduledTaskDefinition(
                ScheduledTaskName,
                brokerDestination,
                "--server",
                _api.CurrentInteractiveUser,
                RunOnlyForInteractiveUser: true,
                BrokerRunLevel.Highest);
            hadExistingTask = await _api.TaskExistsAsync(
                ScheduledTaskName,
                cancellationToken).ConfigureAwait(false);
            if (hadExistingTask)
            {
                failure = NativePromptBrokerInstallationStatus.ExistingInstallationPreparationFailed;
                mutationStarted = true;
                await _api.StopTaskAsync(ScheduledTaskName, cancellationToken).ConfigureAwait(false);
                await _api.PrepareUpdateBackupAsync(
                    installDirectory,
                    cancellationToken).ConfigureAwait(false);
                updateBackupPrepared = true;
            }

            mutationStarted = true;
            failure = NativePromptBrokerInstallationStatus.DirectoryCreationFailed;
            await _api.CreateDirectoryAsync(installDirectory, cancellationToken).ConfigureAwait(false);
            failure = NativePromptBrokerInstallationStatus.CopyFailed;
            await _api.CopyFileAsync(brokerSource, brokerDestination, cancellationToken).ConfigureAwait(false);
            failure = NativePromptBrokerInstallationStatus.PolicyWriteFailed;
            await _api.WritePolicyFileAsync(
                validatedPolicy,
                policyDestination,
                cancellationToken).ConfigureAwait(false);

            failure = NativePromptBrokerInstallationStatus.ProtectionFailed;
            await _api.ProtectDirectoryAsync(installDirectory, cancellationToken).ConfigureAwait(false);

            await _api.WriteOwnerAsync(validatedPolicy, cancellationToken).ConfigureAwait(false);

            failure = NativePromptBrokerInstallationStatus.TaskRegistrationFailed;
            await _api.RegisterTaskAsync(task, cancellationToken).ConfigureAwait(false);

            failure = NativePromptBrokerInstallationStatus.TaskStartFailed;
            await _api.StartTaskAsync(ScheduledTaskName, cancellationToken).ConfigureAwait(false);

            return new(NativePromptBrokerInstallationStatus.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mutationStarted && !await RecoverInstallationAsync(
                    installDirectory,
                    hadExistingTask,
                    updateBackupPrepared,
                    task).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Native prompt broker installation was cancelled and rollback was incomplete.");
            }

            throw;
        }
        catch
        {
            if (!mutationStarted)
            {
                return new(failure);
            }

            return await RecoverInstallationAsync(
                    installDirectory,
                    hadExistingTask,
                    updateBackupPrepared,
                    task).ConfigureAwait(false)
                ? new(failure)
                : new(NativePromptBrokerInstallationStatus.RollbackFailed);
        }
        finally
        {
            Array.Clear(validatedPolicy, 0, validatedPolicy.Length);
        }
    }

    public async ValueTask<NativePromptBrokerInstallationResult> UninstallAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_api.IsHighIntegrity)
        {
            return new(NativePromptBrokerInstallationStatus.NotElevated);
        }

        var ownership = await PrepareInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (!ownership.IsSuccess) return ownership;

        var expected = ExpectedInstallDirectory();
        string resolved;
        try
        {
            resolved = _api.ResolvePath(expected);
        }
        catch
        {
            return new(NativePromptBrokerInstallationStatus.InvalidInstallPath);
        }

        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            return new(NativePromptBrokerInstallationStatus.InvalidInstallPath);
        }

        string currentProcess;
        try
        {
            currentProcess = _api.ResolvePath(_api.CurrentProcessPath);
        }
        catch
        {
            return new(NativePromptBrokerInstallationStatus.InvalidInstallPath);
        }

        if (IsAtOrBelow(currentProcess, expected))
        {
            return new(NativePromptBrokerInstallationStatus.InvalidInstallPath);
        }

        var installedBroker = Path.Combine(expected, BrokerFileName);
        if (!await _api.IsSameBinaryIdentityAsync(
                currentProcess,
                installedBroker,
                cancellationToken).ConfigureAwait(false))
        {
            return new(NativePromptBrokerInstallationStatus.InvalidInstallPath);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _api.TaskExistsAsync(ScheduledTaskName, cancellationToken).ConfigureAwait(false))
            {
                await _api.StopTaskAsync(ScheduledTaskName, cancellationToken).ConfigureAwait(false);
                await _api.DeleteTaskAsync(ScheduledTaskName, cancellationToken).ConfigureAwait(false);
            }

            await _api.DeleteDirectoryAsync(expected, cancellationToken).ConfigureAwait(false);
            return new(NativePromptBrokerInstallationStatus.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(NativePromptBrokerInstallationStatus.UninstallFailed);
        }
    }

    private bool TryGetValidatedPaths(
        NativePromptBrokerPackage package,
        out string installDirectory,
        out string brokerSource,
        out string policySource)
    {
        installDirectory = ExpectedInstallDirectory();
        brokerSource = string.Empty;
        policySource = string.Empty;
        try
        {
            if (!IsAbsoluteLocalPath(package.BrokerExecutablePath) ||
                !IsAbsoluteLocalPath(package.PolicyFilePath))
            {
                return false;
            }

            var resolvedInstall = _api.ResolvePath(installDirectory);
            if (!string.Equals(resolvedInstall, installDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            brokerSource = _api.ResolvePath(package.BrokerExecutablePath);
            policySource = _api.ResolvePath(package.PolicyFilePath);
            return !IsAtOrBelow(brokerSource, installDirectory) &&
                   !IsAtOrBelow(policySource, installDirectory) &&
                   !string.Equals(brokerSource, policySource, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string ExpectedInstallDirectory() =>
        Path.GetFullPath(Path.Combine(
            _api.ProgramFilesPath,
            ProductDirectory,
            BrokerDirectory));

    private async ValueTask<bool> RollBackAsync(string installDirectory)
    {
        var complete = true;
        try
        {
            if (await _api.TaskExistsAsync(
                    ScheduledTaskName,
                    CancellationToken.None).ConfigureAwait(false))
            {
                complete &= await TryCleanupAsync(
                    () => _api.StopTaskAsync(ScheduledTaskName, CancellationToken.None)).ConfigureAwait(false);
                complete &= await TryCleanupAsync(
                    () => _api.DeleteTaskAsync(ScheduledTaskName, CancellationToken.None)).ConfigureAwait(false);
            }
        }
        catch
        {
            complete = false;
        }

        complete &= await TryCleanupAsync(
            () => _api.DeleteDirectoryAsync(installDirectory, CancellationToken.None)).ConfigureAwait(false);
        return complete;
    }

    private ValueTask<bool> RecoverInstallationAsync(
        string installDirectory,
        bool hadExistingTask,
        bool updateBackupPrepared,
        BrokerScheduledTaskDefinition? task)
    {
        if (!hadExistingTask)
        {
            return RollBackAsync(installDirectory);
        }

        return RestorePreviousInstallationAsync(
            installDirectory,
            updateBackupPrepared,
            task);
    }

    private async ValueTask<bool> RestorePreviousInstallationAsync(
        string installDirectory,
        bool updateBackupPrepared,
        BrokerScheduledTaskDefinition? task)
    {
        if (!await TryCleanupAsync(
                () => _api.StopTaskAsync(
                    ScheduledTaskName,
                    CancellationToken.None)).ConfigureAwait(false))
        {
            return false;
        }

        if (updateBackupPrepared)
        {
            if (task is null ||
                !await TryCleanupAsync(
                    () => _api.RestoreUpdateBackupAsync(
                        installDirectory,
                        CancellationToken.None)).ConfigureAwait(false) ||
                !await TryCleanupAsync(
                    () => _api.ProtectDirectoryAsync(
                        installDirectory,
                        CancellationToken.None)).ConfigureAwait(false) ||
                !await TryCleanupAsync(
                    () => _api.RegisterTaskAsync(
                        task,
                        CancellationToken.None)).ConfigureAwait(false))
            {
                return false;
            }
        }

        if (!await TryCleanupAsync(
                () => _api.StartTaskAsync(
                    ScheduledTaskName,
                    CancellationToken.None)).ConfigureAwait(false))
        {
            return false;
        }

        _ = await TryCleanupAsync(
            () => _api.DeleteUpdateBackupAsync(
                installDirectory,
                CancellationToken.None)).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<bool> TryCleanupAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAbsoluteLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsAtOrBelow(string candidate, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   normalizedDirectory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class WindowsNativePromptScheduledTaskRegistrar : INativePromptScheduledTaskRegistrar
{
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;

    public ValueTask RegisterAsync(
        BrokerScheduledTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? service = null;
        object? folder = null;
        object? taskDefinition = null;
        object? registrationInfo = null;
        object? settings = null;
        object? principal = null;
        object? triggers = null;
        object? trigger = null;
        object? actions = null;
        object? action = null;
        object? registeredTask = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true) ??
                throw new InvalidOperationException("Windows Task Scheduler service is unavailable.");
            service = Activator.CreateInstance(serviceType) ??
                throw new InvalidOperationException("Windows Task Scheduler service could not be created.");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            taskDefinition = ((dynamic)service).NewTask(0);

            registrationInfo = ((dynamic)taskDefinition).RegistrationInfo;
            ((dynamic)registrationInfo).Description =
                "Runs the USB Web Launcher native prompt broker for the current interactive user.";

            settings = ((dynamic)taskDefinition).Settings;
            ((dynamic)settings).AllowDemandStart = true;
            ((dynamic)settings).DisallowStartIfOnBatteries = false;
            ((dynamic)settings).StopIfGoingOnBatteries = false;
            ((dynamic)settings).StartWhenAvailable = true;
            ((dynamic)settings).ExecutionTimeLimit = "PT0S";
            ((dynamic)settings).MultipleInstances = TaskInstancesIgnoreNew;

            principal = ((dynamic)taskDefinition).Principal;
            ((dynamic)principal).UserId = definition.UserName;
            ((dynamic)principal).LogonType = TaskLogonInteractiveToken;
            ((dynamic)principal).RunLevel = TaskRunLevelHighest;

            triggers = ((dynamic)taskDefinition).Triggers;
            trigger = ((dynamic)triggers).Create(TaskTriggerLogon);
            ((dynamic)trigger).Enabled = true;
            ((dynamic)trigger).UserId = definition.UserName;

            actions = ((dynamic)taskDefinition).Actions;
            action = ((dynamic)actions).Create(TaskActionExecute);
            ((dynamic)action).Path = definition.ExecutablePath;
            ((dynamic)action).Arguments = definition.Arguments;

            registeredTask = ((dynamic)folder).RegisterTaskDefinition(
                definition.Name,
                taskDefinition,
                TaskCreateOrUpdate,
                definition.UserName,
                null,
                TaskLogonInteractiveToken,
                null);
            return ValueTask.CompletedTask;
        }
        finally
        {
            ReleaseCom(registeredTask);
            ReleaseCom(action);
            ReleaseCom(actions);
            ReleaseCom(trigger);
            ReleaseCom(triggers);
            ReleaseCom(principal);
            ReleaseCom(settings);
            ReleaseCom(registrationInfo);
            ReleaseCom(taskDefinition);
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal sealed partial class WindowsNativePromptBrokerInstallationApi : INativePromptBrokerInstallationApi
{
    private const string BrokerUpdateBackupFileName = "UsbWebLauncher.NativePromptBroker.exe.update-backup";
    private const string PolicyUpdateBackupFileName = "native-prompt-policy.json.update-backup";
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int WtsActive = 0;
    private const int WtsConnectState = 8;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private readonly INativePromptBrokerHealthClient _healthClient;
    private readonly INativePromptInteractiveUserResolver _interactiveUserResolver;
    private readonly INativePromptScheduledTaskRegistrar _scheduledTaskRegistrar;
    private readonly string? _programFilesPath;

    public WindowsNativePromptBrokerInstallationApi()
        : this(
            new WindowsNativePromptBrokerHealthClient(),
            new WindowsNativePromptInteractiveUserResolver())
    {
    }

    internal WindowsNativePromptBrokerInstallationApi(
        INativePromptBrokerHealthClient healthClient,
        INativePromptInteractiveUserResolver interactiveUserResolver,
        string? programFilesPath = null,
        INativePromptScheduledTaskRegistrar? scheduledTaskRegistrar = null)
    {
        _healthClient = healthClient;
        _interactiveUserResolver = interactiveUserResolver;
        _scheduledTaskRegistrar = scheduledTaskRegistrar ?? new WindowsNativePromptScheduledTaskRegistrar();
        _programFilesPath = programFilesPath is null
            ? null
            : Path.GetFullPath(programFilesPath);
    }

    public bool IsHighIntegrity =>
        new WindowsNamedPipePeerNativeApi().TryGetIntegrityRid(
            Environment.ProcessId,
            out var integrityRid) &&
        integrityRid >= WindowsNamedPipePeerInspector.HighIntegrityRid;

    public string ProgramFilesPath =>
        _programFilesPath ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string CurrentInteractiveUser => _interactiveUserResolver.ResolveCurrentSessionUser();

    public string CurrentProcessPath => Environment.ProcessPath ?? string.Empty;

    public string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        RejectReparsePointComponents(fullPath);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            return fullPath;
        }

        using var handle = CreateFile(
            fullPath,
            FileReadAttributes,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Unable to resolve the protected broker path.", Marshal.GetHRForLastWin32Error());
        }

        var buffer = new StringBuilder(32_768);
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException("Unable to resolve the protected broker final path.");
        }

        return NormalizeFinalPath(buffer.ToString());
    }

    public async ValueTask<byte[]?> ReadValidatedPolicyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? contents = null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            contents = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(contents, cancellationToken).ConfigureAwait(false);
            var policy = NativePromptBrokerPolicyFile.Parse(contents);
            if (policy.TrustedClient is not null)
            {
                return null;
            }

            var policyDirectory = Path.GetDirectoryName(path);
            var packageDirectory = policyDirectory is null
                ? null
                : Directory.GetParent(policyDirectory)?.FullName;
            if (packageDirectory is null ||
                !string.Equals(
                    Path.GetFileName(policyDirectory),
                    "broker",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(path),
                    WindowsNativePromptBrokerInstaller.PolicyFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var clientPath = ResolvePath(Path.Combine(
                packageDirectory,
                NativePromptBrokerTrustedClient.ProcessFileName));
            var trustedClient = new NativePromptBrokerTrustedClient(
                clientPath,
                await ComputeSha256Async(clientPath, cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(
                    ResolvePath(Path.Combine(
                        packageDirectory,
                        NativePromptBrokerTrustedClient.ApplicationAssemblyFileName)),
                    cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(
                    ResolvePath(Path.Combine(
                        packageDirectory,
                        NativePromptBrokerTrustedClient.CoreAssemblyFileName)),
                    cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(
                    ResolvePath(Path.Combine(
                        packageDirectory,
                        NativePromptBrokerTrustedClient.WindowsAssemblyFileName)),
                    cancellationToken).ConfigureAwait(false));
            return NativePromptBrokerPolicyFile.BindTrustedClient(contents, trustedClient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (contents is not null)
            {
                Array.Clear(contents, 0, contents.Length);
            }
        }
    }

    private static async ValueTask<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] hash = [];
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }
        finally
        {
            Array.Clear(hash, 0, hash.Length);
        }
    }

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(source, destination, overwrite: true);
        return ValueTask.CompletedTask;
    }

    public ValueTask PrepareUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ValidateInstallDirectory(installDirectory);
        var broker = Path.Combine(expected, WindowsNativePromptBrokerInstaller.BrokerFileName);
        var policy = Path.Combine(expected, WindowsNativePromptBrokerInstaller.PolicyFileName);
        var brokerBackup = Path.Combine(expected, BrokerUpdateBackupFileName);
        var policyBackup = Path.Combine(expected, PolicyUpdateBackupFileName);
        try
        {
            var resolvedBrokerBackup = ValidateBackupFilePath(brokerBackup);
            var resolvedPolicyBackup = ValidateBackupFilePath(policyBackup);
            var brokerBackupExists = File.Exists(resolvedBrokerBackup);
            var policyBackupExists = File.Exists(resolvedPolicyBackup);
            if (brokerBackupExists && policyBackupExists)
            {
                return ValueTask.CompletedTask;
            }

            DeleteFileIfPresent(brokerBackup);
            DeleteFileIfPresent(policyBackup);
            DeleteFileIfPresent(Path.Combine(expected, OwnerFileName + ".update-backup"));
            File.Copy(broker, brokerBackup, overwrite: false);
            File.Copy(policy, policyBackup, overwrite: false);
            var owner = ValidateBackupFilePath(Path.Combine(expected, OwnerFileName));
            var ownerBackup = ValidateBackupFilePath(Path.Combine(expected, OwnerFileName + ".update-backup"));
            if (File.Exists(owner)) File.Copy(owner, ownerBackup, overwrite: false);
            return ValueTask.CompletedTask;
        }
        catch
        {
            DeleteFileIfPresent(brokerBackup);
            DeleteFileIfPresent(policyBackup);
            DeleteFileIfPresent(Path.Combine(expected, OwnerFileName + ".update-backup"));
            throw;
        }
    }

    public ValueTask RestoreUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ValidateInstallDirectory(installDirectory);
        File.Copy(
            Path.Combine(expected, BrokerUpdateBackupFileName),
            Path.Combine(expected, WindowsNativePromptBrokerInstaller.BrokerFileName),
            overwrite: true);
        File.Copy(
            Path.Combine(expected, PolicyUpdateBackupFileName),
            Path.Combine(expected, WindowsNativePromptBrokerInstaller.PolicyFileName),
            overwrite: true);
        var owner = ValidateBackupFilePath(Path.Combine(expected, OwnerFileName));
        var ownerBackup = ValidateBackupFilePath(Path.Combine(expected, OwnerFileName + ".update-backup"));
        if (File.Exists(ownerBackup)) File.Copy(ownerBackup, owner, overwrite: true);
        else if (File.Exists(owner)) File.Delete(owner);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteUpdateBackupAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ValidateInstallDirectory(installDirectory);
        DeleteFileIfPresent(Path.Combine(expected, BrokerUpdateBackupFileName));
        DeleteFileIfPresent(Path.Combine(expected, PolicyUpdateBackupFileName));
        DeleteFileIfPresent(Path.Combine(expected, OwnerFileName + ".update-backup"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask WritePolicyFileAsync(
        byte[] validatedPolicy,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validatedPolicy);
        await using var stream = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(validatedPolicy, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ProtectDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sid = ResolveAccountSid(CurrentInteractiveUser);
        ProtectTransactionDirectory(path, sid);
        foreach (var name in SnapshotFiles)
        {
            var file = ResolvePath(Path.Combine(path, name));
            if (File.Exists(file)) ProtectFile(file, sid);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask RegisterTaskAsync(
        BrokerScheduledTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!definition.RunOnlyForInteractiveUser || definition.RunLevel != BrokerRunLevel.Highest ||
            !string.Equals(definition.Arguments, "--server", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native prompt broker task definition is unsafe.");
        }

        await _scheduledTaskRegistrar.RegisterAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StartTaskAsync(string taskName, CancellationToken cancellationToken) =>
        RunSchtasksAsync(
            ["/Run", "/TN", taskName],
            allowNonZeroExitCode: false,
            cancellationToken);

    public ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(
        string pipeName,
        CancellationToken cancellationToken) =>
        _healthClient.CheckAsync(pipeName, cancellationToken);

    public ValueTask<bool> TaskExistsAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = TryOpenTask(taskName, out var service, out var folder, out var task);
        ReleaseCom(task);
        ReleaseCom(folder);
        ReleaseCom(service);
        return ValueTask.FromResult(exists);
    }

    public async ValueTask<bool> IsSameBinaryIdentityAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken)
    {
        byte[] firstHash = [];
        byte[] secondHash = [];
        try
        {
            await using var first = new FileStream(
                firstPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var second = new FileStream(
                secondPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            firstHash = await SHA256.HashDataAsync(first, cancellationToken).ConfigureAwait(false);
            secondHash = await SHA256.HashDataAsync(second, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            Array.Clear(firstHash, 0, firstHash.Length);
            Array.Clear(secondHash, 0, secondHash.Length);
        }
    }

    public async ValueTask StopTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOpenTask(taskName, out var service, out var folder, out var task))
        {
            return;
        }

        try
        {
            try
            {
                ((dynamic)task!).Stop(0);
            }
            catch (COMException exception) when (unchecked((uint)exception.HResult) == 0x8004130B)
            {
            }

            await WaitForTaskStoppedAsync(
                () => Convert.ToInt32(((dynamic)task!).State),
                () => IsBrokerImageReleased(Path.Combine(
                    ProgramFilesPath,
                    "UsbWebLauncher",
                    "NativePromptBroker",
                    WindowsNativePromptBrokerInstaller.BrokerFileName)),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    internal static async ValueTask WaitForTaskStoppedAsync(
        Func<int> readState,
        Func<bool>? isBrokerImageReleased,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readState);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = readState();
            if (state is 1 or 3 && (isBrokerImageReleased?.Invoke() ?? true))
            {
                return;
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException("The native prompt broker task did not stop in time.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static ValueTask WaitForTaskStoppedAsync(
        Func<int> readState,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        WaitForTaskStoppedAsync(
            readState,
            isBrokerImageReleased: null,
            pollInterval,
            timeout,
            cancellationToken);

    private static bool IsBrokerImageReleased(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read);
            return true;
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return false;
        }
    }

    public ValueTask DeleteTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOpenTask(taskName, out var service, out var folder, out var task))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            ((dynamic)folder!).DeleteTask(taskName, 0);
        }
        catch (Exception exception) when (IsTaskNotFoundHResult(exception.HResult))
        {
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ResolvePath(Path.Combine(
            ProgramFilesPath,
            "UsbWebLauncher",
            "NativePromptBroker"));
        var resolved = ResolvePath(path);
        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected directory.");
        }

        var currentProcess = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcess) && IsAtOrBelow(ResolvePath(currentProcess), expected))
        {
            throw new InvalidOperationException("The installed broker cannot delete its own running image.");
        }

        if (Directory.Exists(expected))
        {
            DeleteDirectoryWithoutFollowingReparsePoints(expected);
        }

        return ValueTask.CompletedTask;
    }

    private string ValidateInstallDirectory(string path)
    {
        var expected = ResolvePath(Path.Combine(
            ProgramFilesPath,
            "UsbWebLauncher",
            "NativePromptBroker"));
        var resolved = ResolvePath(path);
        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to modify an unexpected broker directory.");
        }

        return expected;
    }

    private void DeleteFileIfPresent(string path)
    {
        var resolved = ValidateBackupFilePath(path);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }
    }

    private string ValidateBackupFilePath(string path)
    {
        var resolved = ResolvePath(path);
        var expectedDirectory = ValidateInstallDirectory(Path.GetDirectoryName(resolved) ?? string.Empty);
        if (!string.Equals(
                Path.GetDirectoryName(resolved),
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(path), resolved, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected broker backup file.");
        }

        return resolved;
    }

    private static ValueTask RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        bool allowNonZeroExitCode,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "schtasks.exe");
        return RunProcessAsync(executable, arguments, allowNonZeroExitCode, cancellationToken);
    }

    private static async ValueTask RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool allowNonZeroExitCode,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start the Windows broker installation helper.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        if (!allowNonZeroExitCode && process.ExitCode != 0)
        {
            throw new InvalidOperationException("The Windows broker installation helper failed.");
        }
    }

    private static bool IsAtOrBelow(string candidate, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   normalizedDirectory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenTask(
        string taskName,
        out object? service,
        out object? folder,
        out object? task)
    {
        service = null;
        folder = null;
        task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true) ??
                throw new InvalidOperationException("Windows Task Scheduler service is unavailable.");
            service = Activator.CreateInstance(serviceType) ??
                throw new InvalidOperationException("Windows Task Scheduler service could not be created.");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(taskName);
            return true;
        }
        catch (Exception exception) when (IsTaskNotFoundHResult(exception.HResult))
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
            service = null;
            folder = null;
            task = null;
            return false;
        }
        catch
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
            service = null;
            folder = null;
            task = null;
            throw;
        }
    }

    internal static bool IsTaskNotFoundHResult(int hresult) =>
        unchecked((uint)hresult) == 0x80070002;

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    internal static string ResolveCurrentInteractiveUser()
    {
        var sessionId = checked((uint)Process.GetCurrentProcess().SessionId);
        if (!TryQuerySessionInt32(sessionId, WtsConnectState, out var state) || state != WtsActive ||
            !TryQuerySessionString(sessionId, WtsUserName, out var user) ||
            string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("The current interactive Windows session could not be identified.");
        }

        _ = TryQuerySessionString(sessionId, WtsDomainName, out var domain);
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }

    private static bool TryQuerySessionString(uint sessionId, int informationClass, out string value)
    {
        value = string.Empty;
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                informationClass,
                out var buffer,
                out var bytes) ||
            buffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            value = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return bytes >= sizeof(char);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool TryQuerySessionInt32(uint sessionId, int informationClass, out int value)
    {
        value = 0;
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                informationClass,
                out var buffer,
                out var bytes) ||
            buffer == IntPtr.Zero ||
            bytes < sizeof(int))
        {
            return false;
        }

        try
        {
            value = Marshal.ReadInt32(buffer);
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static void RejectReparsePointComponents(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidDataException("Protected broker path has no local root.");
        }

        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Protected broker paths cannot contain reparse points.");
            }
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to recursively delete a reparse point.");
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to delete a reparse-point file from the broker directory.");
            }

            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            DeleteDirectoryWithoutFollowingReparsePoints(child);
        }

        Directory.Delete(directory, recursive: false);
    }

    private static string NormalizeFinalPath(string finalPath)
    {
        const string localPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var normalized = finalPath.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + finalPath[uncPrefix.Length..]
            : finalPath.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                ? finalPath[localPrefix.Length..]
                : finalPath;
        return Path.GetFullPath(normalized).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        int filePathLength,
        uint flags);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        int informationClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

internal interface INativePromptInteractiveUserResolver
{
    string ResolveCurrentSessionUser();
}

internal sealed class WindowsNativePromptInteractiveUserResolver : INativePromptInteractiveUserResolver
{
    public string ResolveCurrentSessionUser() =>
        WindowsNativePromptBrokerInstallationApi.ResolveCurrentInteractiveUser();
}
