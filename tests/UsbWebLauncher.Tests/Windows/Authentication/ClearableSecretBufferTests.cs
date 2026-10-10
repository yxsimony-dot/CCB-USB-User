using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class ClearableSecretBufferTests
{
    [Fact]
    public void Dispose_ClearsManagedAndUnmanagedSecretBuffers()
    {
        var allocator = new RecordingSecretBufferAllocator();
        var secret = CreateRandomSecret();
        var buffer = new ClearableSecretBuffer(secret.AsSpan(), allocator);

        buffer.Dispose();

        var managedBuffer = (byte[])typeof(ClearableSecretBuffer)
            .GetField("_managedBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(buffer)!;
        Assert.All(managedBuffer, value => Assert.Equal((byte)0, value));
        Assert.All(allocator.BytesAtFree, value => Assert.Equal((byte)0, value));
        Array.Clear(secret, 0, secret.Length);
    }

    [Fact]
    public void Constructor_WhenAllocationFails_ClearsEncodedManagedSecret()
    {
        var memory = new AllocationFailingSecretBufferMemory();
        var secret = CreateRandomSecret();

        try
        {
            Assert.Throws<InvalidOperationException>(() => new ClearableSecretBuffer(secret.AsSpan(), memory));

            Assert.All(memory.EncodedBytes, value => Assert.Equal((byte)0, value));
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public void Constructor_WhenCopyFails_ClearsManagedSecretAndAllocatedUnmanagedMemory()
    {
        var memory = new CopyFailingSecretBufferMemory();
        var secret = CreateRandomSecret();

        try
        {
            Assert.Throws<InvalidOperationException>(() => new ClearableSecretBuffer(secret.AsSpan(), memory));

            Assert.All(memory.EncodedBytes, value => Assert.Equal((byte)0, value));
            Assert.All(memory.BytesAtFree, value => Assert.Equal((byte)0, value));
            Assert.Equal(1, memory.FreeCount);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenNativeCallThrows_RedactsMarkerAndClearsSecretBuffers()
    {
        var marker = $"credential-marker-{Guid.NewGuid():N}";
        var diagnostics = new RecordingDiagnostics();
        var allocator = new RecordingSecretBufferAllocator();
        var manager = new WindowsCredentialManager(new ThrowingNativeApi(marker), diagnostics, allocator);
        using var pin = ClientCertificatePin.CopyOf(marker.AsMemory());

        var exception = await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
            await manager.WriteAsync("UsbWebLauncher.Tests/failure", pin));

        Assert.DoesNotContain(marker, exception.Message, StringComparison.Ordinal);
        Assert.All(diagnostics.Messages, message => Assert.DoesNotContain(marker, message, StringComparison.Ordinal));
        Assert.All(allocator.BytesAtFree, value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task NativeFailure_DoesNotExposeRandomMarkerInDiagnosticsOrException(string operation)
    {
        var marker = $"credential-marker-{Guid.NewGuid():N}";
        var diagnostics = new RecordingDiagnostics();
        var manager = new WindowsCredentialManager(new ThrowingNativeApi(marker), diagnostics);

        var exception = operation == "read"
            ? await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
                await manager.ReadAsync($"UsbWebLauncher.Tests/{marker}"))
            : await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
                await manager.DeleteAsync($"UsbWebLauncher.Tests/{marker}"));

        Assert.DoesNotContain(marker, exception.Message, StringComparison.Ordinal);
        Assert.All(diagnostics.Messages, message => Assert.DoesNotContain(marker, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_WhenCancellationOccursDuringNativeCall_ClearsSecretBuffers()
    {
        var allocator = new RecordingSecretBufferAllocator();
        using var source = new CancellationTokenSource();
        var native = new CancelingNativeApi(source);
        var manager = new WindowsCredentialManager(
            native,
            secretBufferAllocator: allocator);
        var secret = CreateRandomSecret();

        try
        {
            using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await manager.WriteAsync("UsbWebLauncher.Tests/canceled", pin, source.Token));
            Assert.All(allocator.BytesAtFree, value => Assert.Equal((byte)0, value));
            Assert.False(native.CredentialExists);
            Assert.Equal(1, native.DeleteCount);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenCancellationCompensationFails_ThrowsSanitizedPersistenceError()
    {
        var marker = $"credential-marker-{Guid.NewGuid():N}";
        var diagnostics = new RecordingDiagnostics();
        using var source = new CancellationTokenSource();
        var native = new CancelingDeleteFailingNativeApi(source, marker);
        var manager = new WindowsCredentialManager(native, diagnostics);
        using var pin = ClientCertificatePin.CopyOf(marker.AsMemory());

        var exception = await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
            await manager.WriteAsync($"UsbWebLauncher.Tests/{marker}", pin, source.Token));

        Assert.Equal(1, native.DeleteCount);
        Assert.DoesNotContain(marker, exception.Message, StringComparison.Ordinal);
        Assert.All(diagnostics.Messages, message => Assert.DoesNotContain(marker, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_WhenRecordIsMalformed_ZeroesNativeBlobBeforeCredFree()
    {
        var native = new ReadCredentialNativeApi(CreateRandomSecret(), malformed: true);
        var manager = new WindowsCredentialManager(native);

        await Assert.ThrowsAsync<WindowsCredentialException>(async () =>
            await manager.ReadAsync("UsbWebLauncher/ClientCertificate/test-read"));

        Assert.True(native.BlobWasZeroedBeforeFree);
        Assert.True(native.WasFreed);
    }

    [Fact]
    public async Task ReadAsync_WhenCancellationOccursAfterCredRead_ZeroesBlobFreesHandleAndReturnsNoPin()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new ReadCredentialNativeApi(CreateRandomSecret(), cancellation);
        var manager = new WindowsCredentialManager(native);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.ReadAsync("UsbWebLauncher/ClientCertificate/test-read", cancellation.Token));

        Assert.True(native.BlobWasZeroedBeforeFree);
        Assert.True(native.WasFreed);
    }

    [Fact]
    public async Task ReadAsync_WhenCredFreeThrows_DoesNotExposeNativeReleaseFailure()
    {
        var native = new ReadCredentialNativeApi(CreateRandomSecret(), throwOnFree: true);
        var manager = new WindowsCredentialManager(native);

        using var pin = await manager.ReadAsync("UsbWebLauncher/ClientCertificate/test-read");

        Assert.NotNull(pin);
        Assert.True(native.BlobWasZeroedBeforeFree);
        Assert.True(native.WasFreed);
    }

    private static char[] CreateRandomSecret() =>
        RandomNumberGenerator.GetBytes(24).Select(value => (char)('A' + (value % 26))).ToArray();

    private sealed class RecordingSecretBufferAllocator : IUnmanagedSecretBufferAllocator
    {
        public byte[] BytesAtFree { get; private set; } = [];

        public IntPtr Allocate(int byteCount) => Marshal.AllocHGlobal(byteCount);

        public void Free(IntPtr buffer, int byteCount)
        {
            BytesAtFree = new byte[byteCount];
            Marshal.Copy(buffer, BytesAtFree, 0, byteCount);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class AllocationFailingSecretBufferMemory : ISecretBufferMemory
    {
        public byte[] EncodedBytes { get; private set; } = [];

        public byte[] Encode(ReadOnlySpan<char> secret)
        {
            var characters = secret.ToArray();
            try
            {
                EncodedBytes = Encoding.Unicode.GetBytes(characters);
                return EncodedBytes;
            }
            finally
            {
                Array.Clear(characters, 0, characters.Length);
            }
        }

        public IntPtr Allocate(int byteCount) => throw new InvalidOperationException();

        public void CopyToUnmanaged(byte[] source, IntPtr destination)
        {
        }

        public void ClearUnmanaged(IntPtr buffer, int byteCount)
        {
        }

        public void Free(IntPtr buffer, int byteCount)
        {
        }
    }

    private sealed class CopyFailingSecretBufferMemory : ISecretBufferMemory
    {
        public byte[] EncodedBytes { get; private set; } = [];

        public byte[] BytesAtFree { get; private set; } = [];

        public int FreeCount { get; private set; }

        public byte[] Encode(ReadOnlySpan<char> secret)
        {
            var characters = secret.ToArray();
            try
            {
                EncodedBytes = Encoding.Unicode.GetBytes(characters);
                return EncodedBytes;
            }
            finally
            {
                Array.Clear(characters, 0, characters.Length);
            }
        }

        public IntPtr Allocate(int byteCount) => Marshal.AllocHGlobal(byteCount);

        public void CopyToUnmanaged(byte[] source, IntPtr destination)
        {
            Marshal.Copy(source, 0, destination, source.Length);
            throw new InvalidOperationException();
        }

        public void ClearUnmanaged(IntPtr buffer, int byteCount) =>
            Marshal.Copy(new byte[byteCount], 0, buffer, byteCount);

        public void Free(IntPtr buffer, int byteCount)
        {
            BytesAtFree = new byte[byteCount];
            Marshal.Copy(buffer, BytesAtFree, 0, byteCount);
            FreeCount++;
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class RecordingDiagnostics : IWindowsCredentialDiagnostics
    {
        public List<string> Messages { get; } = [];

        public void CredentialOperationFailed(string operation, int? win32ErrorCode) =>
            Messages.Add($"{operation}:{win32ErrorCode}");
    }

    private sealed class ThrowingNativeApi(string marker) : IWindowsCredentialNativeApi
    {
        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags) =>
            throw new InvalidOperationException(marker);

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential) =>
            throw new InvalidOperationException(marker);

        public bool CredDelete(string targetName, uint type, uint flags) =>
            throw new InvalidOperationException(marker);

        public int GetLastError() => 5;

        public void CredFree(IntPtr credential)
        {
        }
    }

    private sealed class CancelingNativeApi(CancellationTokenSource source) : IWindowsCredentialNativeApi
    {
        public bool CredentialExists { get; private set; }

        public int DeleteCount { get; private set; }

        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags)
        {
            CredentialExists = true;
            source.Cancel();
            return true;
        }

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential)
        {
            credential = IntPtr.Zero;
            return false;
        }

        public bool CredDelete(string targetName, uint type, uint flags)
        {
            DeleteCount++;
            CredentialExists = false;
            return true;
        }

        public int GetLastError() => 0;

        public void CredFree(IntPtr credential)
        {
        }
    }

    private sealed class CancelingDeleteFailingNativeApi(CancellationTokenSource source, string marker) : IWindowsCredentialNativeApi
    {
        public int DeleteCount { get; private set; }

        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags)
        {
            source.Cancel();
            return true;
        }

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential)
        {
            credential = IntPtr.Zero;
            return false;
        }

        public bool CredDelete(string targetName, uint type, uint flags)
        {
            DeleteCount++;
            throw new InvalidOperationException(marker);
        }

        public int GetLastError() => 5;

        public void CredFree(IntPtr credential)
        {
        }
    }

    private sealed class ReadCredentialNativeApi : IWindowsCredentialNativeApi
    {
        private readonly char[] _secret;
        private readonly CancellationTokenSource? _cancellation;
        private readonly bool _malformed;
        private readonly bool _throwOnFree;

        public ReadCredentialNativeApi(
            char[] secret,
            CancellationTokenSource? cancellation = null,
            bool malformed = false,
            bool throwOnFree = false)
        {
            _secret = secret;
            _cancellation = cancellation;
            _malformed = malformed;
            _throwOnFree = throwOnFree;
        }

        public bool BlobWasZeroedBeforeFree { get; private set; }

        public bool WasFreed { get; private set; }

        public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags) => true;

        public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential)
        {
            var target = Marshal.StringToHGlobalUni(targetName);
            var bytes = Encoding.Unicode.GetBytes(_secret);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Array.Clear(bytes, 0, bytes.Length);
            credential = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCredentialNativeRecord>());
            Marshal.StructureToPtr(new WindowsCredentialNativeRecord
            {
                Type = type,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = _malformed ? 1u : checked((uint)(_secret.Length * sizeof(char))),
                Persist = 2
            }, credential, false);
            _cancellation?.Cancel();
            return true;
        }

        public bool CredDelete(string targetName, uint type, uint flags) => true;

        public int GetLastError() => 5;

        public void CredFree(IntPtr credential)
        {
            var record = Marshal.PtrToStructure<WindowsCredentialNativeRecord>(credential);
            var blob = new byte[checked((int)record.CredentialBlobSize)];
            Marshal.Copy(record.CredentialBlob, blob, 0, blob.Length);
            BlobWasZeroedBeforeFree = blob.All(value => value == 0);
            Marshal.FreeHGlobal(record.TargetName);
            Marshal.FreeHGlobal(record.CredentialBlob);
            Marshal.FreeHGlobal(credential);
            WasFreed = true;
            Array.Clear(_secret, 0, _secret.Length);
            if (_throwOnFree)
            {
                throw new InvalidOperationException();
            }
        }
    }
}
