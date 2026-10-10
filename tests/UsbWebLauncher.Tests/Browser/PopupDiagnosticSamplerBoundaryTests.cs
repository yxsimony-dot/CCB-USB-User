using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PopupDiagnosticSamplerBoundaryTests
{
    [Fact]
    public async Task OversizedDiagnosticIsDiscarded_AndLaterSmallSampleIsStillWritten()
    {
        var reads = 0;
        var writes = new List<string>();
        const string small = "{\"status\":\"diagnostic\"}";
        var oversized = "{\"status\":\"diagnostic\",\"padding\":\"" + new string('x', 128 * 1024) + "\"}";
        using var sampler = new PopupDiagnosticSampler(
            () => Task.FromResult(++reads == 1 ? oversized : small),
            json => { writes.Add(json); return Task.CompletedTask; });

        await sampler.CaptureAsync();
        Assert.Empty(writes);
        await sampler.CaptureAsync();
        Assert.Equal(small, Assert.Single(writes));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"status\":null}")]
    [InlineData("{\"status\":1}")]
    [InlineData("{\"status\":true}")]
    public async Task UnexpectedOutputShapeIsNotWritten(string json)
    {
        var writes = 0;
        using var sampler = new PopupDiagnosticSampler(() => Task.FromResult(json),
            _ => { writes++; return Task.CompletedTask; });

        await sampler.CaptureAsync();

        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task SynchronousReadFailureDoesNotLeaveSamplerBusy()
    {
        var reads = 0;
        var writes = 0;
        using var sampler = new PopupDiagnosticSampler(
            () => ++reads == 1 ? throw new InvalidOperationException("synthetic")
                : Task.FromResult("{\"status\":\"diagnostic\"}"),
            _ => { writes++; return Task.CompletedTask; });

        await sampler.CaptureAsync();
        await sampler.CaptureAsync();

        Assert.Equal(2, reads);
        Assert.Equal(1, writes);
    }
}
