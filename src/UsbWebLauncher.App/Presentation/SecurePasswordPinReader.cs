using System.Runtime.InteropServices;
using System.Security;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Presentation;

internal static class SecurePasswordPinReader
{
    public static ClientCertificatePin CopyFrom(SecureString securePassword)
    {
        ArgumentNullException.ThrowIfNull(securePassword);
        if (securePassword.Length == 0)
        {
            return ClientCertificatePin.CopyOf(ReadOnlySpan<char>.Empty);
        }

        var characters = new char[securePassword.Length];
        var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
            Marshal.Copy(pointer, characters, 0, characters.Length);
            return ClientCertificatePin.CopyOf(characters.AsSpan());
        }
        finally
        {
            Array.Clear(characters, 0, characters.Length);
            if (pointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }
    }
}
