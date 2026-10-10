using UsbWebLauncher.App.Composition;

namespace UsbWebLauncher.Tests.Composition;

public sealed class InstallerLaunchTests
{
    [Theory]
    [InlineData("--installer-health-check", "HealthCheck")]
    [InlineData("--installer-register-broker", "RegisterBroker")]
    [InlineData("--installer-uninstall-broker", "UninstallBroker")]
    [InlineData("--installer-prepare", "Prepare")]
    [InlineData("--installer-backup-broker", "BackupBroker")]
    [InlineData("--installer-restore-broker", "RestoreBroker")]
    [InlineData("--installer-commit-broker", "CommitBroker")]
    public void InstallerCommandsAreHeadless(string argument, string expected)
    {
        var options = ApplicationLaunchOptions.Parse([argument]);
        Assert.Equal(expected, options.InstallerCommand.ToString());
        Assert.True(options.IsHeadless);
    }

    [Theory]
    [InlineData("--installer-unknown")]
    [InlineData("--installer-health-check", "--smoke-test-no-devices")]
    [InlineData("--installer-first-run", "--installer-health-check")]
    public void MalformedInstallerInvocationNeverFallsThroughToBusiness(params string[] args)
    {
        var options = ApplicationLaunchOptions.Parse(args);
        Assert.True(options.IsHeadless);
        Assert.Equal("Invalid", options.InstallerCommand.ToString());
    }

    [Fact]
    public void FirstRunIsPreparationOnly()
    {
        var options = ApplicationLaunchOptions.Parse(["--installer-first-run"]);
        Assert.True(options.ShowDependencyPreparation);
        Assert.False(options.IsHeadless);
    }
}
