using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class WpfReceiptDownloadTests : IDisposable
{
    private const string Origin = "https://b2b3.ccb.com";
    private const string Endpoint = Origin + "/NCCB/NECV6FileDownload_09_LH";
    private const string Pdf = "%PDF-1.7\noriginal bank receipt\n%%EOF";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "receipt-download-tests", Guid.NewGuid().ToString("N"));

    public WpfReceiptDownloadTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task DefersDownloadUntilSourceConfirmsRunThenPublishesOnlyAfterCompletion()
    {
        var source = new Source();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? verifiedRun = null;
        await using var receiver = Create(source, (run, _) => { verifiedRun = run; return confirmation.Task; });
        var args = source.Start();
        Assert.Equal("run-one", verifiedRun);
        Assert.False(args.Handled);
        Assert.False(args.Deferral.Task.IsCompleted);
        confirmation.SetResult(true);
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(args.Handled);
        Assert.False(args.Cancel);
        Assert.NotEqual("bank-receipt.pdf", args.ResultFilePath);
        await File.WriteAllTextAsync(args.ResultFilePath, Pdf);
        var waiting = receiver.WaitAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        args.Operation.SetState(ReceiptDownloadState.Completed);
        var final = await waiting;
        Assert.Equal("09-测试公司-建行0001-回单.pdf", Path.GetFileName(final));
        Assert.Equal(Pdf, await File.ReadAllTextAsync(final));
        Assert.False(File.Exists(args.ResultFilePath));
    }

    [Fact]
    public async Task CompletedBeforeEventIsObservedStillValidatesAndPublishesPdf()
    {
        var source = new Source();
        await using var receiver = Create(source);
        var args = source.Start(initial: ReceiptDownloadState.Completed);
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args.ResultFilePath, Pdf);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(await receiver.WaitAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task UnrelatedDownloadsAreNotModifiedOrEvenDeferred()
    {
        var source = new Source();
        var verificationCount = 0;
        await using var receiver = Create(source, (_, _) => { verificationCount++; return Task.FromResult(true); });
        var args = source.Start(uri: Origin + "/other.pdf");
        Assert.False(args.Handled);
        Assert.False(args.Cancel);
        Assert.Equal("bank-receipt.pdf", args.ResultFilePath);
        Assert.Equal(0, args.DeferralsTaken);
        Assert.Equal(0, verificationCount);
    }

    [Fact]
    public async Task DownloadAlreadyClaimedByAnotherHandlerIsNotChanged()
    {
        var source = new Source();
        source.DownloadStarting += (_, args) => args.Handled = true;
        await using var receiver = Create(source);
        var args = source.Start();
        Assert.True(args.Handled);
        Assert.False(args.Cancel);
        Assert.Equal("bank-receipt.pdf", args.ResultFilePath);
        Assert.Equal(0, args.DeferralsTaken);
    }

    [Fact]
    public async Task DownloadTimeoutReleasesListenerAndTemporaryFile()
    {
        var source = new Source();
        await using var receiver = new WpfReceiptDownload(source, Origin, "run-one",
            StatementExportIdentity.CreateReceipt("测试公司", "0001", 9), directory, (_, _) => Task.FromResult(true), TimeSpan.FromMilliseconds(30));
        var error = await Assert.ThrowsAsync<StatementExportException>(() => receiver.WaitAsync(CancellationToken.None));
        Assert.Contains("超时", error.Message);
        Assert.Equal(0, source.SubscriberCount);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task MatchingEndpointFromUnverifiedSourceIsNotClaimedAndDoesNotBlockLaterOwnDownload()
    {
        var source = new Source();
        var verify = false;
        await using var receiver = Create(source, (_, _) => Task.FromResult(verify));
        var unrelated = source.Start();
        await unrelated.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(unrelated.Handled);
        Assert.False(unrelated.Cancel);
        Assert.Equal("bank-receipt.pdf", unrelated.ResultFilePath);
        verify = true;
        var owned = source.Start();
        await owned.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(owned.Handled);
    }

    [Fact]
    public async Task OneReceiverClaimsOnlyOneDownload()
    {
        var source = new Source();
        await using var receiver = Create(source);
        var owned = source.Start();
        await owned.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = source.Start();
        Assert.True(owned.Handled);
        Assert.False(second.Handled);
        Assert.False(second.Cancel);
        Assert.Equal(0, second.DeferralsTaken);
    }

    [Fact]
    public async Task VerifiedNonPdfFailsWithoutPublishing()
    {
        var source = new Source();
        await using var receiver = Create(source);
        var args = source.Start(mime: "text/html");
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(args.Cancel);
        var later = source.Start();
        Assert.Equal(0, later.DeferralsTaken);
        await Assert.ThrowsAsync<StatementExportException>(() => receiver.WaitAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf"));
    }

    [Fact]
    public async Task CompletedFileWithWrongSignatureIsDeleted()
    {
        var source = new Source();
        await using var receiver = Create(source);
        var args = source.Start();
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args.ResultFilePath, "not a PDF");
        args.Operation.SetState(ReceiptDownloadState.Completed);
        await Assert.ThrowsAsync<InvalidDataException>(() => receiver.WaitAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task InterruptedDownloadFailsAndCleansTemporaryFile()
    {
        var source = new Source();
        var receiver = Create(source);
        var args = source.Start();
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args.ResultFilePath, Pdf);
        args.Operation.SetState(ReceiptDownloadState.Interrupted);
        await Assert.ThrowsAsync<StatementExportException>(() => receiver.WaitAsync(CancellationToken.None));
        await receiver.DisposeAsync();
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Equal(0, args.Operation.SubscriberCount);
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task CancellationStopsOwnedOperationAndRemovesPartialFile()
    {
        var source = new Source();
        var receiver = Create(source);
        var args = source.Start();
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args.ResultFilePath, "%PDF-partial");
        using var cancel = new CancellationTokenSource();
        var waiting = receiver.WaitAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await receiver.DisposeAsync();
        Assert.Equal(1, args.Operation.CancelCount);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopRacingCompletedDownloadPublishesOnlyIfCompletionWasAlreadyConsumed(bool stopBeforePublication)
    {
        var source = new Source();
        var receiver = Create(source);
        var args = source.Start();
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.WriteAllTextAsync(args.ResultFilePath, Pdf);
        var savedStatement = Path.Combine(directory, "already-saved-statement.pdf");
        await File.WriteAllTextAsync(savedStatement, Pdf + "statement");
        using var cancellation = new CancellationTokenSource();
        using var ui = new DeferredContinuationContext();
        var previous = SynchronizationContext.Current;
        Task<string> waiting;
        try
        {
            // Freeze the real receiver's continuation at exactly the boundary
            // between WebView reporting completion and the atomic PDF rename.
            SynchronizationContext.SetSynchronizationContext(ui);
            waiting = receiver.WaitAsync(cancellation.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        args.Operation.SetState(ReceiptDownloadState.Completed);
        await ui.Posted.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopBeforePublication) cancellation.Cancel();
        ui.RunPending();

        string? receiptPath = null;
        if (stopBeforePublication)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        else
        {
            receiptPath = await waiting;
            cancellation.Cancel();
        }
        await receiver.DisposeAsync();

        Assert.Equal(Pdf + "statement", await File.ReadAllTextAsync(savedStatement));
        Assert.False(File.Exists(args.ResultFilePath));
        Assert.Equal(stopBeforePublication ? 1 : 2, Directory.GetFiles(directory, "*.pdf").Length);
        if (receiptPath is not null) Assert.Equal(Pdf, await File.ReadAllTextAsync(receiptPath));
        Assert.Equal(0, source.SubscriberCount);
        Assert.Equal(0, args.Operation.SubscriberCount);
    }

    private sealed class DeferredContinuationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
        private readonly TaskCompletionSource posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Posted => posted.Task;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            pending.Enqueue((callback, state));
            posted.TrySetResult();
        }
        public void RunPending()
        {
            while (pending.TryDequeue(out var work)) work.Callback(work.State);
        }
        public void Dispose() => RunPending();
    }

    [Fact]
    public async Task DisposeReleasesPendingDeferralAndPreventsLateOwnership()
    {
        var source = new Source();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Create(source, (_, _) => confirmation.Task);
        var args = source.Start();
        await receiver.DisposeAsync();
        confirmation.TrySetResult(true);
        await args.Deferral.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, args.DeferralCompletions);
        Assert.False(args.Handled);
        Assert.False(args.Cancel);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void RejectsStatementIdentityBeforeSubscribingOrCreatingFile()
    {
        var source = new Source();
        Assert.Throws<ArgumentException>(() => new WpfReceiptDownload(source, Origin, "run-one",
            StatementExportIdentity.Create("测试公司", "0001", 9), directory, (_, _) => Task.FromResult(true)));
        Assert.Equal(0, source.SubscriberCount);
        Assert.Empty(Directory.GetFiles(directory));
    }

    private WpfReceiptDownload Create(Source source, Func<string, CancellationToken, Task<bool>>? verify = null) =>
        new(source, Origin, "run-one", StatementExportIdentity.CreateReceipt("测试公司", "0001", 9), directory,
            verify ?? ((_, _) => Task.FromResult(true)));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class Source : IReceiptDownloadSource
    {
        public event EventHandler<IReceiptDownloadStarting>? DownloadStarting;
        public int SubscriberCount => DownloadStarting?.GetInvocationList().Length ?? 0;
        public bool Disposed { get; private set; }
        public Starting Start(string uri = Endpoint, string mime = "application/pdf", ReceiptDownloadState initial = ReceiptDownloadState.InProgress)
        {
            var args = new Starting(new Operation(uri, mime, initial));
            DownloadStarting?.Invoke(this, args);
            return args;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Starting(Operation operation) : IReceiptDownloadStarting
    {
        public Operation Operation => operation;
        IReceiptDownloadOperation IReceiptDownloadStarting.Operation => Operation;
        public string ResultFilePath { get; set; } = "bank-receipt.pdf";
        public bool Handled { get; set; }
        public bool Cancel { get; set; }
        public int DeferralsTaken { get; private set; }
        public int DeferralCompletions { get; private set; }
        public TaskCompletionSource<bool> Deferral { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action GetCompleteDeferral()
        {
            DeferralsTaken++;
            return () => { DeferralCompletions++; Deferral.TrySetResult(true); };
        }
    }

    private sealed class Operation(string uri, string mime, ReceiptDownloadState initial) : IReceiptDownloadOperation
    {
        public string Uri => uri;
        public string MimeType => mime;
        public ReceiptDownloadState State { get; private set; } = initial;
        public event EventHandler? StateChanged;
        public int SubscriberCount => StateChanged?.GetInvocationList().Length ?? 0;
        public int CancelCount { get; private set; }
        public void SetState(ReceiptDownloadState state) { State = state; StateChanged?.Invoke(this, EventArgs.Empty); }
        public void Cancel() { CancelCount++; SetState(ReceiptDownloadState.Interrupted); }
    }
}
