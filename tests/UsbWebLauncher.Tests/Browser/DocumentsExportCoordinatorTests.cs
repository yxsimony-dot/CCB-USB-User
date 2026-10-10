using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class DocumentsExportCoordinatorTests
{
    private static ExportBatchContext Context => ExportBatchContext.Create(new DateTime(2026, 10, 8), 9, Path.GetTempPath());

    [Fact]
    public async Task ReceiptWaitsForFullStatementCompletionAndUsesSameSnapshot()
    {
        var context = Context;
        var finished = new TaskCompletionSource<DocumentExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var coordinator = new DocumentsExportCoordinator((snapshot, _) => { Assert.Same(context, snapshot); calls.Add("statement"); return finished.Task; },
            (snapshot, _) => { Assert.Same(context, snapshot); calls.Add("receipt"); return Task.FromResult(DocumentExportResult.Completed("receipt.pdf")); });
        var run = coordinator.ExportAsync(context, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { "statement" }, calls);
        finished.SetResult(DocumentExportResult.Completed("statement.pdf"));
        var result = await run;
        Assert.Equal(new[] { "statement", "receipt" }, calls);
        Assert.Equal("statement.pdf", result.Statement.FilePath);
        Assert.Equal("receipt.pdf", result.Receipt!.FilePath);
        Assert.Contains("第一页", result.Message);
    }

    [Theory]
    [InlineData(DocumentExportStatus.Empty)]
    [InlineData(DocumentExportStatus.Failed)]
    [InlineData(DocumentExportStatus.Cancelled)]
    public async Task StatementEmptyFailureOrCancellationNeverStartsReceipt(DocumentExportStatus status)
    {
        var statement = status switch {
            DocumentExportStatus.Empty => DocumentExportResult.Empty(DocumentExportMessages.EmptyStatement),
            DocumentExportStatus.Cancelled => DocumentExportResult.Cancelled(),
            _ => DocumentExportResult.Failed("无法核对流水账户。")
        };
        var coordinator = new DocumentsExportCoordinator((_, _) => Task.FromResult(statement), (_, _) => throw new Exception("must not navigate"));
        var result = await coordinator.ExportAsync(Context, _ => { }, CancellationToken.None);
        Assert.Same(statement, result.Statement); Assert.Null(result.Receipt);
        if (status == DocumentExportStatus.Empty) Assert.Equal("流水页面为空，不需要导出该月份流水。", result.Message);
        if (status == DocumentExportStatus.Failed) Assert.Contains("无法核对流水账户", result.Message);
    }

    [Theory]
    [InlineData(DocumentExportStatus.Empty)]
    [InlineData(DocumentExportStatus.Failed)]
    [InlineData(DocumentExportStatus.Cancelled)]
    public async Task ReceiptNonSuccessKeepsStatementPathAndReportsPartialResult(DocumentExportStatus status)
    {
        var receipt = status switch {
            DocumentExportStatus.Empty => DocumentExportResult.Empty("该月无回单。"),
            DocumentExportStatus.Cancelled => DocumentExportResult.Cancelled(),
            _ => DocumentExportResult.Failed("回单下载中断。")
        };
        var coordinator = new DocumentsExportCoordinator((_, _) => Task.FromResult(DocumentExportResult.Completed("saved-statement.pdf")),
            (_, _) => Task.FromResult(receipt));
        var result = await coordinator.ExportAsync(Context, _ => { }, CancellationToken.None);
        Assert.Equal("saved-statement.pdf", result.Statement.FilePath); Assert.Same(receipt, result.Receipt);
        Assert.Contains("saved-statement.pdf", result.Message); Assert.Contains(receipt.Message, result.Message);
    }

    [Fact]
    public async Task CancellationBetweenStagesPreventsSecondNavigationAndKeepsFirstFile()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new DocumentsExportCoordinator((_, _) => { cts.Cancel(); return Task.FromResult(DocumentExportResult.Completed("saved.pdf")); },
            (_, _) => throw new Exception("must not start receipt"));
        var result = await coordinator.ExportAsync(Context, _ => { }, cts.Token);
        Assert.Equal("saved.pdf", result.Statement.FilePath); Assert.Equal(DocumentExportStatus.Cancelled, result.Receipt!.Status);
    }

    [Fact]
    public async Task UnexpectedReceiptExceptionCannotLosePartialSuccessOrExposeRawContent()
    {
        var coordinator = new DocumentsExportCoordinator((_, _) => Task.FromResult(DocumentExportResult.Completed("saved.pdf")),
            (_, _) => throw new IOException("PRIVATE_BANK_CONTENT"));
        var result = await coordinator.ExportAsync(Context, _ => { }, CancellationToken.None);
        Assert.Equal(DocumentExportStatus.Failed, result.Receipt!.Status);
        Assert.Contains("saved.pdf", result.Message); Assert.DoesNotContain("PRIVATE_BANK_CONTENT", result.Message);
    }

    [Fact]
    public async Task AlreadyCancelledBatchDoesNotTouchEitherPage()
    {
        var coordinator = new DocumentsExportCoordinator((_, _) => throw new Exception("must not navigate"), (_, _) => throw new Exception("must not navigate"));
        var result = await coordinator.ExportAsync(Context, _ => { }, new CancellationToken(true));
        Assert.Equal(DocumentExportStatus.Cancelled, result.Statement.Status); Assert.Null(result.Receipt);
    }

    [Fact]
    public void SnapshotKeepsWholeSelectedMonthAndRejectsFutureMonth()
    {
        var leap = ExportBatchContext.Create(new DateTime(2024, 2, 9), 2, Path.GetTempPath());
        Assert.Equal("20240201", leap.Range.StartDate); Assert.Equal("20240229", leap.Range.EndDate);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), leap.Directory);
        Assert.Throws<InvalidOperationException>(() => ExportBatchContext.Create(new DateTime(2026, 10, 8), 11, Path.GetTempPath()));
    }

    [Fact]
    public void CompletedResultRequiresAFilePath()
    {
        Assert.ThrowsAny<ArgumentException>(() => DocumentExportResult.Completed(" "));
    }

    [Fact]
    public async Task BrokenProgressSubscriberCannotDiscardCompletedFiles()
    {
        var coordinator = new DocumentsExportCoordinator((_, _) => Task.FromResult(DocumentExportResult.Completed("saved.pdf")),
            (_, _) => Task.FromResult(DocumentExportResult.Completed("receipt.pdf")));
        var notifications = 0;
        var result = await coordinator.ExportAsync(Context, _ => { if (++notifications == 2) throw new InvalidOperationException("broken UI"); }, CancellationToken.None);
        Assert.Equal("saved.pdf", result.Statement.FilePath);
        Assert.Equal("receipt.pdf", result.Receipt!.FilePath);
    }

    [Fact]
    public async Task WaitsForActualStatementReceiverDisposalBeforeReceiptStarts()
    {
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptStarted = false;
        var first = new StatementExportCoordinator(new StatementPage(), _ => new CleanupDownload(disposing, release));
        var coordinator = new DocumentsExportCoordinator(async (context, token) =>
            DocumentExportResult.Completed((await first.ExportAsync(context.Range, _ => { }, token))!),
            (_, _) => { receiptStarted = true; return Task.FromResult(DocumentExportResult.Completed("receipt.pdf")); });
        var running = coordinator.ExportAsync(Context, _ => { }, CancellationToken.None);
        await disposing.Task;
        Assert.False(receiptStarted); Assert.False(running.IsCompleted);
        release.SetResult();
        var result = await running;
        Assert.True(receiptStarted); Assert.Equal("statement.pdf", result.Statement.FilePath);
    }

    private sealed class StatementPage : IStatementExportPage
    {
        private readonly Queue<string> _stages = new(["accounts", "query", "results"]);
        public Task<StatementPageReply> ExecuteAsync(string operation, StatementExportMonth range, string runId, CancellationToken token) =>
            Task.FromResult(operation switch {
                "ensure-home" => new StatementPageReply("home-ready"), "open-accounts" => new("opened"),
                "select-first" => new("selected"), "query" => new("queried"), "inspect" => new("ready", _stages.Dequeue()),
                "identify" => new("identified", Identity: StatementExportIdentity.Create("测试公司", "0012", range.Month)),
                "download" => new("downloading"), _ => new("disposed")
            });
    }
    private sealed class CleanupDownload(TaskCompletionSource entered, TaskCompletionSource release) : IStatementExportDownload
    {
        public Task<string> WaitAsync(CancellationToken token) => Task.FromResult("statement.pdf");
        public void Dispose() => throw new Exception("must await cleanup");
        public async ValueTask DisposeAsync() { entered.SetResult(); await release.Task; }
    }
}
