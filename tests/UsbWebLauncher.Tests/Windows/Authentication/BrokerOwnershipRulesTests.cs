using System.Security.AccessControl;
using System.Security.Principal;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class BrokerOwnershipRulesTests
{
    private const string User = "S-1-5-21-100-200-300-1001";
    private const string Other = "S-1-5-21-100-200-300-1002";
    private static BrokerOwnerRecord Owner(string sid = User) => new(1, "usb-web-launcher", sid, @"C:\Users\User\App.exe");

    [Fact]
    public void FreshInstall_AcceptsOriginalInteractiveUser() =>
        Assert.Equal(NativePromptBrokerInstallationStatus.Success, Evaluate(User, User));

    [Theory]
    [InlineData(Other, User)]
    [InlineData(User, Other)]
    [InlineData("S-1-5-18", "S-1-5-18")]
    [InlineData("", "")]
    public void AlternateAdminSystemOrUnknownIdentity_IsRejected(string caller, string session) =>
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipMismatch, Evaluate(caller, session));

    [Fact]
    public void ProtectedRecord_AcceptsSameUserRepairEvenIfTaskIsMissing() =>
        Assert.Equal(NativePromptBrokerInstallationStatus.Success, Evaluate(User, User, Owner(), true));

    [Fact]
    public void OtherUserRecord_IsRejectedEvenIfTaskClaimsCurrentUser() =>
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipMismatch, Evaluate(User, User, Owner(Other), true, User));

    [Fact]
    public void OtherUserTask_IsRejectedEvenIfRecordClaimsCurrentUser() =>
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipMismatch, Evaluate(User, User, Owner(), true, Other));

    [Fact]
    public void LegacyInstall_RequiresBoundPolicyAndSameUserTask()
    {
        Assert.Equal(NativePromptBrokerInstallationStatus.Success, Evaluate(User, User, null, true, User, true));
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipInspectionFailed, Evaluate(User, User, null, true, User, false));
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipInspectionFailed, Evaluate(User, User, null, true, null, true));
    }

    [Fact]
    public void TaskWithoutOwnedDirectory_IsNotSilentlyTakenOver() =>
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipInspectionFailed, Evaluate(User, User, null, false, User));

    [Fact]
    public void ForeignProductOrRelativePath_IsRejected()
    {
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipMismatch, Evaluate(User, User, Owner() with { ProductId = "other" }, true));
        Assert.Equal(NativePromptBrokerInstallationStatus.OwnershipMismatch, Evaluate(User, User, Owner() with { ApplicationPath = "app.exe" }, true));
    }

    [Theory]
    [InlineData(User, false, true)]
    [InlineData(Other, false, false)]
    [InlineData(User, true, false)]
    public void InterruptedSnapshot_OnlySameUserProtectedDirectoryCanBeDiscarded(string reader, bool writable, bool expected)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(reader),
            writable ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.Equal(expected, WindowsNativePromptBrokerInstallationApi.IsSafeIncompleteSnapshotAcl(acl, User));
    }

    [Fact]
    public async Task CommitAfterSnapshotCleanup_IsIdempotentAndStillChecksOwnership()
    {
        var checks = 0;
        for (var attempt = 0; attempt < 2; attempt++)
            await WindowsNativePromptBrokerInstallationApi.CompleteAbsentTransactionAsync(
                NativePromptBrokerCommandKind.Commit,
                () => { checks++; return ValueTask.FromResult(NativePromptBrokerInstallationStatus.Success); });
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task CommitWithoutSnapshot_RejectsChangedOwnership() =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            WindowsNativePromptBrokerInstallationApi.CompleteAbsentTransactionAsync(
                NativePromptBrokerCommandKind.Commit,
                () => ValueTask.FromResult(NativePromptBrokerInstallationStatus.OwnershipMismatch)).AsTask());

    [Fact]
    public async Task RestoreWithoutSnapshot_FailsClosedEvenForSameOwner() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsNativePromptBrokerInstallationApi.CompleteAbsentTransactionAsync(
                NativePromptBrokerCommandKind.Restore,
                () => ValueTask.FromResult(NativePromptBrokerInstallationStatus.Success)).AsTask());

    [Fact]
    public void CommitCleanup_CanFinishPartlyDeletedSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "broker-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "transaction.json"), "owned snapshot manifest");
            // The original broker payload was already deleted by an interrupted commit.
            File.WriteAllText(Path.Combine(directory, "native-prompt-policy.json"), "remaining payload");
            WindowsNativePromptBrokerInstallationApi.DeleteCommittedSnapshot(directory);
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SnapshotManifest_IsOnlyPublishedAfterPendingWriteCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "broker-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pending = Path.Combine(directory, "transaction.next.json");
            var final = Path.Combine(directory, "transaction.json");
            File.WriteAllText(pending, "partial");
            Assert.False(File.Exists(final));
            File.WriteAllText(pending, "complete manifest");
            WindowsNativePromptBrokerInstallationApi.PublishTransactionManifest(pending, final);
            Assert.Equal("complete manifest", File.ReadAllText(final));
            Assert.False(File.Exists(pending));
            File.WriteAllText(pending, "another manifest");
            Assert.Throws<IOException>(() => WindowsNativePromptBrokerInstallationApi.PublishTransactionManifest(pending, final));
            Assert.Equal("complete manifest", File.ReadAllText(final));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static NativePromptBrokerInstallationStatus Evaluate(string caller, string session, BrokerOwnerRecord? owner = null,
        bool directory = false, string? task = null, bool legacy = false) =>
        BrokerOwnershipRules.Evaluate(caller, session, owner, directory, task, legacy);
}
