using System.Text;
using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementDiagnosticLogTests
{
    [Fact]
    public async Task ReplacesPreviousRunAndDrainsAcceptedRecordsOnDispose()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "old-run");
            var log = new StatementDiagnosticLog(path);
            Assert.True(log.TryWrite("capture-start", new { version = 1 }));
            log.Dispose();
            await log.Completion;
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Single(lines);
            using var record = JsonDocument.Parse(lines[0]);
            Assert.Equal("capture-start", record.RootElement.GetProperty("eventName").GetString());
            Assert.False(log.TryWrite("late", new { }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StopsAtByteLimitWithValidJsonMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var log = new StatementDiagnosticLog(path, maxBytes: 512);
            for (var i = 0; i < 10; i++) log.TryWrite("structure", new { data = new string('中', 60) });
            log.Dispose();
            await log.Completion;
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.InRange(bytes.Length, 1, 512);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines) using (JsonDocument.Parse(line)) { }
            Assert.Contains("file-limit", lines[^1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task IoFailureDoesNotEscapeIntoBusinessFlow()
    {
        var parentFile = Path.GetTempFileName();
        try
        {
            var log = new StatementDiagnosticLog(Path.Combine(parentFile, "impossible.jsonl"));
            log.TryWrite("capture-start", new { });
            log.Dispose();
            await log.Completion;
            Assert.False(log.TryWrite("late", new { }));
        }
        finally { File.Delete(parentFile); }
    }
}
