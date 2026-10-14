using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class ReceiptExportCoordinatorTests
{
    private static readonly StatementExportMonth Range = StatementExportMonth.Create(new DateTime(2026, 10, 8), 2);
    private static readonly StatementExportIdentity Identity = StatementExportIdentity.CreateReceipt("测试有限公司", "0012", 9);

    [Fact]
    public async Task ArmsBeforePrintAndAwaitsCleanupBeforePageRelease()
    {
        var calls = new List<string>();
        var page = new Page(calls);
        var download = new Download(calls);
        var coordinator = new ReceiptExportCoordinator(page, (identity, run) => {
            Assert.Same(Identity, identity); Assert.Equal(page.RunIds[0], run); calls.Add("arm"); return download;
        });
        Assert.Equal("receipt.pdf", await coordinator.ExportAsync(Range, _ => { }, CancellationToken.None));
        Assert.Equal(new[] { "ensure-home", "open", "query", "identify", "arm", "download", "wait", "cleanup", "dispose" }, calls);
        Assert.Single(page.RunIds.Distinct());
    }

    [Fact]
    public async Task EmptyResultsDoNotArmOrPrint()
    {
        var calls = new List<string>(); var messages = new List<string>();
        var page = new Page(calls) { Override = op => op == "identify" ? new("empty", "results") : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => throw new Exception("must not arm"));
        Assert.Null(await coordinator.ExportAsync(Range, messages.Add, CancellationToken.None));
        Assert.Contains("该月无回单。", messages); Assert.DoesNotContain("download", calls); Assert.Equal("dispose", calls.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongDocumentIdentityNeverPrints(bool wrongKind)
    {
        var calls = new List<string>();
        var page = new Page(calls) { Override = op => op == "identify" ? new("identified", Identity: wrongKind ? StatementExportIdentity.Create("测试", "0012", 9) : null) : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => throw new Exception("must not arm"));
        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(Range, _ => { }, CancellationToken.None));
        Assert.DoesNotContain("download", calls); Assert.Equal("dispose", calls.Last());
    }

    [Fact]
    public async Task CancellationWhileWaitingStopsLaterActionsAndCleansPage()
    {
        using var cts = new CancellationTokenSource(); var calls = new List<string>();
        var page = new Page(calls) { Override = op => op == "open" ? new("waiting") : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => throw new Exception("must not arm"),
            (_, _) => { cts.Cancel(); return Task.FromCanceled(cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(Range, _ => { }, cts.Token));
        Assert.Equal(new[] { "ensure-home", "open", "dispose" }, calls);
    }

    [Fact]
    public async Task FailureAfterArmingDisposesReceiverWithoutWaiting()
    {
        var calls = new List<string>();
        var page = new Page(calls) { Override = op => op == "download" ? new("failed", Code: "private text") : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => new Download(calls));
        var error = await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(Range, _ => { }, CancellationToken.None));
        Assert.DoesNotContain("private text", error.Message); Assert.DoesNotContain("wait", calls);
        Assert.Equal(new[] { "cleanup", "dispose" }, calls.TakeLast(2));
    }

    [Fact]
    public async Task RepeatedRunsUseNewRunIdsAndWaitForResultsWithoutResubmittingQuery()
    {
        var calls = new List<string>(); var polls = 0;
        var page = new Page(calls) { Override = op => op == "identify" && ++polls % 2 == 1 ? new("waiting", "results") : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => new Download(calls), (_, _) => Task.CompletedTask);
        for (var i = 0; i < 2; i++) await coordinator.ExportAsync(Range, _ => { }, CancellationToken.None);
        Assert.Equal(2, page.RunIds.Distinct().Count()); Assert.Equal(2, calls.Count(c => c == "query"));
        Assert.Equal(4, calls.Count(c => c == "identify")); Assert.Equal(2, calls.Count(c => c == "download"));
    }

    [Fact]
    public async Task ContinuesPollingWhenHomeIdentityIsStillBeingInserted()
    {
        var calls = new List<string>();
        var replies = new Queue<StatementPageReply>([
            new("waiting", "home", "account-identity"),
            new("home-ready", "home")
        ]);
        var page = new Page(calls)
        {
            Override = op => op == "ensure-home" && replies.Count != 0 ? replies.Dequeue() : null
        };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => new Download(calls),
            (_, _) => Task.CompletedTask);

        Assert.Equal("receipt.pdf", await coordinator.ExportAsync(Range, _ => { }, CancellationToken.None));
        Assert.Equal(2, calls.Count(operation => operation == "ensure-home"));
    }

    [Fact]
    public async Task WaitingTimeoutUsesFriendlyReceiptMessage()
    {
        var calls = new List<string>();
        var page = new Page(calls) { Override = op => op == "ensure-home" ? new("waiting", "home", "account-identity") : null };
        var coordinator = new ReceiptExportCoordinator(page, (_, _) => throw new Exception("must not arm"),
            (_, _) => Task.FromCanceled(new CancellationToken(true)));

        var error = await Assert.ThrowsAsync<StatementExportException>(() =>
            coordinator.ExportAsync(Range, _ => { }, CancellationToken.None));

        Assert.Equal("等待电子回单页面超时，请查看银行页面后重试。", error.Message);
        Assert.Equal("dispose", calls.Last());
    }

    private sealed class Page(List<string> calls) : IStatementExportPage
    {
        public List<string> RunIds { get; } = [];
        public Func<string, StatementPageReply?>? Override { get; init; }
        public Task<StatementPageReply> ExecuteAsync(string operation, StatementExportMonth range, string runId, CancellationToken token)
        {
            calls.Add(operation); RunIds.Add(runId); Assert.Same(Range, range);
            return Task.FromResult(Override?.Invoke(operation) ?? new StatementPageReply(operation switch {
                "ensure-home" => "home-ready", "open" => "opened", "query" => "queried", "identify" => "identified",
                "download" => "downloading", _ => "disposed"
            }, Identity: operation == "identify" ? Identity : null));
        }
    }
    private sealed class Download(List<string> calls) : IStatementExportDownload
    {
        public Task<string> WaitAsync(CancellationToken token) { calls.Add("wait"); return Task.FromResult("receipt.pdf"); }
        public void Dispose() => throw new Exception("must await async cleanup");
        public async ValueTask DisposeAsync() { await Task.Yield(); calls.Add("cleanup"); }
    }
}
