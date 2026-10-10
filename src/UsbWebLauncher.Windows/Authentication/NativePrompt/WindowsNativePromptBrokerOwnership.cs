using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal sealed record BrokerOwnerRecord(int SchemaVersion, string ProductId, string OwnerSid, string ApplicationPath);
internal sealed record BrokerTransactionRecord(int SchemaVersion, string ProductId, string OwnerSid,
    bool HadDirectory, string? TaskXml, bool TaskWasRunning, Dictionary<string, string> FileHashes);

internal static class BrokerOwnershipRules
{
    internal const string ProductId = "usb-web-launcher";
    internal static NativePromptBrokerInstallationStatus Evaluate(string callerSid, string sessionSid,
        BrokerOwnerRecord? owner, bool hasDirectory, string? taskSid, bool validLegacyPolicy)
    {
        if (string.IsNullOrWhiteSpace(callerSid) || callerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
            !string.Equals(callerSid, sessionSid, StringComparison.Ordinal))
            return NativePromptBrokerInstallationStatus.OwnershipMismatch;
        if (taskSid is not null && !string.Equals(taskSid, callerSid, StringComparison.Ordinal))
            return NativePromptBrokerInstallationStatus.OwnershipMismatch;
        if (owner is not null)
            return owner.SchemaVersion == 1 && owner.ProductId == ProductId && owner.OwnerSid == callerSid &&
                Path.IsPathFullyQualified(owner.ApplicationPath)
                ? NativePromptBrokerInstallationStatus.Success : NativePromptBrokerInstallationStatus.OwnershipMismatch;
        if (hasDirectory && (taskSid is null || !validLegacyPolicy))
            return NativePromptBrokerInstallationStatus.OwnershipInspectionFailed;
        if (!hasDirectory && taskSid is not null)
            return NativePromptBrokerInstallationStatus.OwnershipInspectionFailed;
        return NativePromptBrokerInstallationStatus.Success;
    }
}

internal sealed partial class WindowsNativePromptBrokerInstallationApi
{
    internal const string OwnerFileName = "installation-owner.json";
    private string InstallDirectory => ResolvePath(Path.Combine(ProgramFilesPath, "UsbWebLauncher", "NativePromptBroker"));
    private string TransactionDirectory => ResolvePath(Path.Combine(ProgramFilesPath, "UsbWebLauncher", "NativePromptBroker.installer-transaction"));
    private static readonly string[] SnapshotFiles = [WindowsNativePromptBrokerInstaller.BrokerFileName,
        WindowsNativePromptBrokerInstaller.PolicyFileName, OwnerFileName];

    internal static string ResolveAccountSid(string account) => account.StartsWith("S-1-", StringComparison.Ordinal)
        ? new SecurityIdentifier(account).Value
        : ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;

