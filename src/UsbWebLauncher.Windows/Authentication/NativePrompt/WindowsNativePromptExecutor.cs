using System.Diagnostics;
using System.Runtime.InteropServices;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface INativePromptExecutionClock
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class NativePromptExecutionClock : INativePromptExecutionClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp);

    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

internal interface INativePromptInputBatchBuilder
{
    NativePromptInput[]? Build(ClearableSecretBuffer secret, IntPtr targetWindow);
    NativePromptInput[]? BuildWithCapsLock(ClearableSecretBuffer secret, IntPtr targetWindow, bool capsLock) =>
        null;
}

internal readonly record struct NativePromptVirtualKey(
    ushort VirtualKey,
    bool RequiresShift);

internal interface INativePromptVirtualKeyMapper
{
    bool TryMap(ushort codeUnit, IntPtr targetWindow, out NativePromptVirtualKey key);
    bool TryMapWithCapsLock(ushort codeUnit, IntPtr targetWindow, bool capsLock, out NativePromptVirtualKey key)
    {
        key = default;
        return false;
    }
}

internal interface IWindowsNativePromptKeyboardApi
{
    uint GetWindowThreadId(IntPtr window);
    IntPtr GetKeyboardLayout(uint thread);
    short VkKeyScan(char character, IntPtr keyboardLayout);
    short GetKeyState(int virtualKey);
}

