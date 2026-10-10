using System.Runtime.InteropServices;
using System.Text;

namespace UsbWebLauncher.Windows.Authentication;

public interface IUnmanagedSecretBufferAllocator
{
    IntPtr Allocate(int byteCount);

    void Free(IntPtr buffer, int byteCount);
}

public sealed class UnmanagedSecretBufferAllocator : IUnmanagedSecretBufferAllocator
{
    public IntPtr Allocate(int byteCount) => Marshal.AllocHGlobal(byteCount);

    public void Free(IntPtr buffer, int byteCount) => Marshal.FreeHGlobal(buffer);
}

internal interface ISecretBufferMemory
{
    byte[] Encode(ReadOnlySpan<char> secret);

    IntPtr Allocate(int byteCount);

    void CopyToUnmanaged(byte[] source, IntPtr destination);

    void ClearUnmanaged(IntPtr buffer, int byteCount);

    void Free(IntPtr buffer, int byteCount);
}

internal sealed class SecretBufferMemory(IUnmanagedSecretBufferAllocator allocator) : ISecretBufferMemory
{
    public byte[] Encode(ReadOnlySpan<char> secret)
    {
        var encoded = new byte[checked(secret.Length * sizeof(char))];
        Encoding.Unicode.GetBytes(secret, encoded);
        return encoded;
    }

    public IntPtr Allocate(int byteCount) => allocator.Allocate(byteCount);

    public void CopyToUnmanaged(byte[] source, IntPtr destination) =>
        Marshal.Copy(source, 0, destination, source.Length);

    public void ClearUnmanaged(IntPtr buffer, int byteCount) =>
        Marshal.Copy(new byte[byteCount], 0, buffer, byteCount);

    public void Free(IntPtr buffer, int byteCount) => allocator.Free(buffer, byteCount);
}

public sealed class ClearableSecretBuffer : IDisposable
{
    private readonly ISecretBufferMemory _memory;
    private byte[] _managedBuffer = [];
    private IntPtr _unmanagedBuffer;
    private bool _disposed;

    public ClearableSecretBuffer(
        ReadOnlySpan<char> secret,
        IUnmanagedSecretBufferAllocator? allocator = null)
        : this(secret, new SecretBufferMemory(allocator ?? new UnmanagedSecretBufferAllocator()))
    {
    }

    internal ClearableSecretBuffer(ReadOnlySpan<char> secret, ISecretBufferMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        try
        {
            _managedBuffer = _memory.Encode(secret);
            _unmanagedBuffer = _memory.Allocate(_managedBuffer.Length);
            _memory.CopyToUnmanaged(_managedBuffer, _unmanagedBuffer);
        }
        catch
        {
            ClearAndFree();
            throw;
        }
    }

    internal IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _unmanagedBuffer;
        }
    }

    internal int ByteLength => _managedBuffer.Length;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearAndFree();
    }

    private void ClearAndFree()
    {
        Array.Clear(_managedBuffer, 0, _managedBuffer.Length);
        if (_unmanagedBuffer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _memory.ClearUnmanaged(_unmanagedBuffer, _managedBuffer.Length);
        }
        finally
        {
            _memory.Free(_unmanagedBuffer, _managedBuffer.Length);
            _unmanagedBuffer = IntPtr.Zero;
        }
    }
}
