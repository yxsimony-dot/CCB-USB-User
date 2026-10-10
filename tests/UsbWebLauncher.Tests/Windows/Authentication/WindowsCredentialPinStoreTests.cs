// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsCredentialPinStoreTests
{
    [Fact]
    public async Task BrowserNativeStores_SaveReadDeleteIgnoreLockedValidationStampFile()
    {
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "UsbWebLauncher.Tests", Guid.NewGuid().ToString("N"));
        var legacyStamps = new WindowsValidationStampStore(WindowsCredentialTarget.ApplicationNamespace, root);
        var native = new RecordingCredentialNativeApi();
        var secret = CreateRandomSecret();
        try
        {
            await legacyStamps.SaveAsync(new(authenticationId, "old-key-hash"));
            var stampFile = Assert.Single(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            using (var lockedStamp = new FileStream(stampFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var stores = WindowsClientCertificateAuthenticationStores.Create(authenticationId,
                    new WindowsCredentialManager(native), root, useValidationStamps: false);
                Assert.Equal(WindowsCredentialTarget.Create(authenticationId), stores.CredentialTargetName);
                using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());
                await stores.PinStore.SaveAsync(stores.CredentialTargetName, pin);
                using var read = await stores.PinStore.ReadAsync(stores.CredentialTargetName);
                Assert.NotNull(read);
                Assert.Equal(secret, read.AsMemory().ToArray());
                await stores.PinStore.DeleteAsync(stores.CredentialTargetName);
                Assert.Null(await stores.PinStore.ReadAsync(stores.CredentialTargetName));
            }

            Assert.Equal("old-key-hash", (await legacyStamps.ReadAsync(authenticationId))!.AuthenticationHash);
        }
        finally
        {
            Array.Clear(secret);
            native.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BrowserNativeStores_ValidationStoreIsNoOpWithUnusableFileSystemRoot()
    {
        var authenticationId = $"test-{Guid.NewGuid():N}";
        using var native = new RecordingCredentialNativeApi();
        var stores = WindowsClientCertificateAuthenticationStores.Create(authenticationId,
            new WindowsCredentialManager(native), "invalid\0root", useValidationStamps: false);

        await stores.ValidationStampStore.SaveAsync(new(authenticationId, "unused-hash"));
        Assert.Null(await stores.ValidationStampStore.ReadAsync(authenticationId));
        await stores.ValidationStampStore.DeleteAsync(authenticationId);
    }

    [Fact]
    public async Task SaveReadReplaceAndDelete_UsesGenericCurrentUserCredential()
    {
        var native = new RecordingCredentialNativeApi();
        var store = new WindowsCredentialPinStore(
            Guid.NewGuid().ToString("N"),
            new WindowsCredentialManager(native),
            new RecordingStampStore(null));
        var target = store.TargetName;
        var firstSecret = CreateRandomSecret();
        var replacementSecret = CreateRandomSecret();

        try
        {
            using (var firstPin = ClientCertificatePin.CopyOf(firstSecret.AsMemory()))
            {
                await store.SaveAsync(target, firstPin);
            }

            using (var replacementPin = ClientCertificatePin.CopyOf(replacementSecret.AsMemory()))
            {
                await store.SaveAsync(target, replacementPin);
            }

            using var readPin = await store.ReadAsync(target);

            Assert.NotNull(readPin);
            Assert.Equal(replacementSecret, readPin.AsMemory().ToArray());
            Assert.Equal(1u, native.LastWrite.Type);
            Assert.Equal(2u, native.LastWrite.Persist);
            Assert.Equal((uint)(replacementSecret.Length * sizeof(char)), native.LastWrite.CredentialBlobSize);
            Assert.True(native.LastWrite.HadCredentialBlob);
            Assert.Equal(1u, native.LastReadType);
            Assert.True(native.BlobWasZeroedBeforeFree);

            await store.DeleteAsync(target);

            Assert.Null(await store.ReadAsync(target));
            Assert.Equal(1u, native.LastDeleteType);
        }
        finally
        {
            try
            {
                await store.DeleteAsync(target);
            }
            finally
            {
                Array.Clear(firstSecret, 0, firstSecret.Length);
                Array.Clear(replacementSecret, 0, replacementSecret.Length);
                native.Dispose();
            }
        }
    }

    [Fact]
    public async Task ReadAndDelete_WhenCredentialDoesNotExist_AreIdempotent()
    {
        var native = new RecordingCredentialNativeApi();
        var store = new WindowsCredentialPinStore(
            Guid.NewGuid().ToString("N"),
            new WindowsCredentialManager(native),
            new RecordingStampStore(null));
        var target = store.TargetName;

        try
        {
            Assert.Null(await store.ReadAsync(target));

            await store.DeleteAsync(target);
        }
        finally
        {
            await store.DeleteAsync(target);
            native.Dispose();
        }
    }

    [Fact]
    public async Task CurrentUserCredentialManager_RoundTripsOnlyARuntimeRandomDisposableCredential()
    {
        var stores = WindowsClientCertificateAuthenticationStores.Create(Guid.NewGuid().ToString("N"));
        var store = stores.PinStore;
        var target = stores.CredentialTargetName;
        var secret = CreateRandomSecret();

        try
        {
            using (var pin = ClientCertificatePin.CopyOf(secret.AsMemory()))
            {
                await store.SaveAsync(target, pin);
            }

            using var readPin = await store.ReadAsync(target);
            Assert.NotNull(readPin);
            Assert.Equal(secret, readPin.AsMemory().ToArray());

            await store.DeleteAsync(target);
            Assert.Null(await store.ReadAsync(target));
        }
        finally
        {
            try
            {
                await store.DeleteAsync(target);
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
            }
        }
    }

    [Fact]
    public async Task Adapter_RejectsNonCanonicalTargetBeforeAccessingNativeCredentials()
    {
        var native = new RecordingCredentialNativeApi();
        var store = new WindowsCredentialPinStore(
            Guid.NewGuid().ToString("N"),
            new WindowsCredentialManager(native),
            new RecordingStampStore(null));
        var arbitraryTarget = $"arbitrary/{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadAsync(arbitraryTarget));

        Assert.Equal(0u, native.LastReadType);
        native.Dispose();
    }

    [Fact]
    public async Task SaveAsync_WhenCanceledWriteCannotBeCompensated_InvalidatesRealStampBeforeCrossProcessReuse()
    {
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var authentication = CreateAuthentication(authenticationId);
        var root = Path.Combine(Path.GetTempPath(), "UsbWebLauncher.Tests", Guid.NewGuid().ToString("N"));
        var initialStamps = new WindowsValidationStampStore(WindowsCredentialTarget.ApplicationNamespace, root);
        using var cancellation = new CancellationTokenSource();
        var native = new ResidualCredentialNativeApi(cancellation);
        var stores = WindowsClientCertificateAuthenticationStores.Create(
            authenticationId,
            new WindowsCredentialManager(native),
            root);
        var secret = CreateRandomSecret();

        try
        {
            await initialStamps.SaveAsync(new(authenticationId, authentication.GetNormalizedHash()));
            using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());
            await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
                await stores.PinStore.SaveAsync(stores.CredentialTargetName, pin, cancellation.Token));

            var reconstructedStamps = new WindowsValidationStampStore(WindowsCredentialTarget.ApplicationNamespace, root);
            Assert.Null(await reconstructedStamps.ReadAsync(authenticationId));

            var unlocker = new CountingUnlocker();
            var reconstructedStores = WindowsClientCertificateAuthenticationStores.Create(
                authenticationId,
                new WindowsCredentialManager(native),
                root);
            await using var coordinator = new AuthenticationCoordinator(
                authentication,
                reconstructedStores.PinStore,
                reconstructedStores.ValidationStampStore,
                unlocker);
            await coordinator.HandleDevicePresenceAsync(true);

            Assert.Equal(AuthenticationStage.RequiresPinSetup, coordinator.CurrentState.Stage);
            Assert.Equal(AuthenticationErrorCategory.CredentialsUnavailable, coordinator.CurrentState.ErrorCategory);
            Assert.Equal(0, native.ReadCount);
            Assert.Equal(0, unlocker.AttemptCount);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WhenStampInvalidationFails_DoesNotWriteCredential()
    {
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var native = new RecordingCredentialNativeApi();
        var stamps = new RecordingStampStore(null) { DeleteException = new InvalidOperationException() };
        var store = new WindowsCredentialPinStore(authenticationId, new WindowsCredentialManager(native), stamps);
        var secret = CreateRandomSecret();

        try
        {
            using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.SaveAsync(store.TargetName, pin));

            Assert.Equal(0u, native.LastWrite.Type);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
            native.Dispose();
        }
    }

    [Fact]
    public async Task DeleteAsync_DeletesStampBeforeCredentialAndClearsBoth()
    {
        var events = new List<string>();
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var native = new RecordingCredentialNativeApi { OnDelete = () => events.Add("credential") };
        var stamps = new RecordingStampStore(null) { OnDelete = () => events.Add("stamp") };
        var store = new WindowsCredentialPinStore(authenticationId, new WindowsCredentialManager(native), stamps);
        var secret = CreateRandomSecret();

        try
        {
            using (var pin = ClientCertificatePin.CopyOf(secret.AsMemory()))
            {
                await store.SaveAsync(store.TargetName, pin);
            }

            await stamps.SaveAsync(new(authenticationId, "verified-configuration-hash"));
            events.Clear();

            await store.DeleteAsync(store.TargetName);

            Assert.Equal(["stamp", "credential"], events);
            Assert.Null(stamps.StoredStamp);
            Assert.Null(await store.ReadAsync(store.TargetName));
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
            native.Dispose();
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenStampDeletionFails_DoesNotDeleteCredential()
    {
        var native = new RecordingCredentialNativeApi();
        var stamps = new RecordingStampStore(null) { DeleteException = new InvalidOperationException() };
        var store = new WindowsCredentialPinStore($"test-{Guid.NewGuid():N}", new WindowsCredentialManager(native), stamps);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.DeleteAsync(store.TargetName));

        Assert.Equal(0u, native.LastDeleteType);
    }

    [Fact]
    public async Task DeleteAsync_WhenCredentialDeletionFails_LeavesStampInvalid()
    {
        var events = new List<string>();
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var native = new RecordingCredentialNativeApi { DeleteFails = true, OnDelete = () => events.Add("credential") };
        var stamps = new RecordingStampStore(new(authenticationId, "verified-configuration-hash")) { OnDelete = () => events.Add("stamp") };
        var store = new WindowsCredentialPinStore(authenticationId, new WindowsCredentialManager(native), stamps);

        await Assert.ThrowsAsync<WindowsCredentialException>(async () => await store.DeleteAsync(store.TargetName));

        Assert.Equal(["stamp", "credential"], events);
        Assert.Null(stamps.StoredStamp);
    }

    [Fact]
    public async Task PublicComposition_CreatesCoordinatorCompatibleCoreStoresWithoutExposingRawCredentialLayer()
    {
        var authenticationId = $"test-{Guid.NewGuid():N}";
        var stores = WindowsClientCertificateAuthenticationStores.Create(authenticationId);
        var authentication = CreateAuthentication(authenticationId);
        var unlocker = new CountingUnlocker();

        await using var coordinator = new AuthenticationCoordinator(
            authentication,
            stores.PinStore,
            stores.ValidationStampStore,
            unlocker);

        Assert.Equal(WindowsCredentialTarget.Create(authenticationId), stores.CredentialTargetName);
        Assert.False(typeof(WindowsCredentialPinStore).IsPublic);
        Assert.False(typeof(WindowsCredentialManager).IsPublic);
    }

    private static ClientCertificateAuthenticationOptions CreateAuthentication(string authenticationId) => new(
        authenticationId,
        "bank.example.test",
        443,
        new("1111111111111111111111111111111111111111", "Test Subject", "Test Issuer"),
        new("Test CSP", 1, "Test Container", "AT_KEYEXCHANGE"),
        WindowsCredentialTarget.Create(authenticationId));

    private static char[] CreateRandomSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var secret = bytes.Select(value => (char)('A' + (value % 26))).ToArray();
        Array.Clear(bytes, 0, bytes.Length);
        return secret;
    }

    private sealed class RecordingCredentialNativeApi : IWindowsCredentialNativeApi, IDisposable
    {
        private readonly Dictionary<string, byte[]> _credentials = new(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, Allocation> _allocations = [];
        private int _lastError;

        public NativeWrite LastWrite { get; private set; } = new(0, 0, 0, false);

        public uint LastReadType { get; private set; }

        public uint LastDeleteType { get; private set; }

        public bool DeleteFails { get; init; }

        public Action? OnDelete { get; init; }

        public bool BlobWasZeroedBeforeFree { get; private set; }

        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags)
        {
            var target = Marshal.PtrToStringUni(credential.TargetName)!;
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            if (_credentials.Remove(target, out var prior))
            {
                Array.Clear(prior, 0, prior.Length);
            }

            _credentials.Add(target, blob);
            LastWrite = new(
                credential.Type,
                credential.Persist,
                credential.CredentialBlobSize,
                credential.CredentialBlob != IntPtr.Zero);
            return true;
        }

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential)
        {
            LastReadType = type;
            if (!_credentials.TryGetValue(targetName, out var blob))
            {
                _lastError = 1168;
                credential = IntPtr.Zero;
                return false;
            }

            var target = Marshal.StringToHGlobalUni(targetName);
            var secret = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, secret, blob.Length);
            var record = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCredentialNativeRecord>());
            Marshal.StructureToPtr(new WindowsCredentialNativeRecord
            {
                Type = type,
                TargetName = target,
                CredentialBlob = secret,
                CredentialBlobSize = (uint)blob.Length,
                Persist = 2
            }, record, false);
            _allocations.Add(record, new(target, secret));
            credential = record;
            return true;
        }

        public bool CredDelete(string targetName, uint type, uint flags)
        {
            LastDeleteType = type;
            OnDelete?.Invoke();
            if (DeleteFails)
            {
                _lastError = 5;
                return false;
            }

            if (_credentials.Remove(targetName, out var blob))
            {
                Array.Clear(blob, 0, blob.Length);
                return true;
            }

            _lastError = 1168;
            return false;
        }

        public int GetLastError() => _lastError;

        public void CredFree(IntPtr credential)
        {
            var allocation = _allocations[credential];
            var record = Marshal.PtrToStructure<WindowsCredentialNativeRecord>(credential);
            var blobBeforeFree = new byte[checked((int)record.CredentialBlobSize)];
            Marshal.Copy(record.CredentialBlob, blobBeforeFree, 0, blobBeforeFree.Length);
            BlobWasZeroedBeforeFree = blobBeforeFree.All(value => value == 0);
            Marshal.StructureToPtr(default(WindowsCredentialNativeRecord), credential, false);
            Marshal.FreeHGlobal(allocation.TargetName);
            Marshal.FreeHGlobal(allocation.Secret);
            Marshal.FreeHGlobal(credential);
            _allocations.Remove(credential);
        }

        public void Dispose()
        {
            foreach (var (record, _) in _allocations.ToArray())
            {
                CredFree(record);
            }

            foreach (var credential in _credentials.Values)
            {
                Array.Clear(credential, 0, credential.Length);
            }

            _credentials.Clear();
        }
    }

    private sealed record Allocation(IntPtr TargetName, IntPtr Secret);

    private sealed record NativeWrite(
        uint Type,
        uint Persist,
        uint CredentialBlobSize,
        bool HadCredentialBlob);

    private sealed class RecordingStampStore(ClientCertificateValidationStamp? stamp) : IClientCertificateValidationStampStore
    {
        public ClientCertificateValidationStamp? StoredStamp { get; private set; } = stamp;

        public Exception? DeleteException { get; init; }

        public Action? OnDelete { get; init; }

        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StoredStamp);

        public ValueTask SaveAsync(ClientCertificateValidationStamp stamp, CancellationToken cancellationToken = default)
        {
            StoredStamp = stamp;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default)
        {
            OnDelete?.Invoke();
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            StoredStamp = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResidualCredentialNativeApi(CancellationTokenSource cancellation) : IWindowsCredentialNativeApi
    {
        public int ReadCount { get; private set; }

        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags)
        {
            cancellation.Cancel();
            return true;
        }

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential)
        {
            ReadCount++;
            credential = IntPtr.Zero;
            return false;
        }

        public bool CredDelete(string targetName, uint type, uint flags) => false;

        public int GetLastError() => 5;

        public void CredFree(IntPtr credential)
        {
        }
    }

    private sealed class CountingUnlocker : IClientCertificateTokenUnlocker
    {
        public int AttemptCount { get; private set; }

        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.UnknownFailure));
        }
    }
}
