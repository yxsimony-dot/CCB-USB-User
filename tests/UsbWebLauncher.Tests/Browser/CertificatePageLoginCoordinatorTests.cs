using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Browser;

public sealed class CertificatePageLoginCoordinatorTests
{
    [Fact]
    public async Task RevokedSessionRejectsLateInspectionBeforeCredentialReadOrSubmission()
    {
        using var session = new CancellationTokenSource();
        var entered = Signal();
        var release = new TaskCompletionSource<CertificateLoginPageState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PinStore();
        var page = new Page { Inspect = _ => { entered.TrySetResult(); return release.Task; } };
        await using var coordinator = new CertificatePageLoginCoordinator(store, "configured-target", page,
            sessionToken: session.Token);
        coordinator.BeginAttempt();
        var work = coordinator.ObserveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var revocation = session.CancelAsync();
        release.SetResult(CertificateLoginPageState.Ready);
        await Task.WhenAll(work, revocation);
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        Assert.Equal(1, page.Inspections);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, page.Submissions);
        Assert.DoesNotContain("已填写", coordinator.Message ?? string.Empty);
    }

    [Fact]
    public async Task RevokedSessionDropsDelayedPinEvenBeforeCoordinatorDispose()
    {
        using var session = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var store = new PinStore { BeforeRead = _ => { entered.TrySetResult(); return release.Task; } };
        var page = new Page();
        await using var coordinator = new CertificatePageLoginCoordinator(store, "configured-target", page,
            sessionToken: session.Token);
        coordinator.BeginAttempt();
        var work = coordinator.ObserveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var revocation = session.CancelAsync();
        release.SetResult();
        await Task.WhenAll(work, revocation);
        Assert.Equal(0, page.Submissions);
        Assert.All(store.LastPin!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
        Assert.DoesNotContain("已填写", coordinator.Message ?? string.Empty);
    }

    [Fact]
    public async Task ExplicitAttempt_ReadsConfiguredPinAndSubmitsOnlyOnce_ThenClearsBuffer()
    {
        var store = new PinStore();
        var page = new Page();
        await using var coordinator = Create(store, page);
        await coordinator.ObserveAsync();
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, page.Inspections);

        coordinator.BeginAttempt();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => coordinator.ObserveAsync()));
        await coordinator.ObserveAsync();

        Assert.Equal(1, store.ReadCount);
        Assert.Equal("configured-target", store.LastTarget);
        Assert.Equal(1, page.Submissions);
        Assert.True(page.ReceivedExpectedPin);
        Assert.All(store.LastPin!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
        Assert.Contains("提交", coordinator.Message);
    }

    [Fact]
    public async Task WaitingPage_DoesNotReadPinUntilReady()
    {
        var inspected = Signal();
        var ready = new TaskCompletionSource<CertificateLoginPageState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PinStore();
        var page = new Page { Inspect = token => { inspected.TrySetResult(); return ready.Task.WaitAsync(token); } };
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        var work = coordinator.ObserveAsync();
        await inspected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, store.ReadCount);
        ready.SetResult(CertificateLoginPageState.Ready);
        await work;
        Assert.Equal(1, page.Submissions);
    }

    [Fact]
    public async Task PageNeverReady_TimesOutWithoutSecretReadAndDoesNotRetry()
    {
        var store = new PinStore();
        var page = new Page { Inspect = _ => Task.FromResult(CertificateLoginPageState.WaitingForPage) };
        await using var coordinator = Create(store, page, TimeSpan.FromMilliseconds(25));
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        var count = page.Inspections;
        page.Inspect = _ => Task.FromResult(CertificateLoginPageState.Ready);
        await coordinator.ObserveAsync();
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, page.Submissions);
        Assert.Equal(count, page.Inspections);
        Assert.Contains("超时", coordinator.Message);
    }

    [Fact]
    public async Task UnsupportedPage_DoesNotExposePin()
    {
        var store = new PinStore();
        var page = new Page { Inspect = _ => Task.FromResult(CertificateLoginPageState.Unsupported) };
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, page.Submissions);
        Assert.Contains("手动", coordinator.Message);
    }

    [Fact]
    public async Task MissingPin_DoesNotSubmitOrRetry()
    {
        var store = new PinStore { Missing = true };
        var page = new Page();
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        await coordinator.ObserveAsync();
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(0, page.Submissions);
        Assert.Contains("设置 PIN", coordinator.Message);
    }

    [Theory]
    [InlineData(CertificateLoginResult.Failed)]
    [InlineData(CertificateLoginResult.Unsupported)]
    public async Task NonSuccess_DoesNotRetry(CertificateLoginResult result)
    {
        var store = new PinStore();
        var page = new Page { Submit = _ => Task.FromResult(result) };
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        await coordinator.ObserveAsync();
        Assert.Equal(1, page.Submissions);
        Assert.All(store.LastPin!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
        Assert.DoesNotContain("已填写", coordinator.Message);
    }

    [Fact]
    public async Task AdapterException_DoesNotLeakDetailsOrRetry_AndClearsPin()
    {
        var store = new PinStore();
        var page = new Page { Submit = _ => throw new InvalidOperationException("test-only-pin") };
        await using var coordinator = Create(store, page);
        var messages = new List<string?>();
        coordinator.StateChanged += (_, _) => messages.Add(coordinator.Message);
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        await coordinator.ObserveAsync();
        Assert.Equal(1, page.Submissions);
        Assert.All(messages, message => Assert.DoesNotContain("test-only-pin", message ?? ""));
        Assert.All(store.LastPin!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task CancelDuringPinRead_DropsLateSecretAndSuppressesSubmission()
    {
        var entered = Signal();
        var release = Signal();
        var store = new PinStore { BeforeRead = _ => { entered.TrySetResult(); return release.Task; } };
        var page = new Page();
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        var work = coordinator.ObserveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Cancel();
        release.SetResult();
        await work;
        await coordinator.ObserveAsync();
        Assert.Equal(0, page.Submissions);
        Assert.Null(coordinator.Message);
        Assert.All(store.LastPin!.AsMemory().ToArray(), c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task NewExplicitAttempt_WaitsForOldAdapterAndRejectsItsLateResult()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<CertificateLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PinStore();
        var page = new Page { Submit = _ => { entered.TrySetResult(); return release.Task; } };
        await using var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        var old = coordinator.ObserveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.BeginAttempt();
        var current = coordinator.ObserveAsync();
        Assert.Equal(1, page.Submissions);
        Assert.Null(coordinator.Message);
        page.Submit = _ => Task.FromResult(CertificateLoginResult.Failed);
        release.SetResult(CertificateLoginResult.Submitted);
        await Task.WhenAll(old, current);
        Assert.Equal(2, page.Submissions);
        Assert.DoesNotContain("已填写", coordinator.Message);
    }

    [Fact]
    public async Task Dispose_CancelsWaitingAndRejectsFurtherAttempts()
    {
        var entered = Signal();
        var store = new PinStore();
        var page = new Page { Inspect = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return CertificateLoginPageState.Ready;
        }};
        var coordinator = Create(store, page);
        coordinator.BeginAttempt();
        var work = coordinator.ObserveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        await work;
        coordinator.BeginAttempt();
        await coordinator.ObserveAsync();
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, page.Submissions);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CertificatePageLoginCoordinator Create(PinStore store, Page page,
        TimeSpan? timeout = null) => new(store, "configured-target", page,
            TimeSpan.FromMilliseconds(1), timeout ?? TimeSpan.FromSeconds(2));

    private sealed class PinStore : IClientCertificatePinStore
    {
        public int ReadCount;
        public string? LastTarget;
        public ClientCertificatePin? LastPin;
        public Func<CancellationToken, Task>? BeforeRead;
        public bool Missing;
        public async ValueTask<ClientCertificatePin?> ReadAsync(string targetName, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastTarget = targetName;
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            return Missing ? null : LastPin = ClientCertificatePin.CopyOf("test-only-pin".AsMemory());
        }
        public ValueTask SaveAsync(string targetName, ClientCertificatePin pin, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(string targetName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Page : ICertificateLoginPage
    {
        public int Inspections;
        public int Submissions;
        public bool ReceivedExpectedPin;
        public Func<CancellationToken, Task<CertificateLoginPageState>> Inspect = _ => Task.FromResult(CertificateLoginPageState.Ready);
        public Func<CancellationToken, Task<CertificateLoginResult>> Submit = _ => Task.FromResult(CertificateLoginResult.Submitted);
        public Task<CertificateLoginPageState> InspectAsync(CancellationToken cancellationToken)
        {
            Inspections++;
            return Inspect(cancellationToken);
        }
        public Task<CertificateLoginResult> TryLoginAsync(ClientCertificatePin pin, CancellationToken cancellationToken)
        {
            Submissions++;
            ReceivedExpectedPin = pin.AsMemory().Span.SequenceEqual("test-only-pin".AsSpan());
            return Submit(cancellationToken);
        }
    }
}
