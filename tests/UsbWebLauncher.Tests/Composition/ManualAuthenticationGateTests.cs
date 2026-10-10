using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.Tests.Composition;

public sealed class ManualAuthenticationGateTests
{
    [Fact]
    public async Task PresenceBeforeExplicitStart_DoesNotReadCredentialsOrUnlock()
    {
        await using var fixture = new AuthenticationFixture();

        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));

        Assert.True(fixture.Gate.RequiresExplicitStart);
        Assert.Equal(0, fixture.Pins.ReadCount);
        Assert.Equal(0, fixture.Unlocker.AttemptCount);
        Assert.Null(fixture.Coordinator.CurrentState.InsertionCycleId);
    }

    [Fact]
    public async Task AllowBusiness_DoesNotReplayPreviouslyObservedPresence()
    {
        await using var fixture = new AuthenticationFixture();
        await fixture.Gate.HandleDevicePresenceAsync(true);

        await fixture.Gate.AllowBusinessAsync();

        Assert.False(fixture.Gate.RequiresExplicitStart);
        Assert.Equal(0, fixture.Unlocker.AttemptCount);
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(1, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task RepeatedSaves_AreAppliedOnceOnNextExplicitPresence()
    {
        await using var fixture = new AuthenticationFixture();
        await fixture.StartAsync();
        var originalCycle = fixture.Coordinator.CurrentState.InsertionCycleId;

        await fixture.Gate.ReportPinProvisionedAsync();
        await fixture.Gate.ReportPinProvisionedAsync();
        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.True(fixture.Gate.RequiresExplicitStart);
        Assert.Equal(1, fixture.Unlocker.AttemptCount);
        Assert.Equal(originalCycle, fixture.Coordinator.CurrentState.InsertionCycleId);

        await fixture.Gate.AllowBusinessAsync();
        Assert.Equal(1, fixture.Unlocker.AttemptCount);
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));
        var changedCycle = fixture.Coordinator.CurrentState.InsertionCycleId;
        Assert.NotEqual(originalCycle, changedCycle);
        Assert.Equal(2, fixture.Unlocker.AttemptCount);

        await fixture.Gate.AllowBusinessAsync();
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(changedCycle, fixture.Coordinator.CurrentState.InsertionCycleId);
        Assert.Equal(2, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task Pause_DoesNotInventRemoval_ButActualRemovalStillReleasesResources()
    {
        await using var fixture = new AuthenticationFixture();
        await fixture.StartAsync();
        var session = Assert.Single(fixture.Unlocker.Sessions);
        var cycle = fixture.Coordinator.CurrentState.InsertionCycleId;

        await fixture.Gate.PauseAsync();

        Assert.False(session.Disposed);
        Assert.Equal(cycle, fixture.Coordinator.CurrentState.InsertionCycleId);
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(false));
        Assert.True(session.Disposed);
        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(1, fixture.Unlocker.AttemptCount);

        await fixture.Gate.AllowBusinessAsync();
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(2, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task SavedWhileRemoved_UnlocksOnlyOnceWhenExplicitlyResumed()
    {
        await using var fixture = new AuthenticationFixture();
        await fixture.StartAsync();
        await fixture.Gate.ReportPinProvisionedAsync();
        await fixture.Gate.HandleDevicePresenceAsync(false);
        await fixture.Gate.HandleDevicePresenceAsync(true);

        await fixture.Gate.AllowBusinessAsync();
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));

        Assert.Equal(2, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task MissingPin_RepausesAndReinsertionCannotBypassExplicitContinue()
    {
        await using var fixture = new AuthenticationFixture();
        fixture.Stamps.Stamp = null;
        var changes = new List<bool>();
        fixture.Gate.StateChanged += (_, _) => changes.Add(fixture.Gate.RequiresExplicitStart);
        await fixture.Gate.AllowBusinessAsync();

        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.True(fixture.Gate.RequiresExplicitStart);
        fixture.Stamps.Stamp = fixture.ValidStamp;
        await fixture.Gate.HandleDevicePresenceAsync(false);
        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(0, fixture.Unlocker.AttemptCount);
        Assert.Equal(new[] { false, true }, changes);

        await fixture.Gate.AllowBusinessAsync();
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));
        Assert.Equal(1, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task NormalBusiness_RemovalAndReinsertionDoNotRequireAnotherClick()
    {
        await using var fixture = new AuthenticationFixture();
        await fixture.StartAsync();

        await fixture.Gate.HandleDevicePresenceAsync(false);
        Assert.True(await fixture.Gate.HandleDevicePresenceAsync(true));

        Assert.False(fixture.Gate.RequiresExplicitStart);
        Assert.Equal(2, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task FailedUnlock_RepeatedExplicitClicksCannotResetAttemptBudget()
    {
        await using var fixture = new AuthenticationFixture();
        fixture.Unlocker.Fail = true;
        await fixture.Gate.AllowBusinessAsync();
        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));
        var cycle = fixture.Coordinator.CurrentState.InsertionCycleId;

        await fixture.Gate.AllowBusinessAsync();
        Assert.False(await fixture.Gate.HandleDevicePresenceAsync(true));

        Assert.True(fixture.Gate.RequiresExplicitStart);
        Assert.Equal(cycle, fixture.Coordinator.CurrentState.InsertionCycleId);
        Assert.Equal(1, fixture.Unlocker.AttemptCount);
    }

    [Fact]
    public async Task PresenceException_RepausesBeforePropagatingFailure()
    {
        var inner = new RecordingBoundary { ThrowOnPresence = true };
        var gate = new ManualAuthenticationGate(inner, inner);
        await gate.AllowBusinessAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.HandleDevicePresenceAsync(true));

        Assert.True(gate.RequiresExplicitStart);
        Assert.False(await gate.HandleDevicePresenceAsync(true));
        Assert.Equal(new[] { "present" }, inner.Calls);
    }

    [Fact]
    public async Task Delete_CleansUpImmediatelyAndDiscardsPendingSave()
    {
        var inner = new RecordingBoundary();
        var gate = new ManualAuthenticationGate(inner, inner);
        await gate.AllowBusinessAsync();
        await gate.HandleDevicePresenceAsync(true);
        await gate.ReportPinProvisionedAsync();

        await gate.ReportPinSetupDeletedAsync();

        Assert.True(gate.RequiresExplicitStart);
        Assert.Equal(new[] { "present", "deleted" }, inner.Calls);
        await gate.AllowBusinessAsync();
        await gate.HandleDevicePresenceAsync(true);
        Assert.Equal(new[] { "present", "deleted", "present" }, inner.Calls);
    }

    [Fact]
    public async Task LatestSetupNotification_ReplacesEarlierPendingNotification()
    {
        var inner = new RecordingBoundary();
        var gate = new ManualAuthenticationGate(inner, inner);
        await gate.ReportPinProvisionedAsync();
        await gate.ReportPinSetupSucceededAsync();
        Assert.Empty(inner.Calls);

        await gate.AllowBusinessAsync();
        await gate.HandleDevicePresenceAsync(true);

        Assert.Equal(new[] { "succeeded", "present" }, inner.Calls);
    }

    [Fact]
    public async Task ObserverFailure_RepausesWithoutReplayingConsumedNotification()
    {
        var inner = new RecordingBoundary { ThrowOnProvisioned = true };
        var gate = new ManualAuthenticationGate(inner, inner);
        await gate.ReportPinProvisionedAsync();
        await gate.AllowBusinessAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.HandleDevicePresenceAsync(true));
        Assert.True(gate.RequiresExplicitStart);
        inner.ThrowOnProvisioned = false;
        await gate.AllowBusinessAsync();
        await gate.HandleDevicePresenceAsync(true);

        Assert.Equal(new[] { "provisioned", "present" }, inner.Calls);
    }

    [Fact]
    public async Task RemovalRacingWithDeferredSave_IsSerializedAndStillDelivered()
    {
        var inner = new RecordingBoundary { BlockProvisioned = true };
        var gate = new ManualAuthenticationGate(inner, inner);
        await gate.ReportPinProvisionedAsync();
        await gate.AllowBusinessAsync();
        var present = gate.HandleDevicePresenceAsync(true);
        await inner.ProvisionedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var removed = gate.HandleDevicePresenceAsync(false);
        Assert.False(removed.IsCompleted);
        inner.ReleaseProvisioned.SetResult();
        await Task.WhenAll(present, removed).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "provisioned", "present", "absent" }, inner.Calls);
    }

    private sealed class AuthenticationFixture : IAsyncDisposable
    {
        public AuthenticationFixture()
        {
            var options = new ClientCertificateAuthenticationOptions(
                "manual-test", "example.test", 443,
                new("00112233445566778899AABBCCDDEEFF00112233", "CN=Test", "CN=Issuer"),
                new("Test Provider", 1, "Test Container", "AT_KEYEXCHANGE"),
                "UsbWebLauncher/manual-test");
            ValidStamp = new(options.Id, options.GetNormalizedHash());
            Stamps = new(ValidStamp);
            Coordinator = new(options, Pins, Stamps, Unlocker);
            Gate = new(new AuthenticationLauncherGate(Coordinator), Coordinator);
        }

        public MemoryPinStore Pins { get; } = new();
        public MemoryStampStore Stamps { get; }
        public ClientCertificateValidationStamp ValidStamp { get; }
        public RecordingUnlocker Unlocker { get; } = new();
        public AuthenticationCoordinator Coordinator { get; }
        public ManualAuthenticationGate Gate { get; }

        public async Task StartAsync()
        {
            await Gate.AllowBusinessAsync();
            Assert.True(await Gate.HandleDevicePresenceAsync(true));
        }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class MemoryPinStore : IClientCertificatePinStore
    {
        public int ReadCount { get; private set; }
        public ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult<ClientCertificatePin?>(ClientCertificatePin.CopyOf("synthetic-test-pin".AsMemory()));
        }
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class MemoryStampStore(ClientCertificateValidationStamp stamp) : IClientCertificateValidationStampStore
    {
        public ClientCertificateValidationStamp? Stamp { get; set; } = stamp;
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(string authenticationId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Stamp);
        public ValueTask SaveAsync(ClientCertificateValidationStamp value, CancellationToken cancellationToken = default) { Stamp = value; return ValueTask.CompletedTask; }
        public ValueTask DeleteAsync(string authenticationId, CancellationToken cancellationToken = default) { Stamp = null; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingUnlocker : IClientCertificateTokenUnlocker
    {
        public int AttemptCount { get; private set; }
        public bool Fail { get; set; }
        public List<RecordingSession> Sessions { get; } = [];
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(ClientCertificateAuthenticationOptions authentication, ClientCertificatePin pin, CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            if (Fail) return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Failed(ClientCertificateTokenUnlockStatus.UnknownFailure));
            var session = new RecordingSession();
            Sessions.Add(session);
            return ValueTask.FromResult(ClientCertificateTokenUnlockResult.Succeeded(session));
        }
    }

    private sealed class RecordingSession : IClientCertificateTokenUnlockSession
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingBoundary : ILauncherAuthenticationGate, IClientCertificatePinSetupLifecycleObserver
    {
        public List<string> Calls { get; } = [];
        public bool ThrowOnProvisioned { get; set; }
        public bool ThrowOnPresence { get; init; }
        public bool BlockProvisioned { get; init; }
        public TaskCompletionSource ProvisionedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProvisioned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> HandleDevicePresenceAsync(bool isTargetDevicePresent, CancellationToken cancellationToken = default)
        {
            Calls.Add(isTargetDevicePresent ? "present" : "absent");
            if (ThrowOnPresence) throw new InvalidOperationException("Synthetic presence failure.");
            return Task.FromResult(true);
        }
        public async Task ReportPinProvisionedAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("provisioned");
            if (ThrowOnProvisioned) throw new InvalidOperationException("Synthetic observer failure.");
            if (BlockProvisioned)
            {
                ProvisionedStarted.TrySetResult();
                await ReleaseProvisioned.Task.WaitAsync(cancellationToken);
            }
        }
        public Task ReportPinSetupSucceededAsync(CancellationToken cancellationToken = default) { Calls.Add("succeeded"); return Task.CompletedTask; }
        public Task ReportPinSetupDeletedAsync(CancellationToken cancellationToken = default) { Calls.Add("deleted"); return Task.CompletedTask; }
    }
}