internal sealed class WindowsNativePromptKeyboardApi : IWindowsNativePromptKeyboardApi
{
    public uint GetWindowThreadId(IntPtr window) => GetWindowThreadProcessId(window, out _);
    public IntPtr GetKeyboardLayout(uint thread) => GetKeyboardLayoutNative(thread);
    public short VkKeyScan(char character, IntPtr keyboardLayout) => VkKeyScanExW(character, keyboardLayout);
    public short GetKeyState(int virtualKey) => GetKeyStateNative(virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
    private static extern IntPtr GetKeyboardLayoutNative(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanExW(char character, IntPtr keyboardLayout);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetKeyStateNative(int virtualKey);
}

internal sealed class WindowsNativePromptVirtualKeyMapper : INativePromptVirtualKeyMapper
{
    private const byte ShiftState = 0x01;
    private const byte ControlState = 0x02;
    private const byte AltState = 0x04;
    private const int CapsLockVirtualKey = 0x14;
    private readonly IWindowsNativePromptKeyboardApi _native;

    public WindowsNativePromptVirtualKeyMapper()
        : this(new WindowsNativePromptKeyboardApi())
    {
    }

    internal WindowsNativePromptVirtualKeyMapper(IWindowsNativePromptKeyboardApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public bool TryMap(ushort codeUnit, IntPtr targetWindow, out NativePromptVirtualKey key) =>
        TryMapCore(codeUnit, targetWindow, null, out key);

    public bool TryMapWithCapsLock(ushort codeUnit, IntPtr targetWindow, bool capsLock, out NativePromptVirtualKey key) =>
        TryMapCore(codeUnit, targetWindow, capsLock, out key);

    private bool TryMapCore(ushort codeUnit, IntPtr targetWindow, bool? capsLock, out NativePromptVirtualKey key)
    {
        key = default;
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var targetThread = _native.GetWindowThreadId(targetWindow);
        if (targetThread == 0)
        {
            return false;
        }

        var keyboardLayout = _native.GetKeyboardLayout(targetThread);
        if (keyboardLayout == IntPtr.Zero)
        {
            return false;
        }

        var mapping = _native.VkKeyScan((char)codeUnit, keyboardLayout);
        var capsLockActive = capsLock ?? ((_native.GetKeyState(CapsLockVirtualKey) & 0x0001) != 0);
        return TryCreateMapping(codeUnit, mapping, capsLockActive, out key);
    }

    internal static bool TryCreateMapping(
        ushort codeUnit,
        short mapping,
        bool capsLockActive,
        out NativePromptVirtualKey key)
    {
        if (mapping == -1)
        {
            key = default;
            return false;
        }

        var virtualKey = unchecked((ushort)(mapping & 0xFF));
        var shiftState = unchecked((byte)((mapping >> 8) & 0xFF));
        if (virtualKey == 0 || (shiftState & (ControlState | AltState)) != 0)
        {
            key = default;
            return false;
        }

        var requiresShift = (shiftState & ShiftState) != 0;
        if (IsAsciiLetter(codeUnit) && capsLockActive)
        {
            requiresShift = !requiresShift;
        }

        key = new(virtualKey, requiresShift);
        return true;
    }

    private static bool IsAsciiLetter(ushort codeUnit) =>
        codeUnit is >= (ushort)'A' and <= (ushort)'Z' or
            >= (ushort)'a' and <= (ushort)'z';

}

internal sealed class NativePromptInputBatchBuilder : INativePromptInputBatchBuilder
{
    private readonly INativePromptVirtualKeyMapper _mapper;

    public NativePromptInputBatchBuilder()
        : this(new WindowsNativePromptVirtualKeyMapper())
    {
    }

    internal NativePromptInputBatchBuilder(INativePromptVirtualKeyMapper mapper)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    public NativePromptInput[]? Build(ClearableSecretBuffer secret, IntPtr targetWindow) =>
        BuildCore(secret, targetWindow, null);

    public NativePromptInput[]? BuildWithCapsLock(ClearableSecretBuffer secret, IntPtr targetWindow, bool capsLock) =>
        BuildCore(secret, targetWindow, capsLock);

    private NativePromptInput[]? BuildCore(ClearableSecretBuffer secret, IntPtr targetWindow, bool? capsLock)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.ByteLength < 0 ||
            (secret.ByteLength % sizeof(char)) != 0 ||
            (secret.ByteLength / sizeof(char)) > NativePromptProtocol.MaxPinCharacters)
        {
            return null;
        }

        var characterCount = secret.ByteLength / sizeof(char);
        var keys = new NativePromptVirtualKey[characterCount];
        try
        {
            for (var index = 0; index < characterCount; index++)
            {
                var codeUnit = unchecked((ushort)Marshal.ReadInt16(
                    secret.Pointer,
                    checked(index * sizeof(char))));
                var mapped = capsLock is { } pinned
                    ? _mapper.TryMapWithCapsLock(codeUnit, targetWindow, pinned, out keys[index])
                    : _mapper.TryMap(codeUnit, targetWindow, out keys[index]);
                // The Caps-pinned vendor path deliberately avoids transient Shift.
                if (!mapped || (capsLock.HasValue && keys[index].RequiresShift))
                {
                    return null;
                }
            }

            var inputCount = checked(
                keys.Sum(key => key.RequiresShift ? 4 : 2));
            var inputs = new NativePromptInput[inputCount];
            try
            {
                var inputIndex = 0;
                foreach (var key in keys)
                {
                    if (key.RequiresShift)
                    {
                        inputs[inputIndex++] = NativePromptInput.VirtualKey(
                            NativePromptInput.ShiftVirtualKey,
                            keyUp: false);
                    }

                    inputs[inputIndex++] = NativePromptInput.VirtualKey(
                        key.VirtualKey,
                        keyUp: false);
                    inputs[inputIndex++] = NativePromptInput.VirtualKey(
                        key.VirtualKey,
                        keyUp: true);

                    if (key.RequiresShift)
                    {
                        inputs[inputIndex++] = NativePromptInput.VirtualKey(
                            NativePromptInput.ShiftVirtualKey,
                            keyUp: true);
                    }
                }

                return inputs;
            }
            catch
            {
                Array.Clear(inputs, 0, inputs.Length);
                throw;
            }
        }
        finally
        {
            Array.Clear(keys, 0, keys.Length);
        }
    }
}

internal interface INativePromptExecutor
{
    ValueTask<ClientCertificateNativePromptResult> ExecuteAsync(
        ClientCertificateNativePromptOptions policy,
        ClearableSecretBuffer ownedPin,
        int trustedClientProcessId,
        CancellationToken cancellationToken = default);
}

internal interface INativePromptInputDiagnostics
{
    void RecordEnvironment(NativePromptWindowSnapshot snapshot, int trustedClientProcessId);
    void RecordCharacterCount(NativePromptWindowSnapshot snapshot, int trustedClientProcessId, string phase, int count);
    void RecordInputFailure(NativePromptWindowSnapshot snapshot, int trustedClientProcessId,
        NativePromptExecutionStage stage, string failureReason, int sentCharacterCount, int confirmedCharacterCount) { }
}

internal sealed class NoOpNativePromptInputDiagnostics : INativePromptInputDiagnostics
{
    public static NoOpNativePromptInputDiagnostics Instance { get; } = new();
    public void RecordEnvironment(NativePromptWindowSnapshot snapshot, int trustedClientProcessId) { }
    public void RecordCharacterCount(NativePromptWindowSnapshot snapshot, int trustedClientProcessId, string phase, int count) { }
}

internal sealed class WindowsNativePromptExecutor : INativePromptExecutor
{
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ForegroundActivationTimeout = TimeSpan.FromMilliseconds(500);
    private readonly IWindowsNativePromptApi _native;
    private readonly INativePromptExecutionClock _clock;
    private readonly INativePromptInputBatchBuilder _inputBatchBuilder;
    private readonly INativePromptExecutionDiagnostics _diagnostics;
    private readonly INativePromptInputDiagnostics _inputDiagnostics;
    private readonly INativePromptCapsLockApi _capsLockApi;

