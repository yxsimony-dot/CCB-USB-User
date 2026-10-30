using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class WpfWebView2ControlDisposalTests
{
    [Fact]
    public void DirectoryOwnershipTracksPendingEnvironmentAndRetiresFailedCreationOnce()
    {
        using var fixture = new SessionDirectoryFixture();
        var retireCount = 0;
        var ownership = new BrowserSessionDirectoryOwnership(
            fixture.Directory,
            () =>
            {
                Interlocked.Increment(ref retireCount);
                fixture.Directory.RetireAndTryCleanup();
            });

        Assert.Equal(BrowserSessionDirectoryOwnershipState.NotCreated, ownership.State);
        Assert.Equal(fixture.Directory.Path, ownership.EnsureCreated());
        Assert.Equal(BrowserSessionDirectoryOwnershipState.PendingEnvironment, ownership.State);

        ownership.DisposeRequested();
        Assert.Equal(BrowserSessionDirectoryOwnershipState.PendingEnvironment, ownership.State);
        Assert.True(System.IO.Directory.Exists(fixture.Directory.Path));

        ownership.EnvironmentCreationFailed();
        ownership.EnvironmentCreationFailed();
        ownership.DisposeRequested();

        Assert.Equal(BrowserSessionDirectoryOwnershipState.Retired, ownership.State);
        Assert.Equal(1, retireCount);
        Assert.False(System.IO.Directory.Exists(fixture.Directory.Path));
    }

    [Fact]
    public void DirectoryOwnershipWaitsForBrowserExitBeforeRetiringLiveProfile()
    {
        using var fixture = new SessionDirectoryFixture();
        var retireCount = 0;
        var ownership = new BrowserSessionDirectoryOwnership(
            fixture.Directory,
            () =>
            {
                Interlocked.Increment(ref retireCount);
                fixture.Directory.RetireAndTryCleanup();
            });

        ownership.EnsureCreated();
        ownership.EnvironmentCreated();
        Assert.Equal(BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit, ownership.State);

        ownership.DisposeRequested();
        Assert.Equal(BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit, ownership.State);
        Assert.Equal(0, retireCount);
        Assert.True(System.IO.Directory.Exists(fixture.Directory.Path));
        Assert.False(File.Exists(System.IO.Path.Combine(
            fixture.Directory.Path,
            BrowserSessionDirectory.RetiredFileName)));

        ownership.BrowserProcessExited();
        ownership.BrowserProcessExited();
        ownership.DisposeRequested();

        Assert.Equal(BrowserSessionDirectoryOwnershipState.Retired, ownership.State);
        Assert.Equal(1, retireCount);
        Assert.False(System.IO.Directory.Exists(fixture.Directory.Path));
    }

    [Fact]
    public void BrowserExitBeforeDisposeIsRememberedAndRetiredByDispose()
    {
        using var fixture = new SessionDirectoryFixture();
        var retireCount = 0;
        var ownership = new BrowserSessionDirectoryOwnership(
            fixture.Directory,
            () =>
            {
                Interlocked.Increment(ref retireCount);
                fixture.Directory.RetireAndTryCleanup();
            });

        ownership.EnsureCreated();
        ownership.EnvironmentCreated();
        ownership.BrowserProcessExited();

        Assert.Equal(BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit, ownership.State);
        Assert.True(System.IO.Directory.Exists(fixture.Directory.Path));

        ownership.DisposeRequested();

        Assert.Equal(BrowserSessionDirectoryOwnershipState.Retired, ownership.State);
        Assert.Equal(1, retireCount);
        Assert.False(System.IO.Directory.Exists(fixture.Directory.Path));
    }

    [Fact]
    public void DisposeDuringEnvironmentCreationWaitsForCreatedEnvironmentToExit()
    {
        using var fixture = new SessionDirectoryFixture();
        var retireCount = 0;
        var ownership = new BrowserSessionDirectoryOwnership(
            fixture.Directory,
            () =>
            {
                Interlocked.Increment(ref retireCount);
                fixture.Directory.RetireAndTryCleanup();
            });

        ownership.EnsureCreated();
        ownership.DisposeRequested();
        Assert.Equal(BrowserSessionDirectoryOwnershipState.PendingEnvironment, ownership.State);

        ownership.EnvironmentCreated();
        Assert.Equal(BrowserSessionDirectoryOwnershipState.WaitingForBrowserExit, ownership.State);
        Assert.Equal(0, retireCount);
        Assert.True(System.IO.Directory.Exists(fixture.Directory.Path));

        ownership.BrowserProcessExited();

        Assert.Equal(BrowserSessionDirectoryOwnershipState.Retired, ownership.State);
        Assert.Equal(1, retireCount);
        Assert.False(System.IO.Directory.Exists(fixture.Directory.Path));
    }

    [Fact]
    public async Task InjectedEnvironmentCreationFailureRetiresCreatedDirectory()
    {
        using var fixture = new SessionDirectoryFixture();
        var phases = new List<string>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new WebView2();
                var wrapper = new WpfWebView2Control(
                    control,
                    downloadSettings: null,
                    downloadError: null,
                    fixture.Directory,
                    CancellationToken.None,
                    _ => Task.FromException<Microsoft.Web.WebView2.Core.CoreWebView2Environment>(
                        new InvalidOperationException("Injected environment failure.")),
                    phases.Add);

                var initialization = wrapper.EnsureInitializedAsync(CancellationToken.None);
                Assert.True(initialization.IsFaulted);
                Assert.IsType<InvalidOperationException>(
                    initialization.Exception?.GetBaseException());
                Assert.Equal(BrowserSessionDirectoryOwnershipState.Retired, wrapper.SessionDirectoryState);
                Assert.Equal(
                    [
                        "session-directory-created",
                        "environment-creation-started",
                        "environment-creation-failed"
                    ],
                    phases);
                WaitUntil(() =>
                    !System.IO.Directory.Exists(fixture.Directory.Path) ||
                    File.Exists(System.IO.Path.Combine(
                        fixture.Directory.Path,
                        BrowserSessionDirectory.RetiredFileName)));
                Assert.True(wrapper.DisposeAsync().IsCompletedSuccessfully);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposingWrapperActuallyDisposesOwnedWebViewAndAllowsRepeatedDispose()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try
            {
                using var control = new WebView2();
                var wrapper = new WpfWebView2Control(control);
                control.GoBack(); // Uninitialized but live controls accept this without navigation.
                await wrapper.DisposeAsync();
                await wrapper.DisposeAsync();
                Assert.Throws<ObjectDisposedException>(() => control.GoBack());
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("Condition was not reached.");
            Thread.Sleep(20);
        }
    }

    private sealed class SessionDirectoryFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "UsbWebLauncherOwnershipTests",
            Guid.NewGuid().ToString("N"));

        public BrowserSessionDirectory Directory { get; }

        public SessionDirectoryFixture()
        {
            Directory = new BrowserSessionDirectory(_root);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_root))
                System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}
