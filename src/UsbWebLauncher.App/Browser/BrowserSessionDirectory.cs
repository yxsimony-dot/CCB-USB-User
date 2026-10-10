using System.IO;

namespace UsbWebLauncher.App.Browser;

public sealed class BrowserSessionDirectory
{
    public const string OwnershipFileName = ".usb-web-launcher-session";
    public const string RetiredFileName = ".usb-web-launcher-retired";
    private readonly string _root;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _created;

    public BrowserSessionDirectory(string? root = null)
    {
        _root = System.IO.Path.GetFullPath(root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbWebLauncher", "browser-sessions"));
        Path = System.IO.Path.Combine(_root, _id);
    }

    public string Path { get; }

    public string EnsureCreated()
    {
        if (_created) return Path;
        if (HasReparseAncestor(_root))
            throw new IOException("Browser session storage cannot use a redirected directory.");
        Directory.CreateDirectory(_root);
        if (Directory.Exists(Path) || File.Exists(Path))
            throw new IOException("Browser session storage already exists.");
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, OwnershipFileName), _id);
        _created = true;
        return Path;
    }

    /// <summary>Call only after this environment's browser processes have released the folder.</summary>
    public bool RetireAndTryCleanup()
    {
        if (!_created) return true;
        try
        {
            if (!IsOwned(_root, Path)) return false;
            File.WriteAllText(System.IO.Path.Combine(Path, RetiredFileName), _id);
            return TryCleanup(_root, Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static int CleanupRetired(string root)
    {
        try
        {
            root = System.IO.Path.GetFullPath(root);
            if (!Directory.Exists(root) || HasReparseAncestor(root)) return 0;
            return Directory.EnumerateDirectories(root).Count(path => TryCleanup(root, path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool TryCleanup(string root, string path)
    {
        try
        {
            if (!IsOwned(root, path)) return false;
            var retired = System.IO.Path.Combine(path, RetiredFileName);
            if (!File.Exists(retired) || IsReparsePoint(retired) ||
                File.ReadAllText(retired) != System.IO.Path.GetFileName(path) || ContainsReparsePoint(path)) return false;

            // Keep both ownership markers if a browser file is still locked, so a later
            // pass can retry without ever considering unmarked or live directories.
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                var name = System.IO.Path.GetFileName(child);
                if (name is OwnershipFileName or RetiredFileName) continue;
                if (Directory.Exists(child)) Directory.Delete(child, recursive: true);
                else File.Delete(child);
            }
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsOwned(string root, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(path), root.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(System.IO.Path.GetFileName(path), "N", out _) ||
            !Directory.Exists(path) || HasReparseAncestor(path)) return false;
        var marker = System.IO.Path.Combine(path, OwnershipFileName);
        return File.Exists(marker) && !IsReparsePoint(marker) &&
            File.ReadAllText(marker) == System.IO.Path.GetFileName(path);
    }

    private static bool ContainsReparsePoint(string path)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsReparsePoint(child)) return true;
            if (Directory.Exists(child) && ContainsReparsePoint(child)) return true;
        }
        return false;
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
