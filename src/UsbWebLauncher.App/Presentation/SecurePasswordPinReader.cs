using System.Runtime.InteropServices;
using System.Security;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Presentation;

internal static class SecurePasswordPinReader
{
    public static ClientCertificatePin CopyFrom(SecureString securePassword) =>
        CopyFrom(
            securePassword,
            Marshal.SecureStringToGlobalAllocUnicode,
            static (pointer, characters) =>
                Marshal.Copy(pointer, characters, 0, characters.Length),
            Marshal.ZeroFreeGlobalAllocUnicode);

    internal static ClientCertificatePin CopyFrom(
        SecureString securePassword,
        Func<SecureString, IntPtr> allocate,
        Action<IntPtr, char[]> copy,
        Action<IntPtr> clearAndFree)
    {
        ArgumentNullException.ThrowIfNull(securePassword);
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(clearAndFree);
        if (securePassword.Length == 0)
        {
            return ClientCertificatePin.CopyOf(ReadOnlySpan<char>.Empty);
        }

        var characters = new char[securePassword.Length];
        var pointer = IntPtr.Zero;
        try
        {
            pointer = allocate(securePassword);
            copy(pointer, characters);
            return ClientCertificatePin.CopyOf(characters.AsSpan());
        }
        finally
        {
            Array.Clear(characters, 0, characters.Length);
            if (pointer != IntPtr.Zero)
            {
                clearAndFree(pointer);
            }
        }
    }
}
