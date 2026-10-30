using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class LocalPinCommandsTests
{
    [Fact]
    public void Dispose_DetachesPrerequisiteSubscriptionAndClearsOutwardSubscribers()
    {
        var prerequisites = new RecordingBrokerPrerequisites([]);
        var commands = new LocalPinCommands(
            new RecordingPinDialog(null),
            new RecordingPinSetupService(),
            prerequisites);
        var notifications = 0;
        commands.StateChanged += (_, _) => notifications++;

        Assert.Equal(1, prerequisites.SubscriptionCount);
        prerequisites.PublishStateChanged();
        Assert.Equal(1, notifications);

        commands.Dispose();
        commands.Dispose();
        prerequisites.PublishStateChanged();

        Assert.Equal(0, prerequisites.SubscriptionCount);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void SessionReplacement_ReleasesPreviousPrerequisiteSubscription()
    {
        var prerequisites = new RecordingBrokerPrerequisites([]);
        var first = new LocalPinCommands(
            new RecordingPinDialog(null),
            new RecordingPinSetupService(),
            prerequisites);
        Assert.Equal(1, prerequisites.SubscriptionCount);

        first.Dispose();
        using var replacement = new LocalPinCommands(
            new RecordingPinDialog(null),
            new RecordingPinSetupService(),
            prerequisites);

        Assert.Equal(1, prerequisites.SubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinSetup_WhenBrokerMissing_RequestsExplicitInstallationWithoutOpeningDialog(bool reset)
    {
        var events = new List<string>();
        var prerequisites = new RecordingBrokerPrerequisites(events) { HealthResult = false };
        var dialog = new RecordingPinDialog(null, events);
        var setup = new RecordingPinSetupService(events);
        var commands = new LocalPinCommands(dialog, setup, prerequisites: prerequisites);

        if (reset)
        {
            await commands.ResetAsync();
        }
        else
        {
            await commands.ValidateAndSaveAsync();
        }

        Assert.Equal(["Health"], events);
        Assert.Equal(0, dialog.ShowCount);
        Assert.False(commands.CurrentState.IsHealthy);
        Assert.False(commands.CurrentState.IsInstalled);
        Assert.Contains("请先点击安装环境", commands.CurrentState.StatusText);
    }

    [Fact]
    public async Task InstallNativePromptBrokerAsync_WhenUacIsCancelled_DoesNotOpenPinDialog()
    {
        var events = new List<string>();
        var prerequisites = new RecordingBrokerPrerequisites(events) { InstallResult = false };
        var dialog = new RecordingPinDialog(null, events);
        var commands = new LocalPinCommands(
            dialog,
            new RecordingPinSetupService(events),
            prerequisites: prerequisites);

        await commands.InstallNativePromptBrokerAsync();

        Assert.Equal(["Install"], events);
        Assert.Equal(0, dialog.ShowCount);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenBrokerVersionIsUnhealthy_DoesNotOpenPinDialog()
    {
        var events = new List<string>();
        var prerequisites = new RecordingBrokerPrerequisites(events) { HealthResult = false };
        var dialog = new RecordingPinDialog(null, events);
        var commands = new LocalPinCommands(
            dialog,
            new RecordingPinSetupService(events),
            prerequisites: prerequisites);

        await commands.ValidateAndSaveAsync();

        Assert.Equal(["Health"], events);
        Assert.Equal(0, dialog.ShowCount);
    }

    [Fact]
    public async Task InstallNativePromptBrokerAsync_InstallsAndChecksHealthWithoutOpeningPinDialog()
    {
        var events = new List<string>();
        var prerequisites = new RecordingBrokerPrerequisites(events);
        var commands = new LocalPinCommands(
            new RecordingPinDialog(null, events),
            new RecordingPinSetupService(events),
            prerequisites: prerequisites);

        await commands.InstallNativePromptBrokerAsync();

        Assert.Equal(["Install", "Health"], events);
        Assert.True(commands.CurrentState.IsHealthy);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenDialogIsCancelled_DoesNotStartSetup()
    {
        var dialog = new RecordingPinDialog(null);
        var setup = new RecordingPinSetupService();
        var commands = new LocalPinCommands(dialog, setup);

        await commands.ValidateAndSaveAsync();

        Assert.Equal(1, dialog.ShowCount);
        Assert.Equal(0, setup.SaveFixedCount);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_PassesDisposablePinDirectlyToSetupService()
    {
        using var pin = ClientCertificatePin.CopyOf(
            Guid.NewGuid().ToString("N").AsSpan());
        var dialog = new RecordingPinDialog(pin);
        var setup = new RecordingPinSetupService();
        var commands = new LocalPinCommands(dialog, setup);

        await commands.ValidateAndSaveAsync();

        Assert.Same(pin, setup.ReceivedPin);
        Assert.Equal(1, setup.SaveFixedCount);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ResetAsync_UsesTheSameFixedCredentialFlow()
    {
        using var pin = ClientCertificatePin.CopyOf("replacement-test-pin".AsSpan());
        var dialog = new RecordingPinDialog(pin);
        var setup = new RecordingPinSetupService();
        var commands = new LocalPinCommands(dialog, setup);

        await commands.ResetAsync();

        Assert.Same(pin, setup.ReceivedPin);
        Assert.Equal(1, setup.SaveFixedCount);
    }

    [Fact]
    public async Task DeleteAsync_DeletesStoresWithoutOpeningPinDialog()
    {
        var dialog = new RecordingPinDialog(null);
        var setup = new RecordingPinSetupService();
        var commands = new LocalPinCommands(dialog, setup);

        await commands.DeleteAsync();

        Assert.Equal(0, dialog.ShowCount);
        Assert.Equal(1, setup.DeleteCount);
    }

    [Fact]
    public async Task DeleteAsync_AfterSavingPin_ReplacesSavedStatusWithDeletedStatus()
    {
        using var pin = ClientCertificatePin.CopyOf(Guid.NewGuid().ToString("N").AsSpan());
        var dialog = new RecordingPinDialog(pin);
        var setup = new RecordingPinSetupService();
        var commands = new LocalPinCommands(dialog, setup);
        await commands.ValidateAndSaveAsync();
        var statusUpdates = new List<string>();
        commands.StateChanged += (_, _) => statusUpdates.Add(commands.CurrentState.StatusText);

        await commands.DeleteAsync();

        Assert.Equal(1, setup.DeleteCount);
        Assert.Equal(1, dialog.ShowCount);
        Assert.DoesNotContain("PIN 已保存", commands.CurrentState.StatusText);
        Assert.Contains("已删除", commands.CurrentState.StatusText);
        Assert.Contains(statusUpdates, status => status.Contains("已删除"));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_AfterFixedCredentialSave_OnlySavesAndWaitsForExplicitStart()
    {
        using var pin = ClientCertificatePin.CopyOf(Guid.NewGuid().ToString("N").AsSpan());
        var events = new List<string>();
        var setup = new RecordingPinSetupService(events);
        var commands = new LocalPinCommands(
            new RecordingPinDialog(pin, events),
            setup,
            prerequisites: new RecordingBrokerPrerequisites(events));

        await commands.ValidateAndSaveAsync();

        Assert.Equal(["Health", "ShowPinDialog", "SaveFixed"], events);
        Assert.Equal(1, setup.SaveFixedCount);
        Assert.Equal(0, setup.ValidateCount);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
        Assert.Contains("启动业务", commands.CurrentState.StatusText);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_AfterFixedCredentialSave_PublishesWaitingForStartStatus()
    {
        using var pin = ClientCertificatePin.CopyOf(Guid.NewGuid().ToString("N").AsSpan());
        var commands = new LocalPinCommands(
            new RecordingPinDialog(pin),
            new RecordingPinSetupService());
        var statusUpdates = new List<string>();
        commands.StateChanged += (_, _) => statusUpdates.Add(commands.CurrentState.StatusText);

        await commands.ValidateAndSaveAsync();

        Assert.Contains(statusUpdates, status => status.Contains("启动业务"));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenCredentialSaveFails_DoesNotPublishSavedStatus()
    {
        using var pin = ClientCertificatePin.CopyOf(Guid.NewGuid().ToString("N").AsSpan());
        var setup = new RecordingPinSetupService { SaveFixedResult = false };
        var commands = new LocalPinCommands(
            new RecordingPinDialog(pin),
            setup);

        await commands.ValidateAndSaveAsync();

        Assert.Equal(1, setup.SaveFixedCount);
        Assert.Equal(0, setup.ValidateCount);
        Assert.DoesNotContain("PIN 已保存", commands.CurrentState.StatusText);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenCredentialSaveThrows_ClearsThePin()
    {
        var secret = Guid.NewGuid().ToString("N");
        using var pin = ClientCertificatePin.CopyOf(secret.AsSpan());
        var commands = new LocalPinCommands(
            new RecordingPinDialog(pin),
            new RecordingPinSetupService
            {
                SaveFixedException = new IOException("synthetic save failure")
            });

        await Assert.ThrowsAsync<IOException>(
            () => commands.ValidateAndSaveAsync());

        Assert.DoesNotContain(secret, commands.CurrentState.StatusText);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenCancelledDuringSave_ClearsThePin()
    {
        var secret = Guid.NewGuid().ToString("N");
        using var pin = ClientCertificatePin.CopyOf(secret.AsSpan());
        var commands = new LocalPinCommands(
            new RecordingPinDialog(pin),
            new RecordingPinSetupService
            {
                SaveFixedException = new OperationCanceledException()
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => commands.ValidateAndSaveAsync());

        Assert.DoesNotContain(secret, commands.CurrentState.StatusText);
        Assert.All(pin.AsMemory().ToArray(), character => Assert.Equal('\0', character));
    }

    private sealed class RecordingPinDialog(
        ClientCertificatePin? result,
        List<string>? events = null) : ILocalPinDialog
    {
        public int ShowCount { get; private set; }

        public Task<ClientCertificatePin?> ShowAsync(CancellationToken cancellationToken = default)
        {
            ShowCount++;
            events?.Add("ShowPinDialog");
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingPinSetupService(List<string>? events = null) : IClientCertificatePinSetupService
    {
        public ClientCertificateSetupValidationResult Result { get; init; } =
            ClientCertificateSetupValidationResult.Succeeded();
        public int ValidateCount { get; private set; }
        public int SaveFixedCount { get; private set; }
        public bool SaveFixedResult { get; init; } = true;
        public Exception? SaveFixedException { get; init; }
        public int DeleteCount { get; private set; }
        public ClientCertificatePin? ReceivedPin { get; private set; }

        public Task<ClientCertificateSetupValidationResult> ValidateAndSaveAsync(
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            events?.Add("Validate");
            ReceivedPin = pin;
            return Task.FromResult(Result);
        }

        public Task<bool> SaveFixedAsync(
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            SaveFixedCount++;
            events?.Add("SaveFixed");
            ReceivedPin = pin;
            if (SaveFixedException is not null)
            {
                return Task.FromException<bool>(SaveFixedException);
            }

            return Task.FromResult(SaveFixedResult);
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBrokerPrerequisites(List<string> events)
        : INativePromptBrokerPrerequisites
    {
        private NativePromptBrokerPrerequisiteState _state = new(
            IsRequired: true,
            IsHealthy: false,
            IsInstalled: false,
            "需要安装本机认证组件。");

        private EventHandler? _stateChanged;

        public event EventHandler? StateChanged
        {
            add
            {
                _stateChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                _stateChanged -= value;
                SubscriptionCount--;
            }
        }

        public bool InstallResult { get; set; } = true;
        public bool HealthResult { get; set; } = true;
        public int SubscriptionCount { get; private set; }
        public NativePromptBrokerPrerequisiteState CurrentState => _state;

        public void PublishStateChanged() => _stateChanged?.Invoke(this, EventArgs.Empty);

        public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        {
            events.Add("Install");
            if (InstallResult)
            {
                _state = _state with { IsInstalled = true };
                PublishStateChanged();
            }

            return Task.FromResult(InstallResult);
        }

        public Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default)
        {
            events.Add("Health");
            _state = _state with { IsHealthy = HealthResult, IsInstalled = HealthResult };
            PublishStateChanged();
            return Task.FromResult(HealthResult);
        }

        public Task<bool> UninstallAsync(CancellationToken cancellationToken = default)
        {
            _state = _state with { IsHealthy = false, IsInstalled = false };
            PublishStateChanged();
            return Task.FromResult(true);
        }
    }
}