    public WindowsNativePromptExecutor()
        : this(
            new WindowsNativePromptApi(),
            new NativePromptExecutionClock(),
            new NativePromptInputBatchBuilder(),
            new NativePromptExecutionDiagnostics(),
            new WindowsNativePromptInputDiagnostics())
    {
    }

    internal WindowsNativePromptExecutor(
        IWindowsNativePromptApi native,
        INativePromptExecutionClock clock)
        : this(
            native,
            clock,
            new NativePromptInputBatchBuilder(),
            NoOpNativePromptExecutionDiagnostics.Instance)
    {
    }

    internal WindowsNativePromptExecutor(
        IWindowsNativePromptApi native,
        INativePromptExecutionClock clock,
        INativePromptInputBatchBuilder inputBatchBuilder)
        : this(
            native,
            clock,
            inputBatchBuilder,
            NoOpNativePromptExecutionDiagnostics.Instance)
    {
    }

    internal WindowsNativePromptExecutor(
        IWindowsNativePromptApi native,
        INativePromptExecutionClock clock,
        INativePromptInputBatchBuilder inputBatchBuilder,
        INativePromptExecutionDiagnostics diagnostics,
        INativePromptInputDiagnostics? inputDiagnostics = null,
        INativePromptCapsLockApi? capsLockApi = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _inputBatchBuilder = inputBatchBuilder ??
            throw new ArgumentNullException(nameof(inputBatchBuilder));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _inputDiagnostics = inputDiagnostics ?? NoOpNativePromptInputDiagnostics.Instance;
        _capsLockApi = capsLockApi ?? new WindowsNativePromptCapsLock();
    }

    public async ValueTask<ClientCertificateNativePromptResult> ExecuteAsync(
        ClientCertificateNativePromptOptions policy,
        ClearableSecretBuffer ownedPin,
        int trustedClientProcessId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedPin);
        var started = _clock.GetTimestamp();
        var enumerationCount = 0;
        var candidateCount = 0;
        var matchingCandidateCount = 0;
        var selectedProcessId = 0;
        var currentStage = NativePromptExecutionStage.PolicyValidation;
        TryClearDiagnostics();

