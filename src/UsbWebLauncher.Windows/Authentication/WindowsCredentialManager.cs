using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

internal interface IWindowsCredentialDiagnostics
{
    void CredentialOperationFailed(string operation, int? win32ErrorCode);
}

internal sealed class NullWindowsCredentialDiagnostics : IWindowsCredentialDiagnostics
{
    public void CredentialOperationFailed(string operation, int? win32ErrorCode)
    {
    }
}

internal sealed class WindowsCredentialException(string operation, int? win32ErrorCode = null)
    : Exception($"Windows Credential Manager {operation} failed.")
{
    public int? Win32ErrorCode { get; } = win32ErrorCode;
}

internal sealed class WindowsCredentialManager
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private readonly IWindowsCredentialNativeApi _native;
    private readonly IWindowsCredentialDiagnostics _diagnostics;
    private readonly IUnmanagedSecretBufferAllocator _secretBufferAllocator;

    public WindowsCredentialManager(
        IWindowsCredentialNativeApi native,
        IWindowsCredentialDiagnostics? diagnostics = null,
        IUnmanagedSecretBufferAllocator? secretBufferAllocator = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _diagnostics = diagnostics ?? new NullWindowsCredentialDiagnostics();
        _secretBufferAllocator = secretBufferAllocator ?? new UnmanagedSecretBufferAllocator();
    }

    public ValueTask WriteAsync(
        string targetName,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(pin);
        cancellationToken.ThrowIfCancellationRequested();

        var writeSucceeded = false;
        using var secret = new ClearableSecretBuffer(pin.AsMemory().Span, _secretBufferAllocator);
        var targetNameBuffer = Marshal.StringToHGlobalUni(targetName);
        try
        {
            var credential = new WindowsCredentialNativeRecord
            {
                Type = CredentialTypeGeneric,
                TargetName = targetNameBuffer,
                CredentialBlobSize = checked((uint)secret.ByteLength),
                CredentialBlob = secret.Pointer,
                Persist = CredentialPersistLocalMachine
            };

            if (!_native.CredWrite(ref credential, 0))
            {
                ThrowOperationFailure("write", _native.GetLastError());
            }

            writeSucceeded = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (writeSucceeded && cancellationToken.IsCancellationRequested)
        {
            if (!TryCompensateCanceledWrite(targetName, out var compensationError))
            {
                ThrowOperationFailure("compensating delete", compensationError);
            }

            throw;
        }
        catch (WindowsCredentialException)
        {
            throw;
        }
        catch
        {
            ThrowOperationFailure("write", null);
        }
        finally
        {
            Marshal.FreeHGlobal(targetNameBuffer);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ClientCertificatePin?> ReadAsync(
        string targetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_native.CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
            {
                var error = _native.GetLastError();
                if (error == ErrorNotFound)
                {
                    return ValueTask.FromResult<ClientCertificatePin?>(null);
                }

                ThrowOperationFailure("read", error);
            }

            using (var credential = new SafeCredentialHandle(credentialPointer, _native))
            {
                var record = Marshal.PtrToStructure<WindowsCredentialNativeRecord>(credential.DangerousGetHandle());
                try
                {
                    if (record.Type != CredentialTypeGeneric ||
                        record.CredentialBlob == IntPtr.Zero ||
                        record.CredentialBlobSize == 0 ||
                        record.CredentialBlobSize % sizeof(char) != 0)
                    {
                        ThrowOperationFailure("read", null);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var characters = new char[record.CredentialBlobSize / sizeof(char)];
                    try
                    {
                        Marshal.Copy(record.CredentialBlob, characters, 0, characters.Length);
                        return ValueTask.FromResult<ClientCertificatePin?>(
                            ClientCertificatePin.CopyOf(characters.AsMemory()));
                    }
                    finally
                    {
                        Array.Clear(characters, 0, characters.Length);
                    }
                }
                finally
                {
                    ClearNativeCredentialBlob(record);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsCredentialException)
        {
            throw;
        }
        catch
        {
            ThrowOperationFailure("read", null);
            throw;
        }
    }

    public ValueTask DeleteAsync(
        string targetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_native.CredDelete(targetName, CredentialTypeGeneric, 0))
            {
                var error = _native.GetLastError();
                if (error == ErrorNotFound)
                {
                    return ValueTask.CompletedTask;
                }

                ThrowOperationFailure("delete", error);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsCredentialException)
        {
            throw;
        }
        catch
        {
            ThrowOperationFailure("delete", null);
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowOperationFailure(string operation, int? win32ErrorCode)
    {
        _diagnostics.CredentialOperationFailed(operation, win32ErrorCode);
        throw new WindowsCredentialException(operation, win32ErrorCode);
    }

    private bool TryCompensateCanceledWrite(string targetName, out int? errorCode)
    {
        try
        {
            if (_native.CredDelete(targetName, CredentialTypeGeneric, 0))
            {
                errorCode = null;
                return true;
            }

            errorCode = _native.GetLastError();
            return errorCode == ErrorNotFound;
        }
        catch
        {
            errorCode = null;
            return false;
        }
    }

    private static void ClearNativeCredentialBlob(WindowsCredentialNativeRecord record)
    {
        if (record.CredentialBlob == IntPtr.Zero || record.CredentialBlobSize == 0 ||
            record.CredentialBlobSize > int.MaxValue)
        {
            return;
        }

        try
        {
            Marshal.Copy(
                new byte[checked((int)record.CredentialBlobSize)],
                0,
                record.CredentialBlob,
                checked((int)record.CredentialBlobSize));
        }
        catch
        {
        }
    }

    private sealed class SafeCredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly IWindowsCredentialNativeApi _native;

        public SafeCredentialHandle(IntPtr handle, IWindowsCredentialNativeApi native)
            : base(true)
        {
            _native = native;
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            try
            {
                _native.CredFree(handle);
            }
            catch
            {
            }

            return true;
        }
    }
}
