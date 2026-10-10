using System.Globalization;
using System.Text;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal static class NativePromptBrokerFailureLog
{
    private const string ProductDirectoryName = "UsbWebLauncher";
    private const string BrokerDirectoryName = "NativePromptBroker";
    private const string FileName = "last-server-error.txt";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectoryName,
        BrokerDirectoryName,
        FileName);

    public static void Clear(string? path = null)
    {
        try
        {
            var target = Path.GetFullPath(path ?? DefaultPath);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch
        {
        }
    }

    public static void Write(Exception exception, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            var target = Path.GetFullPath(path ?? DefaultPath);
            var directory = Path.GetDirectoryName(target) ??
                throw new InvalidOperationException("The broker diagnostic path has no directory.");
            Directory.CreateDirectory(directory);

            var text = new StringBuilder();
            text.Append("TimestampUtc=")
                .AppendLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var current = exception;
            for (var depth = 0; current is not null && depth < 8; depth++)
            {
                text.Append("Exception")
                    .Append(depth)
                    .Append(".Type=")
                    .AppendLine(current.GetType().FullName ?? current.GetType().Name);
                text.Append("Exception")
                    .Append(depth)
                    .Append(".HResult=0x")
                    .AppendLine(unchecked((uint)current.HResult).ToString("X8", CultureInfo.InvariantCulture));
                current = current.InnerException;
            }

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                text.AppendLine("StackTrace=");
                text.AppendLine(exception.StackTrace);
            }

            File.WriteAllText(target, text.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }
}

internal enum NativePromptExecutionStage
{
    PolicyValidation,
    WindowEnumeration,
    CandidateSelection,
    ForegroundWait,
    InputFocus,
    InputMethodPreparation,
    InputBuild,
    IdentityRecapture,
    FocusValidation,
    InputStateValidation,
    InputSend,
    InputProcessingWait,
    SubmitWait,
    SubmitIdentityRecapture,
    SubmitActivation,
    Completed,
    Cancelled,
    UnexpectedFailure,
    ModifierProcessingWait,
    CharacterProcessingWait
}

internal readonly record struct NativePromptExecutionObservation(
    ClientCertificateNativePromptStatus Status,
    NativePromptExecutionStage Stage,
    int TrustedClientProcessId,
    int EnumerationCount,
    int CandidateCount,
    int MatchingCandidateCount,
    int SelectedProcessId,
    long ElapsedMilliseconds);

internal interface INativePromptExecutionDiagnostics
{
    void Clear();

    void Record(NativePromptExecutionObservation observation);
}

internal sealed class NativePromptExecutionDiagnostics : INativePromptExecutionDiagnostics
{
    public void Clear() => NativePromptExecutionStatusLog.Clear();

    public void Record(NativePromptExecutionObservation observation) =>
        NativePromptExecutionStatusLog.Write(observation);
}

internal sealed class NoOpNativePromptExecutionDiagnostics : INativePromptExecutionDiagnostics
{
    public static NoOpNativePromptExecutionDiagnostics Instance { get; } = new();

    private NoOpNativePromptExecutionDiagnostics()
    {
    }

    public void Clear()
    {
    }

    public void Record(NativePromptExecutionObservation observation)
    {
        _ = observation;
    }
}

internal static class NativePromptExecutionStatusLog
{
    private const string ProductDirectoryName = "UsbWebLauncher";
    private const string BrokerDirectoryName = "NativePromptBroker";
    private const string FileName = "last-execution-status.txt";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectoryName,
        BrokerDirectoryName,
        FileName);

    public static void Clear(string? path = null)
    {
        try
        {
            var target = Path.GetFullPath(path ?? DefaultPath);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch
        {
        }
    }

    public static void Write(
        NativePromptExecutionObservation observation,
        string? path = null)
    {
        try
        {
            var target = Path.GetFullPath(path ?? DefaultPath);
            var directory = Path.GetDirectoryName(target) ??
                throw new InvalidOperationException("The broker diagnostic path has no directory.");
            Directory.CreateDirectory(directory);

            var text = new StringBuilder();
            text.Append("TimestampUtc=")
                .AppendLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            text.Append("Status=").AppendLine(observation.Status.ToString());
            text.Append("Stage=").AppendLine(observation.Stage.ToString());
            text.Append("TrustedClientProcessId=")
                .AppendLine(observation.TrustedClientProcessId.ToString(CultureInfo.InvariantCulture));
            text.Append("EnumerationCount=")
                .AppendLine(observation.EnumerationCount.ToString(CultureInfo.InvariantCulture));
            text.Append("CandidateCount=")
                .AppendLine(observation.CandidateCount.ToString(CultureInfo.InvariantCulture));
            text.Append("MatchingCandidateCount=")
                .AppendLine(observation.MatchingCandidateCount.ToString(CultureInfo.InvariantCulture));
            text.Append("SelectedProcessId=")
                .AppendLine(observation.SelectedProcessId.ToString(CultureInfo.InvariantCulture));
            text.Append("ElapsedMilliseconds=")
                .AppendLine(observation.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(target, text.ToString(), Encoding.UTF8);
        }
        catch
        {
        }
    }
}

internal static class NativePromptBrokerProcessLifetime
{
    public static void TryCancel(CancellationTokenSource shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
