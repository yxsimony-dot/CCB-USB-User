using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Composition;

public sealed class InstallerCommandRunnerTests
{
    [Fact]
    public async Task HealthOnlyUsesReadOnlyProbe()
    {
        var prerequisites = new Prerequisites();
        var runner = CreateRunner(prerequisites, _ => throw new Exception("Unexpected prepare"),
            (_, _) => throw new Exception("Unexpected elevation"));
        Assert.Equal(0, await runner.RunAsync(InstallerCommand.HealthCheck));
        Assert.Equal(new[] { "health" }, prerequisites.Calls);
    }

    [Fact]
    public async Task UnhealthyBrokerReturnsFailure()
    {
        var runner = CreateRunner(new Prerequisites { Healthy = false }, _ => Task.FromResult(true), (_, _) => Task.FromResult(true));
        Assert.NotEqual(0, await runner.RunAsync(InstallerCommand.HealthCheck));
    }

    [Fact]
    public async Task InvalidCommandCannotRunAnyOperation()
    {
        var prerequisites = new Prerequisites();
        var runner = CreateRunner(prerequisites, _ => throw new Exception(), (_, _) => throw new Exception());
        Assert.NotEqual(0, await runner.RunAsync(InstallerCommand.Invalid));
        Assert.Empty(prerequisites.Calls);
    }

    [Fact]
    public async Task RejectedOwnerStopsRegistrationBeforeElevation()
    {
        var prerequisites = new Prerequisites();
        var runner = CreateRunner(prerequisites, _ => Task.FromResult(false), (_, _) => Task.FromResult(true));
        Assert.NotEqual(0, await runner.RunAsync(InstallerCommand.RegisterBroker));
        Assert.Empty(prerequisites.Calls);
    }

    [Fact]
    public async Task MutationCannotFinishBeforeElevatedChildExits()
    {
        var childExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(new Prerequisites(), _ => Task.FromResult(true), (_, _) =>
        {
            started.SetResult();
            return childExit.Task;
        });
        var result = runner.RunAsync(InstallerCommand.RestoreBroker);
        await started.Task;
        Assert.False(result.IsCompleted);
        childExit.SetResult(true);
        Assert.Equal(0, await result);
    }

    [Theory]
    [InlineData(InstallerCommand.RestoreBroker, "--restore")]
    [InlineData(InstallerCommand.CommitBroker, "--commit")]
    internal async Task RecoveryUsesProtectedTransactionWhenActiveInstallationIsIncomplete(InstallerCommand command, string expectedVerb)
    {
        string? invokedVerb = null;
        var runner = CreateRunner(new Prerequisites(), _ => Task.FromResult(false), (verb, _) =>
        {
            invokedVerb = verb;
            return Task.FromResult(true);
        });
        Assert.Equal(0, await runner.RunAsync(command));
        Assert.Equal(expectedVerb, invokedVerb);
    }

    [Theory]
    [InlineData(InstallerCommand.RestoreBroker)]
    [InlineData(InstallerCommand.CommitBroker)]
    [InlineData(InstallerCommand.RegisterBroker)]
    internal async Task ElevatedOrNonInteractiveAppCannotInvokeMutation(InstallerCommand command)
    {
        var prerequisites = new Prerequisites();
        var runner = new InstallerCommandRunner(prerequisites,
            _ => throw new Exception("Must not inspect active installation"),
            (_, _) => throw new Exception("Must not launch an elevated transaction"),
            isOrdinaryUser: () => false);
        Assert.NotEqual(0, await runner.RunAsync(command));
        Assert.Empty(prerequisites.Calls);
    }

    private static InstallerCommandRunner CreateRunner(INativePromptBrokerPrerequisites prerequisites,
        Func<CancellationToken, Task<bool>> prepare, Func<string, CancellationToken, Task<bool>> transaction) =>
        new(prerequisites, prepare, transaction, isOrdinaryUser: () => true);

    private sealed class Prerequisites : INativePromptBrokerPrerequisites
    {
        public List<string> Calls { get; } = [];
        public bool Healthy { get; init; } = true;
        public event EventHandler? StateChanged { add { } remove { } }
        public NativePromptBrokerPrerequisiteState CurrentState => NativePromptBrokerPrerequisiteState.NotRequired;
        public Task<bool> RefreshHealthAsync(CancellationToken cancellationToken = default) { Calls.Add("health"); return Task.FromResult(Healthy); }
        public Task<bool> InstallAsync(CancellationToken cancellationToken = default) { Calls.Add("install"); return Task.FromResult(true); }
        public Task<bool> UninstallAsync(CancellationToken cancellationToken = default) { Calls.Add("uninstall"); return Task.FromResult(true); }
    }
}