        ClientCertificateNativePromptResult Complete(
            ClientCertificateNativePromptStatus status,
            NativePromptExecutionStage stage)
        {
            var elapsed = _clock.GetElapsedTime(started);
            TryRecordDiagnostics(new(
                status,
                stage,
                trustedClientProcessId,
                enumerationCount,
                candidateCount,
                matchingCandidateCount,
                selectedProcessId,
                Math.Max(0, checked((long)elapsed.TotalMilliseconds))));
            return new(status);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(policy);
            cancellationToken.ThrowIfCancellationRequested();
            if (!policy.Enabled || policy.TimeoutMilliseconds <= 0)
            {
                return Complete(
                    ClientCertificateNativePromptStatus.PolicyMismatch,
                    NativePromptExecutionStage.PolicyValidation);
            }

            var timeout = TimeSpan.FromMilliseconds(policy.TimeoutMilliseconds);
            NativePromptWindowSnapshot? selected = null;
            while (selected is null)
            {
                currentStage = NativePromptExecutionStage.WindowEnumeration;
                var candidates = _native.EnumerateWindowSnapshots(policy, trustedClientProcessId);
                enumerationCount++;
                candidateCount = checked(candidateCount + candidates.Count);
                cancellationToken.ThrowIfCancellationRequested();

                currentStage = NativePromptExecutionStage.CandidateSelection;
                matchingCandidateCount = 0;
                foreach (var candidate in candidates)
                {
                    if (candidate is null ||
                        !NativePromptWindowMatcher.IsMatch(
                            policy,
                            candidate,
                            trustedClientProcessId))
                    {
                        continue;
                    }

                    selected = candidate;
                    selectedProcessId = candidate.ProcessId;
                    matchingCandidateCount++;
                    if (matchingCandidateCount > 1)
                    {
                        return Complete(
                            ClientCertificateNativePromptStatus.TargetMismatch,
                            NativePromptExecutionStage.CandidateSelection);
                    }
                }

                if (selected is not null)
                {
                    if (HasTimedOut(started, timeout))
                    {
                        return Complete(
                            ClientCertificateNativePromptStatus.TimedOut,
                            NativePromptExecutionStage.CandidateSelection);
                    }

                    break;
                }

                var elapsed = _clock.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    return Complete(
                        ClientCertificateNativePromptStatus.TargetMismatch,
                        NativePromptExecutionStage.WindowEnumeration);
                }

                var remaining = timeout - elapsed;
                await _clock.DelayAsync(
                    remaining < WindowPollInterval ? remaining : WindowPollInterval,
                    cancellationToken).ConfigureAwait(false);
            }

            currentStage = NativePromptExecutionStage.ForegroundWait;
            var foregroundStarted = _clock.GetTimestamp();
            _ = _native.SetForegroundWindow(selected.ForegroundWindowHandle);

            var foregroundResult = await WaitForForegroundAsync(
                selected.ForegroundWindowHandle,
                started,
                timeout,
                foregroundStarted,
                cancellationToken).ConfigureAwait(false);
            if (foregroundResult is not null)
            {
                return Complete(
                    foregroundResult.Value,
                    NativePromptExecutionStage.ForegroundWait);
            }

            currentStage = NativePromptExecutionStage.InputFocus;
            if (!_native.FocusInputControl(selected))
            {
                return Complete(
                    ClientCertificateNativePromptStatus.FocusLost,
                    NativePromptExecutionStage.InputFocus);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (HasTimedOut(started, timeout))
            {
                return Complete(
                    ClientCertificateNativePromptStatus.TimedOut,
                    NativePromptExecutionStage.InputFocus);
            }

            currentStage = NativePromptExecutionStage.InputMethodPreparation;
            _inputDiagnostics.RecordEnvironment(selected, trustedClientProcessId);
            cancellationToken.ThrowIfCancellationRequested();
            if (HasTimedOut(started, timeout))
            {
                return Complete(ClientCertificateNativePromptStatus.TimedOut, currentStage);
            }
            // Fixed PIN characters must bypass IME composition. In particular a
            // newly created bank dialog can inherit Chinese input after a restart.
            if (!_native.PrepareInputMethod(selected))
            {
                return Complete(ClientCertificateNativePromptStatus.InputFailed, currentStage);
            }
            _inputDiagnostics.RecordEnvironment(selected, trustedClientProcessId);
            currentStage = NativePromptExecutionStage.InputBuild;
            var pinCapsLock = selected.InputControlHandle != IntPtr.Zero && IsUppercaseAlphanumeric(ownedPin);
            var inputTarget = selected.InputControlHandle != IntPtr.Zero
                ? selected.InputControlHandle : selected.WindowHandle;
            var inputs = pinCapsLock
                ? _inputBatchBuilder.BuildWithCapsLock(ownedPin, inputTarget, true)
                : _inputBatchBuilder.Build(ownedPin, inputTarget);
            if (inputs is null)
            {
                return Complete(
                    ClientCertificateNativePromptStatus.InputFailed,
                    NativePromptExecutionStage.InputBuild);
            }

            try
            {
                var expectedCharacterCount = checked(
                    ownedPin.ByteLength / sizeof(char));
                var outcome = await ValidateSendAndSubmitOnceAsync(
                    policy,
                    selected,
                    inputs,
                    expectedCharacterCount,
                    pinCapsLock,
                    trustedClientProcessId,
                    started,
                    timeout,
                    stage => currentStage = stage,
                    cancellationToken).ConfigureAwait(false);
                return Complete(outcome.Status, outcome.Stage);
            }
            finally
            {
                Array.Clear(inputs, 0, inputs.Length);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                ClientCertificateNativePromptStatus.Cancelled,
                currentStage);
        }
        catch
        {
            return Complete(
                ClientCertificateNativePromptStatus.UnknownFailure,
                NativePromptExecutionStage.UnexpectedFailure);
        }
        finally
        {
            ownedPin.Dispose();
        }
    }

