using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptBrokerDiagnosticsTests
{
    [Fact]
    public void FailureLog_RecordsTypeAndHResultWithoutExceptionMessage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"native-prompt-failure-{Guid.NewGuid():N}.txt");
        var secretMarker = $"secret-{Guid.NewGuid():N}";
        try
        {
            NativePromptBrokerFailureLog.Write(
                new InvalidOperationException(secretMarker),
                path);

            var contents = File.ReadAllText(path);
            Assert.Contains(typeof(InvalidOperationException).FullName!, contents, StringComparison.Ordinal);
            Assert.Contains("HResult=0x", contents, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMarker, contents, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExecutionStatusLog_RecordsOnlyPublicExecutionMetadata()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"native-prompt-status-{Guid.NewGuid():N}.txt");
        var secretMarker = $"secret-{Guid.NewGuid():N}";
        try
        {
            NativePromptExecutionStatusLog.Write(
                new(
                    ClientCertificateNativePromptStatus.FocusLost,
                    NativePromptExecutionStage.FocusValidation,
                    TrustedClientProcessId: 100,
                    EnumerationCount: 4,
                    CandidateCount: 7,
                    MatchingCandidateCount: 1,
                    SelectedProcessId: 200,
                    ElapsedMilliseconds: 345),
                path);

            var contents = File.ReadAllText(path);
            Assert.Contains("Status=FocusLost", contents, StringComparison.Ordinal);
            Assert.Contains("Stage=FocusValidation", contents, StringComparison.Ordinal);
            Assert.Contains("TrustedClientProcessId=100", contents, StringComparison.Ordinal);
            Assert.Contains("EnumerationCount=4", contents, StringComparison.Ordinal);
            Assert.Contains("CandidateCount=7", contents, StringComparison.Ordinal);
            Assert.Contains("MatchingCandidateCount=1", contents, StringComparison.Ordinal);
            Assert.Contains("SelectedProcessId=200", contents, StringComparison.Ordinal);
            Assert.Contains("ElapsedMilliseconds=345", contents, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMarker, contents, StringComparison.Ordinal);
            Assert.DoesNotContain("Pin", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CharacterCount", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("InputLength", contents, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProcessLifetimeCancellation_DoesNotThrowAfterSourceWasDisposed()
    {
        var shutdown = new CancellationTokenSource();
        shutdown.Dispose();

        var exception = Record.Exception(
            () => NativePromptBrokerProcessLifetime.TryCancel(shutdown));

        Assert.Null(exception);
    }
}
