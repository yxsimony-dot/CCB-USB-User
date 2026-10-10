using System.Diagnostics;
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
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        Assert.False(session.RetireAndTryCleanup());
        Assert.True(File.Exists(System.IO.Path.Combine(outside, "keep.txt")));
        Directory.Delete(junction);
    }

    private static void CreateJunction(string link, string target)
    {
        // Junction creation does not require the symbolic-link privilege on Windows.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}


