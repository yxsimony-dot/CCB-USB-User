using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class BrowserSessionDirectoryTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsbWebLauncherSessionTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SessionFoldersAreLazyUniqueAndDoNotReuseBrowserStorage()
    {
        var first = new BrowserSessionDirectory(_root);
        var second = new BrowserSessionDirectory(_root);
        Assert.False(Directory.Exists(_root));
        Assert.NotEqual(first.Path, second.Path);
        Assert.True(Guid.TryParseExact(System.IO.Path.GetFileName(first.Path), "N", out _));
        var firstPath = first.EnsureCreated();
        File.WriteAllText(System.IO.Path.Combine(firstPath, "Cookies"), "old session");
        var secondPath = second.EnsureCreated();
        Assert.Equal(firstPath, first.EnsureCreated());
        Assert.False(File.Exists(System.IO.Path.Combine(secondPath, "Cookies")));
    }

    [Fact]
    public void RetiringOneSessionDeletesOnlyItsOwnedFolderAndNeverAnotherLiveSession()
    {
        var first = new BrowserSessionDirectory(_root);
        var second = new BrowserSessionDirectory(_root);
        Assert.True(Directory.Exists(first.EnsureCreated()));
        Assert.True(Directory.Exists(second.EnsureCreated()));
        Directory.CreateDirectory(System.IO.Path.Combine(first.Path, "Default"));
        File.WriteAllText(System.IO.Path.Combine(first.Path, "Default", "Cookies"), "old");
        Assert.True(first.RetireAndTryCleanup());
        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
        Assert.Equal(0, BrowserSessionDirectory.CleanupRetired(_root));
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public void MissingOrChangedOwnershipMarkerPreventsDeletion()
    {
        var session = new BrowserSessionDirectory(_root);
        Assert.True(Directory.Exists(session.EnsureCreated()));
        File.WriteAllText(System.IO.Path.Combine(session.Path, BrowserSessionDirectory.OwnershipFileName), "foreign");
        Assert.False(session.RetireAndTryCleanup());
        Assert.True(Directory.Exists(session.Path));
    }

    [Fact]
    public void LockedFileDefersCleanupUntilALaterPassWithoutTouchingLiveFolders()
    {
        var session = new BrowserSessionDirectory(_root);
        Assert.True(Directory.Exists(session.EnsureCreated()));
        var file = System.IO.Path.Combine(session.Path, "locked");
        using (var locked = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.False(session.RetireAndTryCleanup());
            Assert.True(Directory.Exists(session.Path));
        }
        Assert.Equal(1, BrowserSessionDirectory.CleanupRetired(_root));
        Assert.False(Directory.Exists(session.Path));
    }

    [Fact]
    public void CleanupRefusesJunctionInsideSessionAndPreservesExternalFiles()
    {
        var session = new BrowserSessionDirectory(_root);
        Assert.True(Directory.Exists(session.EnsureCreated()));
        var outside = System.IO.Path.Combine(_root, "external");
        Directory.CreateDirectory(outside);
        File.WriteAllText(System.IO.Path.Combine(outside, "keep.txt"), "keep");
        var junction = System.IO.Path.Combine(session.Path, "linked");
        CreateJunction(junction, outside);
        try
        {
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal("keep", File.ReadAllText(System.IO.Path.Combine(junction, "keep.txt")));
            Assert.False(session.RetireAndTryCleanup());
            Assert.True(File.Exists(System.IO.Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            // Remove only the link before fixture-root cleanup, including failed assertions.
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Create the actual mount-point reparse data synchronously. A PowerShell child
        // can outlive its timeout on CI and race fixture cleanup with an unfinished link.
        // Junctions, unlike symbolic links, do not require the symbolic-link privilege.
        var substituteName = Encoding.Unicode.GetBytes(@"\??\" + System.IO.Path.GetFullPath(target));
        var printName = Encoding.Unicode.GetBytes(System.IO.Path.GetFullPath(target));
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
            writer.Write(checked((ushort)(8 + substituteName.Length + 2 + printName.Length + 2)));
            writer.Write((ushort)0); // Reserved
            writer.Write((ushort)0); // SubstituteNameOffset, relative to PathBuffer
            writer.Write(checked((ushort)substituteName.Length));
            writer.Write(checked((ushort)(substituteName.Length + 2))); // PrintNameOffset
            writer.Write(checked((ushort)printName.Length));
            writer.Write(substituteName);
            writer.Write((ushort)0);
            writer.Write(printName);
            writer.Write((ushort)0);
        }

        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open junction fixture directory");
        var bytes = buffer.ToArray();
        if (!DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Set junction fixture reparse point");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] input,
        int inputSize, IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}


