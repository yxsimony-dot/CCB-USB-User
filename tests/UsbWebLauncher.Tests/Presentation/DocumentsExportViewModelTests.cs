using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class DocumentsExportViewModelTests
{
    [Fact]
    public void ExportRequiresInitializationAndDelegateButNotLogin()
    {
        var model = new LauncherViewModel();
        Assert.False(model.CanExportDocuments);
        Assert.False(model.ExportDocumentsCommand.CanExecute(null));
        model.ConfigureDocumentsExportCommand((_, _) => Task.CompletedTask);
        Assert.False(model.CanExportDocuments);
        model.UpdateBusinessState(true, false, false, false);
        Assert.True(model.CanExportDocuments);
        Assert.True(model.ExportDocumentsCommand.CanExecute(null));

        var unconfigured = new LauncherViewModel();
        unconfigured.UpdateBusinessState(true, false, false, false);
        Assert.False(unconfigured.CanExportDocuments);
    }

    [Fact]
    public async Task CapturesSelectedMonthBeforePublishingBusyAndForwardsCancellation()
    {
        var model = Ready();
        model.SelectedExportNumber = 6;
        using var source = new CancellationTokenSource();
        var month = 0;
        CancellationToken received = default;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.IsExportBusy) && model.IsExportBusy)
                model.SelectedExportNumber = 9;
        };
        model.ConfigureDocumentsExportCommand((selected, token) =>
        {
            month = selected;
            received = token;
            return Task.CompletedTask;
        });

        await model.ExportDocumentsCommand.ExecuteAsync(source.Token);

        Assert.Equal(6, month);
        Assert.Equal(source.Token, received);
        Assert.False(model.IsExportBusy);
    }

    [Fact]
    public async Task RunningExportExcludesOtherActionsAndRestoresAfterCompletion()
    {
        var model = Ready();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        model.ConfigureDocumentsExportCommand(async (_, _) => { calls++; await finished.Task; });
        Assert.True(model.CanValidateAndSavePin);
        Assert.True(model.CanInstallNativePromptBroker);
        Assert.True(model.CanUninstallNativePromptBroker);
        var operation = model.ExportDocumentsCommand.ExecuteAsync();

        Assert.True(model.IsExportBusy);
        Assert.True(model.IsBusy);
        Assert.False(model.CanExportDocuments);
        Assert.False(model.CanStartBusiness);
        Assert.False(model.CanValidateAndSavePin);
        Assert.False(model.CanResetPin);
        Assert.False(model.CanDeletePin);
        Assert.False(model.CanInstallNativePromptBroker);
        Assert.False(model.CanUninstallNativePromptBroker);
        await model.ExportDocumentsCommand.ExecuteAsync();
        Assert.Equal(1, calls);
        // Exporter completion notifications cannot release the command's exclusion early.
        model.UpdateExportState(false, "下载完成。");
        Assert.True(model.IsBusy);
        finished.SetResult();
        await operation;

        Assert.False(model.IsExportBusy);
        Assert.False(model.IsBusy);
        Assert.True(model.CanExportDocuments);
        Assert.True(model.CanStartBusiness);
        Assert.True(model.CanValidateAndSavePin);
        Assert.True(model.CanInstallNativePromptBroker);
        Assert.Equal("下载完成。", model.ExportStatusText);
    }

    [Fact]
    public async Task FailureRestoresAvailabilityWithoutChangingBusinessStatus()
    {
        var model = Ready();
        var original = model.StatusText;
        model.ConfigureDocumentsExportCommand((_, _) =>
        {
            model.UpdateExportState(true, "正在查询流水。");
            return Task.FromException(new InvalidOperationException("failure"));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ExportDocumentsCommand.ExecuteAsync());

        Assert.False(model.IsBusy);
        Assert.True(model.CanExportDocuments);
        Assert.Equal(original, model.StatusText);
        Assert.Equal("正在查询流水。", model.ExportStatusText);
        model.UpdateExportState(false, null);
        Assert.Equal(string.Empty, model.ExportStatusText);
    }

    [Fact]
    public void AuthenticationAndBusinessOperationsTemporarilyDisableExport()
    {
        var model = Ready();
        model.ConfigureDocumentsExportCommand((_, _) => Task.CompletedTask);
        model.UpdateBusinessState(true, false, false, true);
        Assert.False(model.CanExportDocuments);
        model.UpdateBusinessState(true, false, false, false);
        model.UpdateAuthenticationState(new(AuthenticationStage.Authenticating, null));
        Assert.False(model.CanExportDocuments);
        model.UpdateAuthenticationState(new(AuthenticationStage.Ready, null));
        Assert.True(model.CanExportDocuments);
    }

    [Fact]
    public async Task KeepsExclusiveOwnershipThroughBothStagesAndFinalCleanup()
    {
        var model = Ready();
        var statementFinished = NewSignal();
        var receiptStarted = NewSignal();
        var receiptFinished = NewSignal();
        var cleanupStarted = NewSignal();
        var cleanupFinished = NewSignal();
        var calls = 0;
        model.ConfigureDocumentsExportCommand(async (_, _) =>
        {
            calls++;
            await statementFinished.Task;
            model.UpdateExportState(false, "流水已保存，开始回单。");
            receiptStarted.SetResult();
            await receiptFinished.Task;
            model.UpdateExportState(false, "流水和回单已保存。");
            cleanupStarted.SetResult();
            await cleanupFinished.Task;
        });

        var operation = model.ExportDocumentsCommand.ExecuteAsync();
        try
        {
            statementFinished.SetResult();
            await receiptStarted.Task;
            Assert.True(model.IsBusy);
            Assert.False(model.CanExportDocuments);
            Assert.False(model.CanStartBusiness);
            Assert.False(model.CanValidateAndSavePin);
            Assert.False(model.CanInstallNativePromptBroker);
            await model.ExportDocumentsCommand.ExecuteAsync();

            receiptFinished.SetResult();
            await cleanupStarted.Task;
            Assert.True(model.IsBusy);
            Assert.False(model.CanExportDocuments);
            await model.ExportDocumentsCommand.ExecuteAsync();
            Assert.Equal(1, calls);
        }
        finally
        {
            statementFinished.TrySetResult();
            receiptFinished.TrySetResult();
            cleanupFinished.TrySetResult();
            await operation;
        }

        Assert.False(model.IsBusy);
        Assert.True(model.CanExportDocuments);
        Assert.Equal("流水和回单已保存。", model.ExportStatusText);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OwnsBusyAndCapturesMonthBeforeCommandNotificationsCanReenter()
    {
        var model = Ready();
        model.SelectedExportNumber = 9;
        var finished = NewSignal();
        var calls = 0;
        var month = 0;
        Task? repeated = null;
        var notified = false;
        model.ConfigureDocumentsExportCommand(async (selected, _) =>
        {
            calls++;
            month = selected;
            await finished.Task;
        });
        model.ExportDocumentsCommand.CanExecuteChanged += (_, _) =>
        {
            if (notified) return;
            notified = true;
            Assert.True(model.IsBusy);
            model.SelectedExportNumber = 10;
            repeated = model.ExportDocumentsCommand.ExecuteAsync();
        };

        var operation = model.ExportDocumentsCommand.ExecuteAsync();
        try
        {
            Assert.Equal(9, month);
            Assert.Equal(1, calls);
            Assert.NotNull(repeated);
            Assert.True(repeated.IsCompletedSuccessfully);
        }
        finally
        {
            finished.TrySetResult();
            await operation;
        }
    }

    [Fact]
    public async Task CancellationReleasesCombinedCommandAndPreservesPartialResultMessage()
    {
        var model = Ready();
        var originalBusinessStatus = model.StatusText;
        model.ConfigureDocumentsExportCommand((_, _) =>
        {
            model.UpdateExportState(false, "流水已保存；回单已取消。");
            return Task.FromCanceled(new CancellationToken(true));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ExportDocumentsCommand.ExecuteAsync());

        Assert.False(model.IsBusy);
        Assert.True(model.CanExportDocuments);
        Assert.Equal("流水已保存；回单已取消。", model.ExportStatusText);
        Assert.Equal(originalBusinessStatus, model.StatusText);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LauncherViewModel Ready()
    {
        var model = new LauncherViewModel(new AvailablePreparationCommands());
        model.ConfigureBusinessCommand(_ => Task.CompletedTask);
        model.UpdateBusinessState(true, false, false, false);
        return model;
    }

    private sealed class AvailablePreparationCommands : ILocalPinCommands, INativePromptBrokerCommands
    {
        public event EventHandler? StateChanged { add { } remove { } }
        public NativePromptBrokerPrerequisiteState CurrentState => new(true, true, true, "已安装");
        public Task ValidateAndSaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UninstallNativePromptBrokerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