    private string RequireOriginalUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? string.Empty;
        if (BrokerOwnershipRules.Evaluate(sid, ResolveAccountSid(CurrentInteractiveUser), null, false, null, false)
            != NativePromptBrokerInstallationStatus.Success)
            throw new UnauthorizedAccessException("Installation requires the original interactive user's own elevation.");
        return sid;
    }

    public ValueTask<NativePromptBrokerInstallationStatus> CheckOwnershipAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? string.Empty;
        var sessionSid = ResolveAccountSid(CurrentInteractiveUser);
        var basic = BrokerOwnershipRules.Evaluate(sid, sessionSid, null, false, null, false);
        if (basic != NativePromptBrokerInstallationStatus.Success) return ValueTask.FromResult(basic);
        var task = ReadTaskSnapshot();
        string directory;
        try { directory = InstallDirectory; }
        catch (IOException exception) when (!IsHighIntegrity && (exception.HResult & 0xffff) == 5 && task.Sid == sid)
        {
            // Legacy admin-only directories cannot be resolved with an ordinary token.
            // The elevated mutation repeats the complete path/policy/ownership inspection.
            return ValueTask.FromResult(NativePromptBrokerInstallationStatus.Success);
        }
        BrokerOwnerRecord? owner = null;
        var validLegacy = false;
        var ownerPath = ResolvePath(Path.Combine(directory, OwnerFileName));
        if (File.Exists(ownerPath))
        {
            RequireProtectedFile(ownerPath);
            owner = JsonSerializer.Deserialize<BrokerOwnerRecord>(ReadBounded(ownerPath))
                ?? throw new InvalidDataException("Broker ownership record is invalid.");
        }
        else if (Directory.Exists(directory) && task.Sid == sid)
        {
            // Older protected directories may grant only SYSTEM/Administrators access. The
            // task principal remains readable by its original interactive user before UAC.
            try
            {
                var policyPath = ResolvePath(Path.Combine(directory, WindowsNativePromptBrokerInstaller.PolicyFileName));
                var policy = NativePromptBrokerPolicyFile.LoadInstalled(policyPath);
                validLegacy = policy.TrustedClient is { IsValid: true };
            }
            catch (UnauthorizedAccessException) when (!IsHighIntegrity) { validLegacy = true; }
        }
        return ValueTask.FromResult(BrokerOwnershipRules.Evaluate(sid, sessionSid, owner,
            Directory.Exists(directory), task.Sid, validLegacy));
    }

    public async ValueTask WriteOwnerAsync(byte[] policy, CancellationToken cancellationToken)
    {
        var sid = RequireOriginalUser();
        var installed = NativePromptBrokerPolicyFile.Parse(policy);
        var client = installed.TrustedClient ?? throw new InvalidDataException("Bound client is required.");
        var record = new BrokerOwnerRecord(1, BrokerOwnershipRules.ProductId, sid, client.ProcessPath);
        var path = ResolvePath(Path.Combine(InstallDirectory, OwnerFileName));
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(record), cancellationToken).ConfigureAwait(false);
        ProtectFile(path, sid);
    }

    public async ValueTask ExecuteTransactionAsync(NativePromptBrokerCommandKind command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHighIntegrity) throw new UnauthorizedAccessException("Elevation required.");
        var sid = RequireOriginalUser();
        var directory = InstallDirectory;
        var transaction = TransactionDirectory;
        var recordPath = ResolvePath(Path.Combine(transaction, "transaction.json"));
        if (command == NativePromptBrokerCommandKind.Backup)
        {
            if (await CheckOwnershipAsync(cancellationToken).ConfigureAwait(false) != NativePromptBrokerInstallationStatus.Success)
                throw new UnauthorizedAccessException("Broker belongs to another user or cannot be verified.");
            if (Directory.Exists(transaction))
                throw new InvalidOperationException("An unfinished broker transaction requires restore or commit before a new backup.");
            var task = ReadTaskSnapshot();
            Directory.CreateDirectory(transaction);
            try
            {
                ProtectTransactionDirectory(transaction, sid);
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var name in SnapshotFiles)
                {
                    var source = ResolvePath(Path.Combine(directory, name));
                    if (!File.Exists(source)) continue;
                    var target = ResolvePath(Path.Combine(transaction, name));
                    File.Copy(source, target, overwrite: false);
                    ProtectFile(target, sid);
                    hashes.Add(name, await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false));
                }
                var record = new BrokerTransactionRecord(1, BrokerOwnershipRules.ProductId, sid,
                    Directory.Exists(directory), task.Xml, task.Running, hashes);
                // The completed manifest is written last. Incomplete snapshots are never restored.
                var pendingRecordPath = ResolvePath(Path.Combine(transaction, "transaction.next.json"));
                await File.WriteAllBytesAsync(pendingRecordPath, JsonSerializer.SerializeToUtf8Bytes(record), cancellationToken).ConfigureAwait(false);
                ProtectFile(pendingRecordPath, sid);
                PublishTransactionManifest(pendingRecordPath, recordPath);
            }
            catch
            {
                // This invocation created the snapshot and has not touched the active broker.
                // Preserve it only if cleanup itself fails, so the next run fails visibly.
                DeleteDirectoryWithoutFollowingReparsePoints(transaction);
                throw;
            }
            return;
        }

        if (!Directory.Exists(transaction))
        {
            await CompleteAbsentTransactionAsync(command,
                () => CheckOwnershipAsync(cancellationToken)).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(recordPath))
        {
            // A crash during snapshot creation has not modified the active broker. Only
            // discard this directory when its protected ACL names this original user.
            var acl = new DirectoryInfo(transaction).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!IsSafeIncompleteSnapshotAcl(acl, sid))
                throw new UnauthorizedAccessException("Incomplete snapshot ownership cannot be verified.");
            DeleteDirectoryWithoutFollowingReparsePoints(transaction);
            return;
        }
        RequireProtectedFile(recordPath);
        var snapshot = JsonSerializer.Deserialize<BrokerTransactionRecord>(ReadBounded(recordPath))
            ?? throw new InvalidDataException("Broker transaction record is missing.");
        if (snapshot.SchemaVersion != 1 || snapshot.ProductId != BrokerOwnershipRules.ProductId || snapshot.OwnerSid != sid)
            throw new UnauthorizedAccessException("Broker transaction belongs to another user.");
        var currentTask = ReadTaskSnapshot();
        if (currentTask.Sid is not null && currentTask.Sid != sid)
            throw new UnauthorizedAccessException("Broker task belongs to another user.");
        var currentOwnerPath = ResolvePath(Path.Combine(directory, OwnerFileName));
        if (File.Exists(currentOwnerPath))
        {
            RequireProtectedFile(currentOwnerPath);
            var owner = JsonSerializer.Deserialize<BrokerOwnerRecord>(ReadBounded(currentOwnerPath));
            if (owner?.OwnerSid != sid || owner.ProductId != BrokerOwnershipRules.ProductId)
                throw new UnauthorizedAccessException("Broker ownership changed during the transaction.");
        }
        if (command == NativePromptBrokerCommandKind.Commit)
        {
            DeleteCommittedSnapshot(transaction);
            return;
        }
        foreach (var (name, hash) in snapshot.FileHashes)
        {
            if (!SnapshotFiles.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Unexpected snapshot file.");
            var file = ResolvePath(Path.Combine(transaction, name));
            RequireProtectedFile(file);
            if (await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false) != hash)
                throw new InvalidDataException("Broker snapshot is incomplete or corrupt.");
        }
        if (command != NativePromptBrokerCommandKind.Restore) throw new ArgumentOutOfRangeException(nameof(command));
        // Validate the entire snapshot before changing either the current task or files.
        if (snapshot.HadDirectory && (!snapshot.FileHashes.ContainsKey(SnapshotFiles[0]) || !snapshot.FileHashes.ContainsKey(SnapshotFiles[1])))
            throw new InvalidDataException("Broker snapshot lacks its original payload.");
        if (snapshot.TaskXml is not null && TaskSidFromXml(snapshot.TaskXml) != sid)
            throw new UnauthorizedAccessException("Saved broker task belongs to another user.");
        await StopTaskAsync(WindowsNativePromptBrokerInstaller.ScheduledTaskName, cancellationToken).ConfigureAwait(false);
        await DeleteTaskAsync(WindowsNativePromptBrokerInstaller.ScheduledTaskName, cancellationToken).ConfigureAwait(false);
        await DeleteDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        if (snapshot.HadDirectory)
        {
            Directory.CreateDirectory(directory);
            ProtectTransactionDirectory(directory, sid);
            foreach (var name in snapshot.FileHashes.Keys)
            {
                var target = ResolvePath(Path.Combine(directory, name));
                File.Copy(ResolvePath(Path.Combine(transaction, name)), target, overwrite: false);
                ProtectFile(target, sid);
            }
        }
        if (snapshot.TaskXml is not null)
        {
            RestoreTaskXml(snapshot.TaskXml, sid);
            if (snapshot.TaskWasRunning)
                await StartTaskAsync(WindowsNativePromptBrokerInstaller.ScheduledTaskName, cancellationToken).ConfigureAwait(false);
        }
        // Retain the snapshot until the outer application restore and health check also
        // succeed. Repeating restore remains possible after any later failure.
    }

    internal static void PublishTransactionManifest(string pendingPath, string finalPath)
    {
        // Both files are in the protected snapshot directory. Publishing the fully written
        // manifest by rename leaves an interrupted write in the incomplete-snapshot path.
        File.Move(pendingPath, finalPath, overwrite: false);
    }

    internal static void DeleteCommittedSnapshot(string directory)
    {
        // Keep the protected ownership manifest until all payload files are gone, so
        // a failed deletion can be retried without requiring the removed payload hashes.
        RejectReparsePointComponents(directory);
        if (Directory.EnumerateDirectories(directory).Any())
            throw new InvalidDataException("Unexpected directory inside broker snapshot.");
        var record = Path.Combine(directory, "transaction.json");
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(file, record, StringComparison.OrdinalIgnoreCase)) continue;
            RejectReparsePointComponents(file);
            File.Delete(file);
        }
        RejectReparsePointComponents(record);
        File.Delete(record);
        Directory.Delete(directory, recursive: false);
    }

    internal static async ValueTask CompleteAbsentTransactionAsync(
        NativePromptBrokerCommandKind command,
        Func<ValueTask<NativePromptBrokerInstallationStatus>> checkOwnership)
    {
        // A commit acknowledgement can be lost after the snapshot was removed. Retrying
        // commit is harmless, provided the current installation still belongs to us.
        // Missing restore data cannot establish that the old installation was restored.
        if (command != NativePromptBrokerCommandKind.Commit)
            throw new InvalidOperationException("The broker restore snapshot is missing.");
        if (await checkOwnership().ConfigureAwait(false) != NativePromptBrokerInstallationStatus.Success)
            throw new UnauthorizedAccessException("Broker ownership cannot be verified for transaction cleanup.");
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Invalid installation record size.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return data;
    }

    private static void ProtectTransactionDirectory(string path, string sid)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var principal in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private static void ProtectFile(string path, string sid)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var principal in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    internal static bool IsSafeIncompleteSnapshotAcl(DirectorySecurity acl, string sid)
    {
        if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            return false;
        var identifiesUser = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference.Value is "S-1-5-18" or "S-1-5-32-544") continue;
            if (rule.IdentityReference.Value != sid || (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                return false;
            identifiesUser = true;
        }
        return identifiesUser;
    }

    private static void RequireProtectedFile(string path)
    {
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("Installation record owner is missing.");
        // The owning administrator can replace the ACL. Users only receive read permission.
        if (!owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            throw new UnauthorizedAccessException("Installation record owner is not trusted.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("Installation record permits untrusted writes.");
    }

    private static string TaskSidFromXml(string xml)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principal = document.Root?.Element(ns + "Principals")?.Element(ns + "Principal");
        var user = principal?.Element(ns + "UserId")?.Value;
        if (string.IsNullOrWhiteSpace(user)) throw new InvalidDataException("Task owner cannot be determined.");
        return ResolveAccountSid(user);
    }

    private static (string? Sid, string? Xml, bool Running) ReadTaskSnapshot()
    {
        var exists = TryOpenTask(WindowsNativePromptBrokerInstaller.ScheduledTaskName, out var service, out var folder, out var task);
        try
        {
            if (!exists) return (null, null, false);
            string xml = ((dynamic)task!).Xml;
            return (TaskSidFromXml(xml), xml, Convert.ToInt32(((dynamic)task!).State) == 4);
        }
        finally { ReleaseCom(task); ReleaseCom(folder); ReleaseCom(service); }
    }

    private static void RestoreTaskXml(string xml, string sid)
    {
        object? service = null; object? folder = null; object? registered = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            registered = ((dynamic)folder).RegisterTask(WindowsNativePromptBrokerInstaller.ScheduledTaskName, xml, 6, sid, null, 3, null);
        }
        finally { ReleaseCom(registered); ReleaseCom(folder); ReleaseCom(service); }
    }
}
