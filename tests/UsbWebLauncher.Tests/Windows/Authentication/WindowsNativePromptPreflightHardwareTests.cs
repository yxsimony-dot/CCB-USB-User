using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

[Collection(UsbHardwareCollection.Name)]
public sealed class WindowsNativePromptPreflightHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task TaskInspection_WhenExplicitHardwareTestIsEnabled_TreatsMissingTaskAsAbsent()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var api = new WindowsNativePromptBrokerInstallationApi();

        var exists = await api.TaskExistsAsync(
            $"UsbWebLauncher Missing Test {Guid.NewGuid():N}",
            CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Inspect_WhenExplicitHardwareTestIsEnabled_VerifiesBrokerAndPromptWithoutSendingInput()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            return;
        }

        var profile = LauncherProfileJson.Parse(File.ReadAllText(FindShippedProfile()));
        var prompt = Assert.IsType<UsbWebLauncher.Core.Configuration.ClientCertificateNativePromptOptions>(
            profile.ClientCertificateAuthentication?.NativePromptContinuation);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var brokerConnection = await new WindowsNamedPipeConnectionFactory().ConnectAsync(
            WindowsNativePromptBrokerInstaller.BrokerPipeName,
            timeout.Token);
        var peerStatus = await new WindowsNamedPipePeerInspector().InspectAsync(
            brokerConnection,
            timeout.Token);
        Assert.Equal(NativePromptPeerInspectionStatus.Verified, peerStatus);

        var trustedClientProcessId = 0;
        if (string.Equals(
                prompt.WindowScope,
                NativePromptWindowScopes.TrustedClientDescendant,
                StringComparison.Ordinal) ||
            string.Equals(
                prompt.WindowScope,
                NativePromptWindowScopes.TrustedClientTopLevel,
                StringComparison.Ordinal))
        {
            Assert.True(
                int.TryParse(
                    Environment.GetEnvironmentVariable("USB_WEB_LAUNCHER_TRUSTED_CLIENT_PID"),
                    out trustedClientProcessId) && trustedClientProcessId > 0,
                "Trusted-client descendant preflight requires USB_WEB_LAUNCHER_TRUSTED_CLIENT_PID.");
        }

        var candidates = new WindowsNativePromptApi().EnumerateWindowSnapshots(
            prompt,
            trustedClientProcessId);
        Assert.True(
            NativePromptWindowMatcher.TryFindUniqueMatch(
                prompt,
                candidates,
                trustedClientProcessId,
                out var match),
            $"Expected one exact native prompt match, observed {candidates.Count} candidate(s).");
        Assert.NotNull(match);
    }

    private static string FindShippedProfile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UsbWebLauncher.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, "src", "UsbWebLauncher.App", "launcher-profile.json");
    }
}
