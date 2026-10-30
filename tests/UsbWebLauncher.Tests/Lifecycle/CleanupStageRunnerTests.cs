using UsbWebLauncher.Core.Lifecycle;

namespace UsbWebLauncher.Tests.Lifecycle;

public sealed class CleanupStageRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenStagesFail_AttemptsEveryStageAndReportsTheirIdentity()
    {
        var calls = new List<string>();
        var stages = new[]
        {
            new CleanupStage("first", () =>
            {
                calls.Add("first");
                throw new InvalidOperationException("sync failure");
            }),
            new CleanupStage("second", () =>
            {
                calls.Add("second");
                return ValueTask.CompletedTask;
            }),
            new CleanupStage("third", async () =>
            {
                calls.Add("third");
                await Task.Yield();
                throw new IOException("async failure");
            }),
            new CleanupStage("fourth", () =>
            {
                calls.Add("fourth");
                return ValueTask.CompletedTask;
            })
        };

        var exception = await Assert.ThrowsAsync<CleanupStageAggregateException>(
            () => CleanupStageRunner.RunAsync(stages));

        Assert.Equal(["first", "second", "third", "fourth"], calls);
        Assert.Equal(
            [
                new CleanupStageFailure("first", nameof(InvalidOperationException)),
                new CleanupStageFailure("third", nameof(IOException))
            ],
            exception.Failures);
    }

    [Fact]
    public async Task RunAsync_DiagnosticsAndAggregateExcludeOriginalExceptionData()
    {
        const string pin = "729104";
        const string pageBody = "<html>credential form body</html>";
        const string credentialUrl = "https://user:secret@example.invalid/login?pin=729104";
        var observations = new List<CleanupStageObservation>();

        var exception = await Assert.ThrowsAsync<CleanupStageAggregateException>(
            () => CleanupStageRunner.RunAsync(
                [
                    new CleanupStage("credential-store", () =>
                        throw new InvalidOperationException(
                            $"{pin}|{pageBody}|{credentialUrl}"))
                ],
                observations.Add));

        var report = string.Join(
            Environment.NewLine,
            observations.Select(observation => observation.ToString()).Append(exception.ToString()));
        Assert.DoesNotContain(pin, report, StringComparison.Ordinal);
        Assert.DoesNotContain(pageBody, report, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialUrl, report, StringComparison.Ordinal);
        Assert.Contains("credential-store", report, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenObserverThrows_StillRunsCleanup()
    {
        var cleaned = false;

        await CleanupStageRunner.RunAsync(
            [
                new CleanupStage("resource", () =>
                {
                    cleaned = true;
                    return ValueTask.CompletedTask;
                })
            ],
            _ => throw new InvalidOperationException("diagnostic failure"));

        Assert.True(cleaned);
    }
}
