using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PopupScriptRefreshTests
{
    [Fact]
    public async Task ConcurrentEventsDoNotQueueScripts_AndLaterEventsCanRefresh()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var reports = new List<string>();
        using var refresh = new PopupScriptRefresh(() => { calls++; return pending.Task; }, reports.Add);
        var first = refresh.RunAsync();
        await refresh.RunAsync();
        Assert.Equal(1, calls);
        pending.SetResult("\"installed\"");
        await first;
        await refresh.RunAsync();
        Assert.Equal(2, calls);
        Assert.All(reports, r => Assert.Equal("popup-bootstrap-ready", r));
    }

    [Fact]
    public async Task DisposeDropsLateResults_AndDoesNotStartNewScripts()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var reports = 0;
        var refresh = new PopupScriptRefresh(() => { calls++; return pending.Task; }, _ => reports++);
        var work = refresh.RunAsync();
        refresh.Dispose();
        pending.SetResult("\"installed\"");
        await work;
        await refresh.RunAsync();
        Assert.Equal(1, calls);
        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task ExceptionsAreIsolated_AndUnknownOutputIsNeverLogged()
    {
        var calls = 0;
        var reports = new List<string>();
        using var refresh = new PopupScriptRefresh(() => ++calls == 1
            ? Task.FromException<string>(new InvalidOperationException("PRIVATE"))
            : Task.FromResult("PRIVATE-PAYLOAD"), reports.Add);
        await refresh.RunAsync();
        await refresh.RunAsync();
        Assert.Equal(2, calls);
        Assert.Empty(reports);
    }
}
