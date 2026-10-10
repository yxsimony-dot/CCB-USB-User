using System.Text.RegularExpressions;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal sealed partial record NativePromptBrokerPolicy(
    string PolicyHash,
    ClientCertificateNativePromptOptions Prompt,
    NativePromptBrokerTrustedClient? TrustedClient = null)
{
    public bool IsPackageValid =>
        Prompt is
        {
            Enabled: true,
            TimeoutMilliseconds: >= 1000 and <= 30000
        } &&
        string.Equals(
            Prompt.BrokerPipeName,
            WindowsNativePromptBrokerInstaller.BrokerPipeName,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(Prompt.BrokerPipeName) &&
        !string.IsNullOrWhiteSpace(Prompt.PolicyId) &&
        UpperSha256().IsMatch(PolicyHash) &&
        !string.IsNullOrWhiteSpace(Prompt.WindowClass) &&
        IsPublisherValid(Prompt) &&
        IsPromptTargetValid(Prompt);

    public bool IsValid => IsPackageValid && TrustedClient?.IsValid == true;

    private static bool IsPromptTargetValid(ClientCertificateNativePromptOptions prompt)
    {
        if (string.Equals(
                prompt.WindowScope,
                NativePromptWindowScopes.TopLevelProcess,
                StringComparison.Ordinal))
        {
            return IsAbsoluteLocalExecutable(prompt.ProcessPath) &&
                   UpperSha256().IsMatch(prompt.ProcessSha256) &&
                   !string.IsNullOrWhiteSpace(prompt.WindowTitle) &&
                   prompt.RequiredChildWindowClasses is { Count: > 0 } &&
                   prompt.RequiredChildWindowClasses.All(value => !string.IsNullOrWhiteSpace(value));
        }

        var isTrustedClientDescendant = string.Equals(
            prompt.WindowScope,
            NativePromptWindowScopes.TrustedClientDescendant,
            StringComparison.Ordinal);
        var isTrustedClientTopLevel = string.Equals(
            prompt.WindowScope,
            NativePromptWindowScopes.TrustedClientTopLevel,
            StringComparison.Ordinal);
        return (isTrustedClientDescendant || isTrustedClientTopLevel) &&
               string.IsNullOrEmpty(prompt.ProcessPath) &&
               (string.IsNullOrEmpty(prompt.ProcessSha256) || UpperSha256().IsMatch(prompt.ProcessSha256)) &&
               IsExecutableFileName(prompt.ProcessFileName) &&
               (isTrustedClientTopLevel
                   ? !string.IsNullOrWhiteSpace(prompt.WindowTitle)
                   : prompt.WindowTitle is not null) &&
               prompt.RequiredChildWindowClasses is { Count: 0 } &&
               (!isTrustedClientDescendant ||
                   (!string.IsNullOrWhiteSpace(prompt.TrustedRootWindowTitle) &&
                    !string.IsNullOrWhiteSpace(prompt.TrustedRootWindowClassPrefix))) &&
               !string.IsNullOrWhiteSpace(prompt.InputWindowClass) &&
               prompt.InputControlId is > 0 &&
               !string.IsNullOrWhiteSpace(prompt.SubmitWindowClass) &&
               prompt.SubmitControlId is > 0 &&
               prompt.InputControlId != prompt.SubmitControlId;
    }

    private static bool IsPublisherValid(ClientCertificateNativePromptOptions prompt) =>
        string.Equals(
            prompt.WindowScope,
            NativePromptWindowScopes.TrustedClientTopLevel,
            StringComparison.Ordinal)
            ? string.IsNullOrEmpty(prompt.PublisherCertificateThumbprint)
            : UpperThumbprint().IsMatch(prompt.PublisherCertificateThumbprint);

    private static bool IsExecutableFileName(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsAbsoluteLocalExecutable(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                   !path.StartsWith("//", StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpperSha256();

    [GeneratedRegex("^[0-9A-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpperThumbprint();
}

internal sealed partial record NativePromptBrokerTrustedClient(
    string ProcessPath,
    string ProcessSha256,
    string ApplicationAssemblySha256,
    string CoreAssemblySha256,
    string WindowsAssemblySha256)
{
    public const string ProcessFileName = "UsbWebLauncher.App.exe";
    public const string ApplicationAssemblyFileName = "UsbWebLauncher.App.dll";
    public const string CoreAssemblyFileName = "UsbWebLauncher.Core.dll";
    public const string WindowsAssemblyFileName = "UsbWebLauncher.Windows.dll";

    public bool IsValid =>
        IsAbsoluteLocalExecutable(ProcessPath) &&
        UpperSha256().IsMatch(ProcessSha256) &&
        UpperSha256().IsMatch(ApplicationAssemblySha256) &&
        UpperSha256().IsMatch(CoreAssemblySha256) &&
        UpperSha256().IsMatch(WindowsAssemblySha256);

    private static bool IsAbsoluteLocalExecutable(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                   !path.StartsWith("//", StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpperSha256();
}

internal sealed record NativePromptBrokerClientIdentity(
    string UserSid,
    int SessionId,
    bool IsInteractive,
    string ProcessPath,
    string ProcessSha256,
    string ApplicationAssemblySha256,
    string CoreAssemblySha256,
    string WindowsAssemblySha256,
    int ProcessId);

internal sealed class NativePromptBrokerArmResult : IAsyncDisposable
{
    public NativePromptBrokerArmResult(
        NativePromptArmResponse response,
        NativePromptBrokerAttempt? attempt = null)
    {
        Response = response;
        Attempt = attempt;
    }

    public NativePromptArmResponse Response { get; }

    public NativePromptBrokerAttempt? Attempt { get; }

    public async ValueTask DisposeAsync()
    {
        if (Attempt is not null)
        {
            await Attempt.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class NativePromptBrokerAttempt : IAsyncDisposable
{
    private readonly INativePromptExecutor _executor;
    private readonly ClientCertificateNativePromptOptions _policy;
    private readonly int _trustedClientProcessId;
    private readonly CancellationToken _shutdownToken;
    private readonly Action _release;
    private ClearableSecretBuffer? _ownedPin;
    private int _state;

    public NativePromptBrokerAttempt(
        INativePromptExecutor executor,
        ClientCertificateNativePromptOptions policy,
        ClearableSecretBuffer ownedPin,
        int trustedClientProcessId,
        CancellationToken shutdownToken,
        Action release)
    {
        _executor = executor;
        _policy = policy;
        _trustedClientProcessId = trustedClientProcessId;
        _ownedPin = ownedPin;
        _shutdownToken = shutdownToken;
        _release = release;
    }

    public async ValueTask<ClientCertificateNativePromptResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return new(ClientCertificateNativePromptStatus.Replayed);
        }

        var pin = Interlocked.Exchange(ref _ownedPin, null);
        if (pin is null)
        {
            Release();
            return new(ClientCertificateNativePromptStatus.Replayed);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownToken);
        try
        {
            if (linked.IsCancellationRequested)
            {
                pin.Dispose();
                return new(ClientCertificateNativePromptStatus.Cancelled);
            }

            return await _executor.ExecuteAsync(
                _policy,
                pin,
                _trustedClientProcessId,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            pin.Dispose();
            return new(ClientCertificateNativePromptStatus.Cancelled);
        }
        catch
        {
            pin.Dispose();
            return new(ClientCertificateNativePromptStatus.UnknownFailure);
        }
        finally
        {
            Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            Interlocked.Exchange(ref _ownedPin, null)?.Dispose();
            Release();
        }

        return ValueTask.CompletedTask;
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _state, 3) != 3)
        {
            _release();
        }
    }
}

internal sealed partial class WindowsNativePromptBrokerServer : IAsyncDisposable
{
    internal const int ConsumedAttemptCapacity = 4096;

    private readonly NativePromptBrokerPolicy _policy;
    private readonly string _expectedUserSid;
    private readonly int _expectedSessionId;
    private readonly INativePromptExecutor _executor;
    private readonly SemaphoreSlim _singleAttempt = new(1, 1);
    private readonly object _replaySync = new();
    private readonly HashSet<NativePromptReplayKey> _consumedAttempts = [];
    private readonly Queue<NativePromptReplayKey> _consumedAttemptOrder = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _stopped;

    public WindowsNativePromptBrokerServer(
        NativePromptBrokerPolicy policy,
        string expectedUserSid,
        int expectedSessionId,
        INativePromptExecutor executor)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _expectedUserSid = expectedUserSid ?? throw new ArgumentNullException(nameof(expectedUserSid));
        _expectedSessionId = expectedSessionId;
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public bool IsHealthy =>
        Volatile.Read(ref _stopped) == 0 && _policy.IsValid;

    internal int ConsumedAttemptCount
    {
        get
        {
            lock (_replaySync)
            {
                return _consumedAttempts.Count;
            }
        }
    }

    public bool IsExpectedClient(NativePromptBrokerClientIdentity identity) =>
        identity is not null &&
        identity.ProcessId > 0 &&
        _policy.TrustedClient is { IsValid: true } trustedClient &&
        identity.IsInteractive &&
        identity.SessionId == _expectedSessionId &&
        string.Equals(identity.UserSid, _expectedUserSid, StringComparison.Ordinal) &&
        string.Equals(identity.ProcessPath, trustedClient.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(identity.ProcessSha256, trustedClient.ProcessSha256, StringComparison.Ordinal) &&
        string.Equals(
            identity.ApplicationAssemblySha256,
            trustedClient.ApplicationAssemblySha256,
            StringComparison.Ordinal) &&
        string.Equals(identity.CoreAssemblySha256, trustedClient.CoreAssemblySha256, StringComparison.Ordinal) &&
        string.Equals(
            identity.WindowsAssemblySha256,
            trustedClient.WindowsAssemblySha256,
            StringComparison.Ordinal);

    public ValueTask<NativePromptBrokerArmResult> ArmAsync(
        NativePromptBrokerClientIdentity identity,
        NativePromptArmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        ClearableSecretBuffer? ownedPin = null;
        var gateHeld = false;
        try
        {
            if (Volatile.Read(ref _stopped) != 0 || cancellationToken.IsCancellationRequested)
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.BrokerUnavailable);
            }

            if (!IsExpectedClient(identity))
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.PeerIdentityMismatch);
            }

            if (!_policy.IsValid ||
                !string.Equals(request.PolicyId, _policy.Prompt.PolicyId, StringComparison.Ordinal) ||
                !string.Equals(request.PolicyHash, _policy.PolicyHash, StringComparison.Ordinal) ||
                !UpperSha256().IsMatch(request.PolicyHash))
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.PolicyMismatch);
            }

            var replayKey = NativePromptReplayKey.From(request.AttemptId);
            if (WasConsumed(replayKey))
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.Replayed);
            }

            if (!_singleAttempt.Wait(0))
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.BrokerUnavailable);
            }

            gateHeld = true;
            if (!TryConsume(replayKey))
            {
                return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.Replayed);
            }

            ownedPin = new ClearableSecretBuffer(request.Pin.Span);
            var attempt = new NativePromptBrokerAttempt(
                _executor,
                _policy.Prompt,
                ownedPin,
                identity.ProcessId,
                _shutdown.Token,
                () => _singleAttempt.Release());
            ownedPin = null;
            gateHeld = false;
            return ValueTask.FromResult(new NativePromptBrokerArmResult(
                new NativePromptArmResponse(
                    request.AttemptId,
                    ClientCertificateNativePromptArmStatus.Armed),
                attempt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.Cancelled);
        }
        catch
        {
            return Rejected(request.AttemptId, ClientCertificateNativePromptArmStatus.UnknownFailure);
        }
        finally
        {
            ownedPin?.Dispose();
            request.Dispose();
            if (gateHeld)
            {
                _singleAttempt.Release();
            }
        }
    }

    public ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _shutdown.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    private bool WasConsumed(NativePromptReplayKey key)
    {
        lock (_replaySync)
        {
            return _consumedAttempts.Contains(key);
        }
    }

    private bool TryConsume(NativePromptReplayKey key)
    {
        lock (_replaySync)
        {
            if (!_consumedAttempts.Add(key))
            {
                return false;
            }

            _consumedAttemptOrder.Enqueue(key);
            while (_consumedAttemptOrder.Count > ConsumedAttemptCapacity)
            {
                _consumedAttempts.Remove(_consumedAttemptOrder.Dequeue());
            }

            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _singleAttempt.Dispose();
    }

    private static ValueTask<NativePromptBrokerArmResult> Rejected(
        NativePromptAttemptId attemptId,
        ClientCertificateNativePromptArmStatus status) =>
        ValueTask.FromResult(new NativePromptBrokerArmResult(
            new NativePromptArmResponse(attemptId, status)));

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpperSha256();

    private readonly record struct NativePromptReplayKey(long NonceHigh, long NonceLow)
    {
        public static NativePromptReplayKey From(NativePromptAttemptId attemptId) =>
            new(attemptId.NonceHigh, attemptId.NonceLow);
    }
}
