using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PopupDiagnosticSamplerTests
{
    [Fact]
    public async Task BusySampleDoesNotQueueMoreWork_AndUnchangedStructureIsWrittenOnce()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var writes = new List<string>();
        using var sampler = new PopupDiagnosticSampler(() => { reads++; return pending.Task; },
            s => { writes.Add(s); return Task.CompletedTask; });
        var first = sampler.CaptureAsync();
        await sampler.CaptureAsync();
        Assert.Equal(1, reads);
        pending.SetResult("{\"status\":\"diagnostic\"}");
        await first;
        await sampler.CaptureAsync();
        Assert.Single(writes);
    }

    [Fact]
    public async Task ReadAndWriteErrorsDoNotEscapeOrDisableFutureSamples()
    {
        var reads = 0;
        var writes = 0;
        using var sampler = new PopupDiagnosticSampler(() => ++reads == 1
            ? Task.FromException<string>(new InvalidOperationException("synthetic"))
            : Task.FromResult("{\"status\":\"diagnostic\"}"),
            _ => ++writes == 1 ? Task.FromException(new IOException("synthetic")) : Task.CompletedTask);
        await sampler.CaptureAsync();
        await sampler.CaptureAsync();
        await sampler.CaptureAsync();
        Assert.Equal(3, reads);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task DisposalDiscardsPendingSample_AndDoesNotReadAgain()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var writes = 0;
        var sampler = new PopupDiagnosticSampler(() => { reads++; return pending.Task; },
            _ => { writes++; return Task.CompletedTask; });
        var first = sampler.CaptureAsync();
        sampler.Dispose();
        pending.SetResult("{\"status\":\"diagnostic\"}");
        await first;
        await sampler.CaptureAsync();
        Assert.Equal(1, reads);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"status\":\"unsupported\"}")]
    [InlineData("not-json")]
    public async Task NonDiagnosticOutputIsDiscarded(string output)
    {
        var writes = 0;
        using var sampler = new PopupDiagnosticSampler(() => Task.FromResult(output),
            _ => { writes++; return Task.CompletedTask; });
        await sampler.CaptureAsync();
        Assert.Equal(0, writes);
    }
}
