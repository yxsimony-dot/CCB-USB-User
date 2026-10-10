using System.IO;
using System.Text.Json;

namespace UsbWebLauncher.App.Configuration;

public sealed class DownloadDirectorySettings
{
    private readonly string settingsFile;

    public DownloadDirectorySettings() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbWebLauncher", "download-settings.json"),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) { }

    public DownloadDirectorySettings(string settingsFile, string defaultDirectory)
    {
        this.settingsFile = settingsFile;
        DirectoryPath = defaultDirectory;
        try
        {
            if (!File.Exists(settingsFile)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile));
            if (string.IsNullOrWhiteSpace(saved?.DirectoryPath) || !Path.IsPathFullyQualified(saved.DirectoryPath))
                throw new InvalidDataException();
            DirectoryPath = Path.GetFullPath(saved.DirectoryPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            LoadError = "下载目录设置无法读取，请点击“下载目录”重新选择。当前默认目录：" + defaultDirectory;
        }
    }

    public string DirectoryPath { get; private set; }
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;

    public string GetWritableDirectory()
    {
        VerifyWritable(DirectoryPath);
        return DirectoryPath;
    }

    public void Save(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择完整的文件夹路径。", nameof(directory));
        var selected = Path.GetFullPath(directory);
        VerifyWritable(selected);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var temporary = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(selected)));
            File.Move(temporary, settingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        DirectoryPath = selected;
        LoadError = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void VerifyWritable(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("下载目录不存在，请重新选择。");
        var probe = Path.Combine(directory, ".download-write-check-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
    }

    private sealed record Settings(string DirectoryPath);
}