    private async ValueTask<ClientCertificateNativePromptStatus?> WaitForForegroundAsync(
        IntPtr targetWindow,
        long started,
        TimeSpan timeout,
        long foregroundStarted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_native.GetForegroundWindow() == targetWindow)
            {
                return null;
            }

            var elapsed = _clock.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                return ClientCertificateNativePromptStatus.TimedOut;
            }

            var foregroundElapsed = _clock.GetElapsedTime(foregroundStarted);
            if (foregroundElapsed >= ForegroundActivationTimeout)
            {
                return ClientCertificateNativePromptStatus.FocusLost;
            }

            var remaining = timeout - elapsed;
            var foregroundRemaining = ForegroundActivationTimeout - foregroundElapsed;
            var delay = remaining < foregroundRemaining ? remaining : foregroundRemaining;
            await _clock.DelayAsync(
                delay < WindowPollInterval ? delay : WindowPollInterval,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<NativePromptExecutionOutcome> ValidateSendAndSubmitOnceAsync(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot selected,
        NativePromptInput[] inputs,
        int expectedCharacterCount,
        bool pinCapsLock,
        int trustedClientProcessId,
        long started,
        TimeSpan timeout,
        Action<NativePromptExecutionStage> setCurrentStage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setCurrentStage);
        setCurrentStage(NativePromptExecutionStage.InputBuild);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.InputBuild);
        }

        setCurrentStage(NativePromptExecutionStage.IdentityRecapture);
        var fresh = _native.CaptureWindowSnapshot(
            selected.WindowHandle,
            policy,
            trustedClientProcessId);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.IdentityRecapture);
        }

        if (fresh is null ||
            !NativePromptWindowMatcher.IsMatch(policy, fresh, trustedClientProcessId) ||
            !NativePromptWindowMatcher.HasSameIdentity(policy, selected, fresh))
        {
            return new(
                ClientCertificateNativePromptStatus.TargetMismatch,
                NativePromptExecutionStage.IdentityRecapture);
        }

        setCurrentStage(NativePromptExecutionStage.FocusValidation);
        if (_native.GetForegroundWindow() != selected.ForegroundWindowHandle ||
            !_native.IsInputControlFocused(selected))
        {
            return new(
                ClientCertificateNativePromptStatus.FocusLost,
                NativePromptExecutionStage.FocusValidation);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.FocusValidation);
        }

        setCurrentStage(NativePromptExecutionStage.InputStateValidation);
        var verifyInputProcessing = fresh.InputControlHandle != IntPtr.Zero;
        if (verifyInputProcessing)
        {
            if (!_native.TryGetInputCharacterCount(fresh, out var initialCharacterCount) ||
                initialCharacterCount != 0)
            {
                return new(
                    ClientCertificateNativePromptStatus.TargetMismatch,
                    NativePromptExecutionStage.InputStateValidation);
            }

            _inputDiagnostics.RecordCharacterCount(fresh, trustedClientProcessId, "before-input", initialCharacterCount);
        }

        setCurrentStage(NativePromptExecutionStage.InputMethodPreparation);
        if (!_native.IsInputMethodReady(fresh))
        {
            return new(ClientCertificateNativePromptStatus.InputFailed,
                NativePromptExecutionStage.InputMethodPreparation);
        }

        setCurrentStage(NativePromptExecutionStage.InputSend);
        if (_native.GetForegroundWindow() != fresh.ForegroundWindowHandle ||
            !_native.IsInputControlFocused(fresh))
        {
            return new(
                ClientCertificateNativePromptStatus.FocusLost,
                NativePromptExecutionStage.InputSend);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.InputSend);
        }

        bool TargetStillMatches(bool requireInputFocus)
        {
            var current = _native.CaptureWindowSnapshot(fresh.WindowHandle, policy, trustedClientProcessId);
            return current is not null &&
                NativePromptWindowMatcher.IsMatch(policy, current, trustedClientProcessId) &&
                NativePromptWindowMatcher.HasSameIdentity(policy, fresh, current) &&
                _native.GetForegroundWindow() == fresh.ForegroundWindowHandle &&
                (!requireInputFocus || _native.IsInputControlFocused(current));
        }

        bool? originalCapsLock = null;
        var capsRestored = true;
        try
        {
            if (pinCapsLock && !_capsLockApi.TrySet(fresh.InputControlHandle, true,
                    () => !cancellationToken.IsCancellationRequested && !HasTimedOut(started, timeout) &&
                        TargetStillMatches(requireInputFocus: true), out originalCapsLock))
                return new(ClientCertificateNativePromptStatus.InputFailed, NativePromptExecutionStage.InputSend);

            cancellationToken.ThrowIfCancellationRequested();
            if (HasTimedOut(started, timeout))
                return new(ClientCertificateNativePromptStatus.TimedOut, NativePromptExecutionStage.InputSend);
            if (pinCapsLock && !TargetStillMatches(requireInputFocus: true))
                return new(ClientCertificateNativePromptStatus.FocusLost, NativePromptExecutionStage.InputSend);
            if (pinCapsLock && !_capsLockApi.AreModifiersReleased())
                return new(ClientCertificateNativePromptStatus.InputFailed, NativePromptExecutionStage.InputSend);

            // Retain the proven single batch. No thread attachment spans character
            // input or its count acknowledgement in the vendor secure control.
            if (_native.SendInput(inputs) != checked((uint)inputs.Length))
                return new(ClientCertificateNativePromptStatus.InputFailed, NativePromptExecutionStage.InputSend);

            if (verifyInputProcessing)
            {
                setCurrentStage(NativePromptExecutionStage.InputProcessingWait);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasTimedOut(started, timeout))
                        return new(ClientCertificateNativePromptStatus.TimedOut, NativePromptExecutionStage.InputProcessingWait);
                    var current = _native.CaptureWindowSnapshot(fresh.WindowHandle, policy, trustedClientProcessId);
                    if (current is null || !NativePromptWindowMatcher.IsMatch(policy, current, trustedClientProcessId) ||
                        !NativePromptWindowMatcher.HasSameIdentity(policy, fresh, current))
                        return new(ClientCertificateNativePromptStatus.TargetMismatch, NativePromptExecutionStage.InputProcessingWait);
                    if (_native.GetForegroundWindow() != fresh.ForegroundWindowHandle)
                        return new(ClientCertificateNativePromptStatus.FocusLost, NativePromptExecutionStage.InputProcessingWait);
                    if (!_native.TryGetInputCharacterCount(current, out var count) || count > expectedCharacterCount)
                        return new(ClientCertificateNativePromptStatus.TargetMismatch, NativePromptExecutionStage.InputProcessingWait);
                    if (count == expectedCharacterCount) break;
                    var remaining = timeout - _clock.GetElapsedTime(started);
                    await _clock.DelayAsync(remaining < WindowPollInterval ? remaining : WindowPollInterval,
                        cancellationToken).ConfigureAwait(false);
                }
                _inputDiagnostics.RecordCharacterCount(fresh, trustedClientProcessId, "after-input", expectedCharacterCount);
            }
        }
        finally
        {
            // Restore only after all characters were consumed (or after aborting).
            // Cleanup must still run after timeout/cancellation, but never against
            // a replaced window or another foreground application.
            if (originalCapsLock is { } original)
            {
                try
                {
                    capsRestored = _capsLockApi.TrySet(fresh.InputControlHandle, original,
                        () => TargetStillMatches(requireInputFocus: false), out _);
                }
                catch { capsRestored = false; }
            }
        }
        if (!capsRestored)
            return new(ClientCertificateNativePromptStatus.InputFailed, NativePromptExecutionStage.InputProcessingWait);
        setCurrentStage(NativePromptExecutionStage.SubmitWait);
        while (!_native.IsSubmitControlReady(fresh))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = _clock.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                return new(
                    ClientCertificateNativePromptStatus.TimedOut,
                    NativePromptExecutionStage.SubmitWait);
            }

            var remaining = timeout - elapsed;
            await _clock.DelayAsync(
                remaining < WindowPollInterval ? remaining : WindowPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        setCurrentStage(NativePromptExecutionStage.SubmitIdentityRecapture);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.SubmitWait);
        }

        var submitFresh = _native.CaptureWindowSnapshot(
            selected.WindowHandle,
            policy,
            trustedClientProcessId);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.SubmitIdentityRecapture);
        }

        if (submitFresh is null ||
            !NativePromptWindowMatcher.IsMatch(
                policy,
                submitFresh,
                trustedClientProcessId) ||
            !NativePromptWindowMatcher.HasSameIdentity(policy, selected, submitFresh))
        {
            return new(
                ClientCertificateNativePromptStatus.TargetMismatch,
                NativePromptExecutionStage.SubmitIdentityRecapture);
        }

        // IME readiness authorizes typing, not button activation. Secure controls
        // may change focus or IME state after processing the complete input. Do
        // not end authentication here solely because that input state changed.
        // The matched dialog, foreground, button and deadline are still checked.
        setCurrentStage(NativePromptExecutionStage.SubmitActivation);
        if (_native.GetForegroundWindow() != submitFresh.ForegroundWindowHandle)
        {
            return new(
                ClientCertificateNativePromptStatus.FocusLost,
                NativePromptExecutionStage.SubmitIdentityRecapture);
        }

        if (!_native.IsSubmitControlReady(submitFresh))
        {
            return new(
                ClientCertificateNativePromptStatus.TargetMismatch,
                NativePromptExecutionStage.SubmitIdentityRecapture);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (HasTimedOut(started, timeout))
        {
            return new(
                ClientCertificateNativePromptStatus.TimedOut,
                NativePromptExecutionStage.SubmitActivation);
        }

        return _native.ActivateSubmitControl(submitFresh)
            ? new(
                ClientCertificateNativePromptStatus.Success,
                NativePromptExecutionStage.Completed)
            : new(
                ClientCertificateNativePromptStatus.InputFailed,
                NativePromptExecutionStage.SubmitActivation);
    }

    private bool HasTimedOut(long started, TimeSpan timeout) =>
        _clock.GetElapsedTime(started) >= timeout;

    private static bool IsUppercaseAlphanumeric(ClearableSecretBuffer secret)
    {
        if (secret.ByteLength <= 0 || secret.ByteLength % sizeof(char) != 0 ||
            secret.ByteLength / sizeof(char) > NativePromptProtocol.MaxPinCharacters) return false;
        var hasUppercase = false;
        for (var offset = 0; offset < secret.ByteLength; offset += sizeof(char))
        {
            var character = (char)Marshal.ReadInt16(secret.Pointer, offset);
            if (character is >= 'A' and <= 'Z') hasUppercase = true;
            else if (character is not (>= '0' and <= '9')) return false;
        }
        return hasUppercase;
    }
    private void TryClearDiagnostics()
    {
        try
        {
            _diagnostics.Clear();
        }
        catch
        {
        }
    }

    private void TryRecordDiagnostics(NativePromptExecutionObservation observation)
    {
        try
        {
            _diagnostics.Record(observation);
        }
        catch
        {
        }
    }

    private readonly record struct NativePromptExecutionOutcome(
        ClientCertificateNativePromptStatus Status,
        NativePromptExecutionStage Stage);
}
