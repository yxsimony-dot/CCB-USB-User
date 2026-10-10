using System.Security.Cryptography;
using System.Text.Json;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptBrokerInstallerTests
{
    [Fact]
    public async Task ProductionPolicyValidation_BindsExactPackagedClientPathAndCodeHashes()
    {
        var packageDirectory = Path.Combine(
            Path.GetTempPath(),
            $"native-prompt-package-{Guid.NewGuid():N}");
        var brokerDirectory = Path.Combine(packageDirectory, "broker");
        Directory.CreateDirectory(brokerDirectory);
        try
        {
            var clientFiles = new Dictionary<string, byte[]>
            {
                [NativePromptBrokerTrustedClient.ProcessFileName] = RandomNumberGenerator.GetBytes(64),
                [NativePromptBrokerTrustedClient.ApplicationAssemblyFileName] = RandomNumberGenerator.GetBytes(64),
                [NativePromptBrokerTrustedClient.CoreAssemblyFileName] = RandomNumberGenerator.GetBytes(64),
                [NativePromptBrokerTrustedClient.WindowsAssemblyFileName] = RandomNumberGenerator.GetBytes(64)
            };
            foreach (var (name, contents) in clientFiles)
            {
                File.WriteAllBytes(Path.Combine(packageDirectory, name), contents);
            }

            var policyPath = Path.Combine(
                brokerDirectory,
                WindowsNativePromptBrokerInstaller.PolicyFileName);
            await File.WriteAllBytesAsync(
                policyPath,
                JsonSerializer.SerializeToUtf8Bytes(new NativePromptBrokerPolicyDocument(
                    NativePromptProtocol.ProtocolVersion,
                    new string('A', 64),
                    ValidPrompt())));
            var api = new WindowsNativePromptBrokerInstallationApi(
                new StubHealthClient(),
                new StubInteractiveUserResolver("SESSION\\User"));

            var installedJson = await api.ReadValidatedPolicyAsync(
                policyPath,
                CancellationToken.None);

            Assert.NotNull(installedJson);
            var installed = NativePromptBrokerPolicyFile.Parse(installedJson);
            Assert.True(installed.IsValid);
            var trusted = Assert.IsType<NativePromptBrokerTrustedClient>(installed.TrustedClient);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(packageDirectory, NativePromptBrokerTrustedClient.ProcessFileName)),
                trusted.ProcessPath,
                ignoreCase: true);
            Assert.Equal(Hash(clientFiles[NativePromptBrokerTrustedClient.ProcessFileName]), trusted.ProcessSha256);
            Assert.Equal(
                Hash(clientFiles[NativePromptBrokerTrustedClient.ApplicationAssemblyFileName]),
                trusted.ApplicationAssemblySha256);
            Assert.Equal(Hash(clientFiles[NativePromptBrokerTrustedClient.CoreAssemblyFileName]), trusted.CoreAssemblySha256);
            Assert.Equal(
                Hash(clientFiles[NativePromptBrokerTrustedClient.WindowsAssemblyFileName]),
                trusted.WindowsAssemblySha256);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionUpdateBackup_PreservesOriginalAcrossRetryAndCanRestoreThenFinalize(bool hadOwner)
    {
        var programFiles = Path.Combine(
            Path.GetTempPath(),
            $"native-prompt-program-files-{Guid.NewGuid():N}");
        var installDirectory = Path.Combine(programFiles, "UsbWebLauncher", "NativePromptBroker");
        Directory.CreateDirectory(installDirectory);
        var brokerPath = Path.Combine(
            installDirectory,
            WindowsNativePromptBrokerInstaller.BrokerFileName);
        var policyPath = Path.Combine(
            installDirectory,
            WindowsNativePromptBrokerInstaller.PolicyFileName);
        var ownerPath = Path.Combine(installDirectory, WindowsNativePromptBrokerInstallationApi.OwnerFileName);
        var oldBroker = RandomNumberGenerator.GetBytes(64);
        var oldPolicy = RandomNumberGenerator.GetBytes(64);
        try
        {
            await File.WriteAllBytesAsync(brokerPath, oldBroker);
            await File.WriteAllBytesAsync(policyPath, oldPolicy);
            if (hadOwner) await File.WriteAllTextAsync(ownerPath, "original-owner");
            var api = new WindowsNativePromptBrokerInstallationApi(
                new StubHealthClient(),
                new StubInteractiveUserResolver("SESSION\\User"),
                programFiles);

            await api.PrepareUpdateBackupAsync(installDirectory, CancellationToken.None);
            await File.WriteAllBytesAsync(brokerPath, RandomNumberGenerator.GetBytes(64));
            await File.WriteAllBytesAsync(policyPath, RandomNumberGenerator.GetBytes(64));
            await File.WriteAllTextAsync(ownerPath, "replacement-owner");
            await api.PrepareUpdateBackupAsync(installDirectory, CancellationToken.None);
            await api.RestoreUpdateBackupAsync(installDirectory, CancellationToken.None);

            Assert.Equal(oldBroker, await File.ReadAllBytesAsync(brokerPath));
            Assert.Equal(oldPolicy, await File.ReadAllBytesAsync(policyPath));
            Assert.Equal(hadOwner ? 3 : 2, Directory.GetFiles(installDirectory, "*.update-backup").Length);
            if (hadOwner) Assert.Equal("original-owner", await File.ReadAllTextAsync(ownerPath));
            else Assert.False(File.Exists(ownerPath));

            await api.DeleteUpdateBackupAsync(installDirectory, CancellationToken.None);
            Assert.Empty(Directory.GetFiles(installDirectory, "*.update-backup"));
        }
        finally
        {
            Array.Clear(oldBroker, 0, oldBroker.Length);
            Array.Clear(oldPolicy, 0, oldPolicy.Length);
            Directory.Delete(programFiles, recursive: true);
        }
    }

    [Fact]
    public void TaskSchedulerClassification_OnlyFileNotFoundIsBenign()
    {
        Assert.True(WindowsNativePromptBrokerInstallationApi.IsTaskNotFoundHResult(
            unchecked((int)0x80070002)));
        Assert.False(WindowsNativePromptBrokerInstallationApi.IsTaskNotFoundHResult(
            unchecked((int)0x8004130F)));
    }

    [Fact]
    public async Task TaskStopWaiter_DoesNotReturnWhileScheduledTaskIsStillRunning()
    {
        var states = new Queue<int>([4, 4, 3]);
        var reads = 0;

        await WindowsNativePromptBrokerInstallationApi.WaitForTaskStoppedAsync(
            () =>
            {
                reads++;
                return states.Dequeue();
            },
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task TaskStopWaiter_DoesNotReturnUntilBrokerImageIsReleased()
    {
        var states = new Queue<int>([3, 3, 3]);
        var imageReleased = new Queue<bool>([false, false, true]);
        var releaseReads = 0;

        await WindowsNativePromptBrokerInstallationApi.WaitForTaskStoppedAsync(
            () => states.Dequeue(),
            () =>
            {
                releaseReads++;
                return imageReleased.Dequeue();
            },
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(3, releaseReads);
    }

    [Fact]
    public async Task RegisterTaskAsync_PreservesSeparateExecutableAndArgumentFields()
    {
        var registrar = new RecordingScheduledTaskRegistrar();
        var api = new WindowsNativePromptBrokerInstallationApi(
            new StubHealthClient(),
            new StubInteractiveUserResolver(@"SESSION\User"),
            scheduledTaskRegistrar: registrar);
        var definition = new BrokerScheduledTaskDefinition(
            "UsbWebLauncher Native Prompt Broker",
            @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe",
            "--server",
            @"SESSION\User",
            RunOnlyForInteractiveUser: true,
            BrokerRunLevel.Highest);

        await api.RegisterTaskAsync(definition, CancellationToken.None);

        var registered = Assert.IsType<BrokerScheduledTaskDefinition>(registrar.Definition);
        Assert.Equal(
            @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe",
            registered.ExecutablePath);
        Assert.Equal("--server", registered.Arguments);
    }

    [Fact]
    public void ProductionApi_UsesInteractiveSessionResolverInsteadOfElevatedIdentity()
    {
        var api = new WindowsNativePromptBrokerInstallationApi(
            new StubHealthClient(),
            new StubInteractiveUserResolver("SESSION\\User"));

        Assert.Equal("SESSION\\User", api.CurrentInteractiveUser);
    }

    [Theory]
    [InlineData("--server", null, 0)]
    [InlineData("--install", PolicySource, 1)]
    [InlineData("--uninstall", null, 2)]
    [InlineData("--backup", null, 3)]
    [InlineData("--restore", null, 4)]
    [InlineData("--commit", null, 5)]
    public void CommandParser_AcceptsOnlyFixedNonSecretForms(
        string verb,
        string? argument,
        int expectedKind)
    {
        var args = argument is null ? new[] { verb } : new[] { verb, argument };

        var accepted = NativePromptBrokerCommand.TryParse(args, out var command);

        Assert.True(accepted);
        Assert.Equal((NativePromptBrokerCommandKind)expectedKind, command.Kind);
        Assert.Equal(argument, command.PolicyPath);
    }

    [Theory]
    [InlineData()]
    [InlineData("--server", "unexpected")]
    [InlineData("--install")]
    [InlineData("--install", "relative.json")]
    [InlineData("--pin", "secret")]
    [InlineData("--unknown")]
    public void CommandParser_RejectsUnknownOrSecretBearingForms(params string[] args)
    {
        Assert.False(NativePromptBrokerCommand.TryParse(args, out _));
    }

    [Fact]
    public async Task InstallAsync_UsesProtectedProgramFilesPathAndHighestInteractiveTask()
    {
        var api = new RecordingInstallationApi { TaskExists = false };
        var installer = new WindowsNativePromptBrokerInstaller(api);

        var result = await installer.InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.Success, result.Status);
        Assert.Equal(ExpectedDirectory, api.ProtectedDirectory);
        Assert.NotNull(api.TaskDefinition);
        Assert.True(api.TaskDefinition!.RunOnlyForInteractiveUser);
        Assert.Equal(BrokerRunLevel.Highest, api.TaskDefinition.RunLevel);
        Assert.Equal("--server", api.TaskDefinition.Arguments);
        Assert.Equal(Path.Combine(ExpectedDirectory, WindowsNativePromptBrokerInstaller.BrokerFileName), api.TaskDefinition.ExecutablePath);
        Assert.Equal(["ValidatePolicy", "CreateDirectory", "CopyBroker", "CopyPolicy", "Protect", "Register", "Start"], api.Events);
        Assert.Equal(api.ValidatedPolicyBytes, api.WrittenPolicyBytes);
    }

    [Fact]
    public async Task InstallAsync_ExistingBrokerKeepsBackupUntilAuthenticatedHealthFinalizesUpdate()
    {
        var api = new RecordingInstallationApi { TaskExists = true };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.Success, result.Status);
        Assert.Equal(
            [
                "ValidatePolicy", "Stop", "Backup", "CreateDirectory", "CopyBroker",
                "CopyPolicy", "Protect", "Register", "Start"
            ],
            api.Events);
        Assert.True(api.BackupExists);
    }

    [Fact]
    public async Task InstallAsync_RetryAfterUnhealthyUpdateReusesTheOriginalBackup()
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = true,
            BackupExists = true
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.Success, result.Status);
        Assert.Contains("ReuseBackup", api.Events);
        Assert.DoesNotContain("Backup", api.Events);
        Assert.True(api.BackupExists);
    }

    [Fact]
    public async Task FinalizeUpdateAsync_RemovesBackupOnlyAfterBrokerHealthHandshake()
    {
        var api = new RecordingInstallationApi { BackupExists = true };
        var installer = new WindowsNativePromptBrokerInstaller(api);

        await installer.FinalizeUpdateAsync();

        Assert.Equal(["DeleteBackup"], api.Events);
        Assert.False(api.BackupExists);
    }

    [Fact]
    public async Task InstallAsync_UpdateFailureRestoresAndRestartsPreviousInstallation()
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = true,
            FailingEvent = "CopyPolicy"
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal((NativePromptBrokerInstallationStatus)14, result.Status);
        Assert.Equal(
            [
                "ValidatePolicy", "Stop", "Backup", "CreateDirectory", "CopyBroker",
                "CopyPolicy", "Stop", "Restore", "Protect", "Register", "Start", "DeleteBackup"
            ],
            api.Events);
        Assert.DoesNotContain("DeleteTask", api.Events);
        Assert.DoesNotContain("DeleteDirectory", api.Events);
        Assert.True(api.RestoredPreviousInstallation);
        Assert.False(api.BackupExists);
    }

    [Fact]
    public async Task InstallAsync_ExistingInstallationPreparationFailureUsesDistinctStatus()
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = true,
            FailingEvent = "Backup"
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal((NativePromptBrokerInstallationStatus)15, result.Status);
    }

    [Fact]
    public async Task InstallAsync_UpdateRestoreFailureReportsIncompleteRollbackWithoutDeletingPreviousFiles()
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = true,
            FailingEvent = "CopyPolicy",
            CleanupFailingEvent = "Restore"
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.RollbackFailed, result.Status);
        Assert.DoesNotContain("DeleteTask", api.Events);
        Assert.DoesNotContain("DeleteDirectory", api.Events);
    }

    [Fact]
    public async Task InstallAsync_WhenNotElevatedPerformsNoMutation()
    {
        var api = new RecordingInstallationApi { IsHighIntegrity = false };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.NotElevated, result.Status);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task InstallAsync_InvalidPolicyIsRejectedBeforeFilesystemMutation()
    {
        var api = new RecordingInstallationApi { PolicyIsValid = false };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidPackage, result.Status);
        Assert.Equal(["ValidatePolicy"], api.Events);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("DeleteTask")]
    [InlineData("DeleteDirectory")]
    public async Task InstallAsync_WhenRollbackCleanupFailsReportsIncompleteRollback(
        string cleanupFailure)
    {
        var api = new RecordingInstallationApi
        {
            FailingEvent = "Start",
            CleanupFailingEvent = cleanupFailure
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal(NativePromptBrokerInstallationStatus.RollbackFailed, result.Status);
    }

    [Theory]
    [InlineData("InspectTask", 12)]
    [InlineData("CreateDirectory", 13)]
    [InlineData("CopyBroker", (int)NativePromptBrokerInstallationStatus.CopyFailed)]
    [InlineData("CopyPolicy", 14)]
    [InlineData("Protect", (int)NativePromptBrokerInstallationStatus.ProtectionFailed)]
    [InlineData("Register", (int)NativePromptBrokerInstallationStatus.TaskRegistrationFailed)]
    [InlineData("Start", (int)NativePromptBrokerInstallationStatus.TaskStartFailed)]
    public async Task InstallAsync_FailureRollsBackTaskAndExactDirectory(
        string failingEvent,
        int expectedStatus)
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = false,
            FailingEvent = failingEvent
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).InstallAsync(Package());

        Assert.Equal((NativePromptBrokerInstallationStatus)expectedStatus, result.Status);
        if (failingEvent == "InspectTask")
        {
            Assert.DoesNotContain("DeleteDirectory", api.Events);
            Assert.Null(api.DeletedDirectory);
            return;
        }

        if (failingEvent == "Start")
        {
            Assert.Equal(["Stop", "DeleteTask", "DeleteDirectory"], api.Events.TakeLast(3));
        }
        else
        {
            Assert.Equal("DeleteDirectory", api.Events[^1]);
        }
        Assert.Equal(ExpectedDirectory, api.DeletedDirectory);
    }

    [Fact]
    public async Task CheckHealthAsync_IsASeparateIdentityBoundStepForTheCallingApplication()
    {
        var api = new RecordingInstallationApi { Health = new(true, NativePromptProtocol.ProtocolVersion) };
        var installer = new WindowsNativePromptBrokerInstaller(api);

        var health = await installer.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(NativePromptProtocol.ProtocolVersion, health.ProtocolVersion);
        Assert.Equal(["Health"], api.Events);
    }

    [Fact]
    public async Task InstallAsync_RejectsRelativeOrInstallDirectorySourcePaths()
    {
        var api = new RecordingInstallationApi();
        var installer = new WindowsNativePromptBrokerInstaller(api);

        var relative = await installer.InstallAsync(new("broker.exe", PolicySource));
        var selfCopy = await installer.InstallAsync(new(
            Path.Combine(ExpectedDirectory, WindowsNativePromptBrokerInstaller.BrokerFileName),
            PolicySource));

        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidPackage, relative.Status);
        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidPackage, selfCopy.Status);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task UninstallAsync_StopsTaskAndDeletesOnlyValidatedExactDirectory()
    {
        var api = new RecordingInstallationApi { TaskExists = true };

        var result = await new WindowsNativePromptBrokerInstaller(api).UninstallAsync();

        Assert.Equal(NativePromptBrokerInstallationStatus.Success, result.Status);
        Assert.Equal(["Stop", "DeleteTask", "DeleteDirectory"], api.Events);
        Assert.Equal(ExpectedDirectory, api.DeletedDirectory);
    }

    [Fact]
    public async Task UninstallAsync_RefusesUnexpectedResolvedInstallDirectory()
    {
        var api = new RecordingInstallationApi
        {
            ResolvedDirectory = @"D:\Unexpected"
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).UninstallAsync();

        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidInstallPath, result.Status);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task UninstallAsync_RefusesToRunFromInstalledBrokerBeforeTaskMutation()
    {
        var api = new RecordingInstallationApi
        {
            CurrentProcessPath = Path.Combine(
                ExpectedDirectory,
                WindowsNativePromptBrokerInstaller.BrokerFileName)
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).UninstallAsync();

        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidInstallPath, result.Status);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task UninstallAsync_RejectsExternalImageThatDoesNotMatchProtectedInstalledBroker()
    {
        var api = new RecordingInstallationApi { IsAuthorizedBundledCopy = false };

        var result = await new WindowsNativePromptBrokerInstaller(api).UninstallAsync();

        Assert.Equal(NativePromptBrokerInstallationStatus.InvalidInstallPath, result.Status);
        Assert.Empty(api.Events);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("DeleteTask")]
    [InlineData("DeleteDirectory")]
    public async Task UninstallAsync_CleanupFailureIsNotReportedAsSuccess(
        string cleanupFailure)
    {
        var api = new RecordingInstallationApi
        {
            TaskExists = true,
            CleanupFailingEvent = cleanupFailure
        };

        var result = await new WindowsNativePromptBrokerInstaller(api).UninstallAsync();

        Assert.Equal(NativePromptBrokerInstallationStatus.UninstallFailed, result.Status);
    }

    [Theory]
    [InlineData(NativePromptBrokerInstallationStatus.OwnershipMismatch)]
    [InlineData(NativePromptBrokerInstallationStatus.OwnershipInspectionFailed)]
    public async Task OwnershipFailure_PreventsAllInstallAndUninstallMutations(NativePromptBrokerInstallationStatus status)
    {
        var api = new RecordingInstallationApi { OwnershipStatus = status };
        var installer = new WindowsNativePromptBrokerInstaller(api);
        Assert.Equal(status, (await installer.InstallAsync(Package())).Status);
        Assert.Equal(status, (await installer.UninstallAsync()).Status);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task PrepareInstallation_DoesNotRequireElevationOrMutateMachine()
    {
        var api = new RecordingInstallationApi { IsHighIntegrity = false };
        Assert.True((await new WindowsNativePromptBrokerInstaller(api).PrepareInstallationAsync()).IsSuccess);
        Assert.Empty(api.Events);
    }

    [Fact]
    public async Task OuterSnapshotCommands_RequireElevationBeforeInvokingApi()
    {
        var api = new RecordingInstallationApi { IsHighIntegrity = false };
        var installer = new WindowsNativePromptBrokerInstaller(api);
        Assert.Equal(NativePromptBrokerInstallationStatus.NotElevated, (await installer.BackupInstallationAsync()).Status);
        Assert.Equal(NativePromptBrokerInstallationStatus.NotElevated, (await installer.RestoreInstallationAsync()).Status);
        Assert.Equal(NativePromptBrokerInstallationStatus.NotElevated, (await installer.CommitInstallationAsync()).Status);
        Assert.Empty(api.Events);
    }

    private const string ProgramFiles = @"D:\Program Files";
    private const string BrokerSource = @"D:\Bundle\UsbWebLauncher.NativePromptBroker.exe";
    private const string PolicySource = @"D:\Bundle\native-prompt-policy.json";
    private static readonly string ExpectedDirectory = Path.Combine(
        ProgramFiles,
        "UsbWebLauncher",
        "NativePromptBroker");

    private static NativePromptBrokerPackage Package() => new(BrokerSource, PolicySource);

    private static string Hash(byte[] contents) =>
        Convert.ToHexString(SHA256.HashData(contents));

    private static ClientCertificateNativePromptOptions ValidPrompt() => new(
        true,
        WindowsNativePromptBrokerInstaller.BrokerPipeName,
        "policy-v1",
        @"C:\Program Files (x86)\Vendor\Prompt.exe",
        new string('A', 64),
        new string('B', 40),
        "Vendor prompt",
        "#32770",
        ["Shell Embedding", "Internet Explorer_Server"],
        1000);

    private sealed class RecordingInstallationApi : INativePromptBrokerInstallationApi
    {
        public NativePromptBrokerInstallationStatus OwnershipStatus { get; set; } = NativePromptBrokerInstallationStatus.Success;
        public ValueTask<NativePromptBrokerInstallationStatus> CheckOwnershipAsync(CancellationToken cancellationToken) => ValueTask.FromResult(OwnershipStatus);
        public ValueTask WriteOwnerAsync(byte[] policy, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExecuteTransactionAsync(NativePromptBrokerCommandKind command, CancellationToken cancellationToken)
        {
            Events.Add(command.ToString());
            return ValueTask.CompletedTask;
        }
        public bool IsHighIntegrity { get; set; } = true;
        public string ProgramFilesPath => ProgramFiles;
        public string CurrentInteractiveUser => "DOMAIN\\User";
        public string CurrentProcessPath { get; set; } = BrokerSource;
        public string? FailingEvent { get; set; }
        public string? CleanupFailingEvent { get; set; }
        public string? ResolvedDirectory { get; set; }
        public bool PolicyIsValid { get; set; } = true;
        public bool IsAuthorizedBundledCopy { get; set; } = true;
        public bool TaskExists { get; set; }
        public bool BackupExists { get; set; }
        public bool RestoredPreviousInstallation { get; private set; }
        public byte[] ValidatedPolicyBytes { get; } = "validated-policy"u8.ToArray();
        public NativePromptBrokerHealthResult Health { get; set; } = new(true, NativePromptProtocol.ProtocolVersion);
        public List<string> Events { get; } = [];
        public string? ProtectedDirectory { get; private set; }
        public string? DeletedDirectory { get; private set; }
        public BrokerScheduledTaskDefinition? TaskDefinition { get; private set; }
        public byte[]? WrittenPolicyBytes { get; private set; }

        public string ResolvePath(string path) =>
            path == ExpectedDirectory && ResolvedDirectory is not null ? ResolvedDirectory : Path.GetFullPath(path);

        public ValueTask<byte[]?> ReadValidatedPolicyAsync(string path, CancellationToken cancellationToken)
        {
            Events.Add("ValidatePolicy");
            return ValueTask.FromResult<byte[]?>(
                PolicyIsValid ? ValidatedPolicyBytes.ToArray() : null);
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Record("CreateDirectory");
            return ValueTask.CompletedTask;
        }

        public ValueTask CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Record("CopyBroker");
            return ValueTask.CompletedTask;
        }

        public ValueTask PrepareUpdateBackupAsync(string installDirectory, CancellationToken cancellationToken)
        {
            if (BackupExists)
            {
                Events.Add("ReuseBackup");
                return ValueTask.CompletedTask;
            }

            Record("Backup");
            BackupExists = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreUpdateBackupAsync(string installDirectory, CancellationToken cancellationToken)
        {
            Events.Add("Restore");
            ThrowCleanupFailure("Restore");
            RestoredPreviousInstallation = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteUpdateBackupAsync(string installDirectory, CancellationToken cancellationToken)
        {
            Events.Add("DeleteBackup");
            ThrowCleanupFailure("DeleteBackup");
            BackupExists = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask WritePolicyFileAsync(
            byte[] validatedPolicy,
            string destination,
            CancellationToken cancellationToken)
        {
            WrittenPolicyBytes = validatedPolicy.ToArray();
            Record("CopyPolicy");
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TaskExistsAsync(string taskName, CancellationToken cancellationToken)
        {
            if (FailingEvent == "InspectTask")
            {
                FailingEvent = null;
                throw new IOException("Injected InspectTask failure.");
            }

            return ValueTask.FromResult(TaskExists);
        }

        public ValueTask<bool> IsSameBinaryIdentityAsync(
            string firstPath,
            string secondPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(IsAuthorizedBundledCopy);

        public ValueTask ProtectDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            ProtectedDirectory = path;
            Record("Protect");
            return ValueTask.CompletedTask;
        }

        public ValueTask RegisterTaskAsync(BrokerScheduledTaskDefinition definition, CancellationToken cancellationToken)
        {
            TaskDefinition = definition;
            Record("Register");
            TaskExists = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StartTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            Record("Start");
            return ValueTask.CompletedTask;
        }

        public ValueTask<NativePromptBrokerHealthResult> CheckHealthAsync(string pipeName, CancellationToken cancellationToken)
        {
            Record("Health");
            return ValueTask.FromResult(Health);
        }

        public ValueTask StopTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            Events.Add("Stop");
            ThrowCleanupFailure("Stop");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteTaskAsync(string taskName, CancellationToken cancellationToken)
        {
            Events.Add("DeleteTask");
            ThrowCleanupFailure("DeleteTask");
            TaskExists = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            DeletedDirectory = path;
            Events.Add("DeleteDirectory");
            ThrowCleanupFailure("DeleteDirectory");
            return ValueTask.CompletedTask;
        }

        private void Record(string name)
        {
            Events.Add(name);
            if (FailingEvent == name)
            {
                FailingEvent = null;
                throw new IOException($"Injected {name} failure.");
            }
        }

        private void ThrowCleanupFailure(string name)
        {
            if (CleanupFailingEvent == name)
            {
                throw new IOException($"Injected cleanup {name} failure.");
            }
        }
    }

    private sealed class StubHealthClient : INativePromptBrokerHealthClient
    {
        public ValueTask<NativePromptBrokerHealthResult> CheckAsync(
            string pipeName,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativePromptBrokerHealthResult(
                true,
                NativePromptProtocol.ProtocolVersion));
    }

    private sealed class StubInteractiveUserResolver(string user)
        : INativePromptInteractiveUserResolver
    {
        public string ResolveCurrentSessionUser() => user;
    }

    private sealed class RecordingScheduledTaskRegistrar : INativePromptScheduledTaskRegistrar
    {
        public BrokerScheduledTaskDefinition? Definition { get; private set; }

        public ValueTask RegisterAsync(
            BrokerScheduledTaskDefinition definition,
            CancellationToken cancellationToken)
        {
            Definition = definition;
            return ValueTask.CompletedTask;
        }
    }
}
