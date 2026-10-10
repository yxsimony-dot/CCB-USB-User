using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementExportCoordinatorTests
{
    private static readonly StatementExportIdentity Identity = StatementExportIdentity.Create("测试服务有限公司工会", "0507", 9);

    [Fact]
    public async Task VerifiedEmptyStopsBeforeArmingDownloadAndDisposesStateWithoutNavigating()
    {
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(operation switch
            {
                "open-accounts" => new StatementPageReply("opened"),
                "select-first" => new StatementPageReply("selected"),
                "query" => new StatementPageReply("queried"),
                "identify" => new StatementPageReply("empty", "results"),
                _ => new StatementPageReply("disposed")
            });
        });
        var stages = new Queue<string>(["accounts", "query", "results"]);
        page.InspectStage = () => stages.Dequeue();
        var messages = new List<string>();
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not create an empty PDF"));
        var result = await coordinator.ExportAsync(StatementExportMonth.Create(new DateTime(2026, 10, 8), 9), messages.Add, CancellationToken.None);
        Assert.Null(result);
        Assert.Equal("流水页面为空，不需要导出该月份流水。", messages.Last());
        Assert.Equal(new[] { "open-accounts", "select-first", "query", "identify", "dispose" }, calls);
    }

    [Fact]
    public async Task EmptyOnUnverifiedInspectionIsNotAnEmptyStatementOutcome()
    {
        var page = new Page((operation, _, _) => Task.FromResult(operation == "open-accounts"
            ? new StatementPageReply("opened") : new StatementPageReply("empty", "results")));
        using var cancellation = new CancellationTokenSource();
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"),
            (_, _) => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 10, 8), 9), _ => { }, cancellation.Token));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("identified")]
    public async Task MissingOrRejectedIdentityNeverArmsOrStartsDownload(string status)
    {
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(operation == "identify" ? new StatementPageReply(status, "results", "naming-identity") :
                new StatementPageReply(operation switch { "open-accounts" => "opened", "select-first" => "selected", "query" => "queried", _ => "disposed" }));
        });
        var stages = new Queue<string>(["accounts", "query", "results"]);
        page.InspectStage = () => stages.Dequeue();
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"));
        var error = await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, CancellationToken.None));
        Assert.Contains("公司全名", error.Message);
        Assert.DoesNotContain("download", calls);
        Assert.Contains("dispose", calls);
    }
    [Fact]
    public async Task MissingAccountEntryIsNotReportedAsMissingLogin()
    {
        var page = new Page((_, _, _) => Task.FromResult(
            new StatementPageReply("unsupported", "unsupported", "entry-not-found")));
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"));
        var error = await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, CancellationToken.None));
        Assert.Contains("活期账户入口", error.Message);
        Assert.DoesNotContain("请先启动业务并登录", error.Message);
    }

    [Fact]
    public async Task ExecutesEachMutationOnceAndArmsDownloadBeforePdfAction()
    {
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(operation switch
            {
                "open-accounts" => new StatementPageReply("opened"),
                "select-first" => new StatementPageReply("selected"),
                "query" => new StatementPageReply("queried"),
                "identify" => new StatementPageReply("identified", Identity: Identity),
                "download" => new StatementPageReply("downloading"),
                "inspect" => new StatementPageReply("ready", calls.Contains("query") ? "results" : calls.Contains("select-first") ? "query" : "accounts"),
                _ => new StatementPageReply("disposed")
            });
        });
        var download = new Download(() => { calls.Add("download-complete"); return Task.FromResult("desktop.pdf"); });
        var coordinator = new StatementExportCoordinator(page, identity => { Assert.Same(Identity, identity); calls.Add("armed"); return download; });

        var result = await coordinator.ExportAsync(StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, CancellationToken.None);

        Assert.Equal("desktop.pdf", result);
        Assert.Equal(new[] { "open-accounts", "inspect", "select-first", "inspect", "query", "inspect", "identify", "armed", "download", "download-complete", "dispose" }, calls);
        Assert.True(download.Disposed);
        Assert.Single(page.RunIds.Distinct());
    }

    [Fact]
    public async Task AmbiguousPdfDoesNotWaitForOrReportDownloadAndCleansUp()
    {
        var page = new Page((operation, _, _) => Task.FromResult(operation switch
        {
            "inspect" => new StatementPageReply("ready", "results"),
            "download" => new StatementPageReply("ambiguous"),
            "identify" => new StatementPageReply("identified", Identity: Identity),
            _ => new StatementPageReply(operation switch { "open-accounts" => "opened", "select-first" => "selected", "query" => "queried", _ => "disposed" })
        }));
        // Use stages in expected order without browser timing dependencies.
        var inspections = new Queue<string>(["accounts", "query", "results"]);
        page.InspectStage = () => inspections.Dequeue();
        var waited = false;
        var download = new Download(() => { waited = true; return Task.FromResult("wrong.pdf"); });
        var coordinator = new StatementExportCoordinator(page, _ => download);

        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, CancellationToken.None));
        Assert.False(waited);
        Assert.True(download.Disposed);
    }

    [Fact]
    public async Task CancellationDuringPageWaitDoesNotSubmitQuery()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(new StatementPageReply(operation == "open-accounts" ? "opened" : "waiting"));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"),
            (_, _) => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, cancellation.Token));
        Assert.DoesNotContain("query", calls);
        Assert.Contains("dispose", calls);
    }

    [Fact]
    public async Task PageFailureIsNotRetriedOrExposedAsRawPageContent()
    {
        var attempts = 0;
        var page = new Page((operation, _, _) =>
        {
            if (operation == "open-accounts") attempts++;
            return Task.FromResult(new StatementPageReply("failed", Code: "SENSITIVE_PAGE_TEXT"));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"));
        var error = await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.DoesNotContain("SENSITIVE", error.Message);
    }

    [Fact]
    public async Task RepeatedExportsWaitForAccountListInsteadOfActingOnOldResultsOrQuery()
    {
        var stages = new Queue<string>();
        var lastStage = "home";
        var selections = 0;
        var queries = 0;
        var page = new Page((operation, _, _) =>
        {
            if (operation == "inspect") return Task.FromResult(new StatementPageReply("ready", lastStage = stages.Dequeue()));
            if (operation == "select-first") { Assert.Equal("accounts", lastStage); selections++; }
            if (operation == "query") { Assert.Equal("query", lastStage); queries++; }
            return Task.FromResult(operation switch
            {
                "open-accounts" => new StatementPageReply("opened"),
                "select-first" => new StatementPageReply("selected"),
                "query" => new StatementPageReply("queried"),
                "identify" => new StatementPageReply("identified", Identity: Identity),
                "download" => new StatementPageReply("downloading"),
                _ => new StatementPageReply("disposed")
            });
        });
        var coordinator = new StatementExportCoordinator(page, _ => new Download(() => Task.FromResult("statement.pdf")),
            (_, _) => Task.CompletedTask);
        for (var month = 1; month <= 3; month++)
        {
            foreach (var stage in new[] { "results", "query", "query", "accounts", "query", "results" }) stages.Enqueue(stage);
            Assert.Equal("statement.pdf", await coordinator.ExportAsync(
                StatementExportMonth.Create(new DateTime(2026, 9, 30), month), _ => { }, CancellationToken.None));
            Assert.Empty(stages);
        }
        Assert.Equal(3, selections);
        Assert.Equal(3, queries);
        Assert.Equal(3, page.RunIds.Distinct().Count());
    }

    [Fact]
    public async Task TransientUnsupportedInspectionWaitsWithoutRepeatingMutations()
    {
        var calls = new List<string>();
        var stages = new Queue<StatementPageReply>([
            new("unsupported"), new("ready", "accounts"),
            new("unsupported"), new("ready", "query"),
            new("unsupported"), new("ready", "results")]);
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(operation == "inspect" ? stages.Dequeue() : new StatementPageReply(operation switch
            {
                "open-accounts" => "opened", "select-first" => "selected", "query" => "queried",
                "identify" => "identified", "download" => "downloading", _ => "disposed"
            }, Identity: operation == "identify" ? Identity : null));
        });
        var delays = 0;
        var coordinator = new StatementExportCoordinator(page, _ => new Download(() => Task.FromResult("desktop.pdf")),
            (_, _) => { delays++; return Task.CompletedTask; });

        Assert.Equal("desktop.pdf", await coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, CancellationToken.None));
        Assert.Equal(3, delays);
        foreach (var operation in new[] { "open-accounts", "select-first", "query", "download" })
            Assert.Equal(1, calls.Count(call => call == operation));
    }

    [Fact]
    public async Task PersistentUnsupportedInspectionStopsAfterFiveConsecutiveSamples()
    {
        var inspections = 0;
        var delays = 0;
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            if (operation == "inspect") inspections++;
            return Task.FromResult(new StatementPageReply(operation == "open-accounts" ? "opened" : "unsupported"));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"),
            (_, _) => { delays++; return Task.CompletedTask; });

        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, CancellationToken.None));
        Assert.Equal(5, inspections);
        Assert.Equal(4, delays);
        Assert.Equal(1, calls.Count(call => call == "open-accounts"));
        Assert.DoesNotContain("select-first", calls);
        Assert.Contains("dispose", calls);
    }

    [Fact]
    public async Task CancellationDuringUnsupportedGraceStopsBeforeAnotherInspectionOrMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(new StatementPageReply(operation == "open-accounts" ? "opened" : "unsupported"));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"),
            (_, _) => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, cancellation.Token));
        Assert.Equal(new[] { "open-accounts", "inspect", "dispose" }, calls);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("waiting")]
    public async Task RecognizedInspectionResetsConsecutiveUnsupportedCount(string status)
    {
        var replies = new Queue<StatementPageReply>([
            new("unsupported"), new("unsupported"), new("unsupported"), new("unsupported"),
            new(status, "home"),
            new("unsupported"), new("unsupported"), new("unsupported"), new("unsupported"), new("ready", "accounts")]);
        var selected = false;
        var page = new Page((operation, _, _) =>
        {
            if (operation == "select-first") { selected = true; return Task.FromResult(new StatementPageReply("failed")); }
            return Task.FromResult(operation == "inspect" ? replies.Dequeue() : new StatementPageReply("opened"));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, CancellationToken.None));
        Assert.True(selected);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("failed")]
    public async Task TerminalInspectionFailureDoesNotReceiveGrace(string status)
    {
        var inspections = 0;
        var page = new Page((operation, _, _) =>
        {
            if (operation == "inspect") inspections++;
            return Task.FromResult(new StatementPageReply(operation == "open-accounts" ? "opened" : status));
        });
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not arm"),
            (_, _) => throw new Exception("must not delay terminal failure"));

        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 9), _ => { }, CancellationToken.None));
        Assert.Equal(1, inspections);
    }

    [Fact]
    public async Task ReturnsHomeAndWaitsBeforeOpeningAccounts()
    {
        var calls = new List<string>();
        var ready = false;
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(operation == "ensure-home"
                ? new StatementPageReply(ready ? "home-ready" : "waiting", "home")
                : new StatementPageReply("failed"));
        }, handleHome: true);
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not download"),
            (_, _) => { ready = true; return Task.CompletedTask; });

        await Assert.ThrowsAsync<StatementExportException>(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, CancellationToken.None));

        Assert.Equal(new[] { "ensure-home", "ensure-home", "open-accounts", "dispose" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeFailureOrCancellationStopsBeforeAccountActions(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var page = new Page((operation, _, _) =>
        {
            calls.Add(operation);
            return Task.FromResult(new StatementPageReply(cancel ? "waiting" : "unsupported", Code: "home-not-found"));
        }, handleHome: true);
        var coordinator = new StatementExportCoordinator(page, _ => throw new Exception("must not download"),
            (_, _) => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); });
        var error = await Record.ExceptionAsync(() => coordinator.ExportAsync(
            StatementExportMonth.Create(new DateTime(2026, 9, 30), 1), _ => { }, cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<StatementExportException>(error);
        Assert.Equal(new[] { "ensure-home", "dispose" }, calls);
    }

    private sealed class Page(Func<string, StatementExportMonth, CancellationToken, Task<StatementPageReply>> execute,
        bool handleHome = false) : IStatementExportPage
    {
        public List<string> RunIds { get; } = [];
        public Func<string>? InspectStage { get; set; }
        public Task<StatementPageReply> ExecuteAsync(string operation, StatementExportMonth range, string runId, CancellationToken token)
        {
            RunIds.Add(runId);
            // Existing downstream scenarios begin with an already-ready homepage.
            if (operation == "ensure-home" && !handleHome) return Task.FromResult(new StatementPageReply("home-ready", "home"));
            return operation == "inspect" && InspectStage is not null
                ? Task.FromResult(new StatementPageReply("ready", InspectStage())) : execute(operation, range, token);
        }
    }

    private sealed class Download(Func<Task<string>> wait) : IStatementExportDownload
    {
        public bool Disposed { get; private set; }
        public Task<string> WaitAsync(CancellationToken token) => wait();
        public void Dispose() => Disposed = true;
    }
}
