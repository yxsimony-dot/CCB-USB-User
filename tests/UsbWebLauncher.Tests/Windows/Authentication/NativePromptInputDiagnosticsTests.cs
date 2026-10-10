using System.Text.Json;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class NativePromptInputDiagnosticsTests
{
    [Fact]
    public void InputFailure_RecordsOnlyCountsStageAndFocusBooleans()
    {
        object? captured = null;
        var diagnostics = new WindowsNativePromptInputDiagnostics(
            _ => new NativePromptFailureFocus(false, true, true, false),
            data => captured = data);
        diagnostics.RecordInputFailure(NativePromptWindowMatcherTests.DescendantCandidate(), 2468,
            NativePromptExecutionStage.ModifierProcessingWait, "ForegroundMismatch", 1, 1);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(captured));
        var data = json.RootElement;
        Assert.Equal("input-failure", data.GetProperty("Phase").GetString());
        Assert.Equal("ModifierProcessingWait", data.GetProperty("Stage").GetString());
        Assert.Equal("ForegroundMismatch", data.GetProperty("FailureReason").GetString());
        Assert.Equal("after-cleanup", data.GetProperty("FocusSnapshotTiming").GetString());
        Assert.Equal(1, data.GetProperty("SentCharacterCount").GetInt32());
        Assert.Equal(1, data.GetProperty("ConfirmedCharacterCount").GetInt32());
        Assert.False(data.GetProperty("ForegroundMatches").GetBoolean());
        Assert.True(data.GetProperty("GuiThreadInfoSucceeded").GetBoolean());
        Assert.True(data.GetProperty("ActiveWindowMatches").GetBoolean());
        Assert.False(data.GetProperty("InputFocusMatches").GetBoolean());
        Assert.Equal(12, data.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InputFailure_FocusOrWriterFailureDoesNotThrow(bool focusThrows)
    {
        var diagnostics = new WindowsNativePromptInputDiagnostics(
            _ => focusThrows ? throw new InvalidOperationException() : new(false, false, false, false),
            _ => throw new IOException());
        diagnostics.RecordInputFailure(NativePromptWindowMatcherTests.DescendantCandidate(), 2468,
            NativePromptExecutionStage.CharacterProcessingWait, "Timeout", 1, 0);
    }

    [Fact]
    public void InputHistory_PreservesAttemptsAndContainsOnlyTechnicalMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "input.jsonl");
        try
        {
            NativePromptInputHistory.Append(new { Phase = "environment", TargetLayout = "8040804", ImeOpen = true }, path);
            NativePromptInputHistory.Append(new { Phase = "after-input", CharacterCount = 6 }, path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.True(first.RootElement.GetProperty("Data").GetProperty("ImeOpen").GetBoolean());
            Assert.False(first.RootElement.TryGetProperty("Pin", out _));
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal(6, second.RootElement.GetProperty("Data").GetProperty("CharacterCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InputHistory_RotatesAtBoundedSizeAndDoesNotThrowForUnavailablePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "input.jsonl");
        try
        {
            File.WriteAllText(path, new string(' ', 65_536));
            NativePromptInputHistory.Append(new { Phase = "environment" }, path);
            Assert.True(File.Exists(path + ".previous"));
            Assert.Single(File.ReadAllLines(path));
            NativePromptInputHistory.Append(new { Phase = "environment" }, directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
