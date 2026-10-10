using System.Runtime.InteropServices;

namespace UsbWebLauncher.Windows.Authentication;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsCredentialNativeRecord
{
    public uint Flags;
    public uint Type;
    public IntPtr TargetName;
    public IntPtr Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    public IntPtr TargetAlias;
    public IntPtr UserName;
}

internal interface IWindowsCredentialNativeApi
{
    bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags);

    bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    bool CredDelete(string targetName, uint type, uint flags);

    int GetLastError();

    void CredFree(IntPtr credential);
}

internal sealed class WindowsCredentialNativeApi : IWindowsCredentialNativeApi
{
    public bool CredWrite(ref WindowsCredentialNativeRecord credential, uint flags) =>
        CredWriteW(ref credential, flags);

    public bool CredRead(string targetName, uint type, uint flags, out IntPtr credential) =>
        CredReadW(targetName, type, flags, out credential);

    public bool CredDelete(string targetName, uint type, uint flags) =>
        CredDeleteW(targetName, type, flags);

    public int GetLastError() => Marshal.GetLastWin32Error();

    public void CredFree(IntPtr credential) => CredFreeNative(credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref WindowsCredentialNativeRecord credential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFreeNative(IntPtr credential);
}
