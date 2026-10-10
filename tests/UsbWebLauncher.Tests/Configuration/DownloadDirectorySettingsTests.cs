using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.Tests.Configuration;

public sealed class DownloadDirectorySettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "download-settings-tests", Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(root, "settings", "downloads.json");
    private string DefaultDirectory => Path.Combine(root, "desktop");

    public DownloadDirectorySettingsTests() => Directory.CreateDirectory(DefaultDirectory);

    [Fact]
    public void MissingSettingsUsesDefaultWithoutWritingSettings()
    {
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        Assert.Equal(DefaultDirectory, settings.DirectoryPath);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void SelectedDirectorySurvivesRestartAndLeavesNoProbeFiles()
    {
        var selected = Directory.CreateDirectory(Path.Combine(root, "中文 文件夹")).FullName;
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        settings.Save(selected);
        Assert.Equal(selected, new DownloadDirectorySettings(SettingsFile, DefaultDirectory).DirectoryPath);
        Assert.Empty(Directory.GetFiles(selected));
    }

    [Fact]
    public void InvalidSelectionKeepsPreviousSavedDirectory()
    {
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        settings.Save(DefaultDirectory);
        Assert.Throws<DirectoryNotFoundException>(() => settings.Save(Path.Combine(root, "missing")));
        Assert.Equal(DefaultDirectory, settings.DirectoryPath);
        Assert.Equal(DefaultDirectory, new DownloadDirectorySettings(SettingsFile, DefaultDirectory).DirectoryPath);
    }

    [Fact]
    public void FailedPersistenceDoesNotChangeActiveDirectory()
    {
        var selected = Directory.CreateDirectory(Path.Combine(root, "new")).FullName;
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        Directory.CreateDirectory(SettingsFile);
        Assert.Throws<UnauthorizedAccessException>(() => settings.Save(selected));
        Assert.Equal(DefaultDirectory, settings.DirectoryPath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsFile)!));
    }

    [Fact]
    public void UnavailableSavedDirectoryIsNotSilentlyReplaced()
    {
        var selected = Directory.CreateDirectory(Path.Combine(root, "removable")).FullName;
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        settings.Save(selected);
        Directory.Delete(selected);
        var reopened = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        Assert.Equal(selected, reopened.DirectoryPath);
        Assert.Throws<DirectoryNotFoundException>(() => reopened.GetWritableDirectory());
        Assert.False(Directory.Exists(selected));
    }

    [Fact]
    public void BrokenSettingsReportsLoadErrorAndAllowsRepair()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, "{broken");
        var settings = new DownloadDirectorySettings(SettingsFile, DefaultDirectory);
        Assert.NotNull(settings.LoadError);
        settings.Save(DefaultDirectory);
        Assert.Null(settings.LoadError);
        Assert.Null(new DownloadDirectorySettings(SettingsFile, DefaultDirectory).LoadError);
    }

    public void Dispose() => Directory.Delete(root, true);
}
