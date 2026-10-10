using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptExecutorTests
{
    [Fact]
    public void InputBatchBuilder_PinnedCapsOnIgnoresBrokerCapsOffAndEmitsUppercaseWithoutShift()
    {
        var keyboard = new SyntheticPinnedKeyboardApi();
        var builder = new NativePromptInputBatchBuilder(new WindowsNativePromptVirtualKeyMapper(keyboard));
        using var secret = new ClearableSecretBuffer("ABC123".AsSpan());
        var inputs = builder.BuildWithCapsLock(secret, new IntPtr(321), true);
        try
        {
            Assert.NotNull(inputs);
            Assert.Equal(12, inputs.Length);
            Assert.DoesNotContain(inputs, input => input.Data.Keyboard.VirtualKey == NativePromptInput.ShiftVirtualKey);
            Assert.Equal(new ushort[] { 'A', 'B', 'C', '1', '2', '3' },
                inputs.Where(input => input.Data.Keyboard.Flags == 0).Select(input => input.Data.Keyboard.VirtualKey));
            Assert.Equal(0, keyboard.CapsReads);
        }
        finally { if (inputs is not null) Array.Clear(inputs); }
    }

    [Fact]
    public void InputBatchBuilder_PinnedCapsOnWithDigitRequiringShift_RejectsUnsupportedLayout()
    {
        var keyboard = new SyntheticPinnedKeyboardApi { DigitsRequireShift = true };
        var builder = new NativePromptInputBatchBuilder(new WindowsNativePromptVirtualKeyMapper(keyboard));
        using var secret = new ClearableSecretBuffer("ABC123".AsSpan());
        Assert.Null(builder.BuildWithCapsLock(secret, new IntPtr(321), true));
        Assert.Equal(0, keyboard.CapsReads);
    }

    private sealed class SyntheticPinnedKeyboardApi : IWindowsNativePromptKeyboardApi
    {
        public bool DigitsRequireShift { get; init; }
        public int CapsReads { get; private set; }
        public uint GetWindowThreadId(IntPtr window) => 17;
        public IntPtr GetKeyboardLayout(uint thread) => new(0x08040804);
        public short VkKeyScan(char character, IntPtr layout) =>
            (short)(char.ToUpperInvariant(character) | ((char.IsAsciiLetterUpper(character) || DigitsRequireShift) ? 0x0100 : 0));
        public short GetKeyState(int virtualKey) { CapsReads++; return 0; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UppercasePin_PinsCapsBeforeSingleBatchAndRestoresAfterCountBeforeSubmit(bool initiallyOn)
    {
        var native = TypingNative();
        var caps = new RecordingCapsLockApi(native) { CurrentState = initiallyOn };
        var builder = new CapsAwareInputBuilder(native);
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: builder, capsLockApi: caps);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(new[] { true, initiallyOn }, caps.DesiredStates);
        Assert.Equal(initiallyOn, caps.CurrentState);
        Assert.Equal(1, builder.PinnedBuildCalls);
        Assert.Equal(0, builder.NormalBuildCalls);
        Assert.True(builder.SourceExactlyPreserved);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(12, native.ObservedInputCount);
        Assert.Equal(1, native.SubmitActivationCount);
        Assert.True(native.Operations.IndexOf("caps-True") < native.Operations.IndexOf("send-input"));
        Assert.True(native.Operations.LastIndexOf("read-input-count") < native.Operations.LastIndexOf($"caps-{initiallyOn}"));
        Assert.True(native.Operations.LastIndexOf($"caps-{initiallyOn}") < native.Operations.IndexOf("activate-submit"));
        native.AssertInputArrayCleared();
        result.AssertSecretCleared();
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("AbC123")]
    [InlineData("123456")]
    public async Task ExecuteAsync_OtherPinPreservesCaseAndUsesOrdinaryBatchWithoutCapsHelper(string value)
    {
        var native = TypingNative();
        var caps = new RecordingCapsLockApi(native);
        var builder = new CapsAwareInputBuilder(native) { ExpectedSource = value };
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: value.ToCharArray(), inputBatchBuilder: builder, capsLockApi: caps);
        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Empty(caps.DesiredStates);
        Assert.Equal(1, builder.NormalBuildCalls);
        Assert.Equal(0, builder.PinnedBuildCalls);
        Assert.True(builder.SourceExactlyPreserved);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(1, native.SubmitActivationCount);
    }

    [Fact]
    public async Task ExecuteAsync_UppercaseCapsPreparationFails_DoesNotInputOrSubmit()
    {
        var native = TypingNative();
        var caps = new RecordingCapsLockApi(native) { FailPreparation = true };
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: new CapsAwareInputBuilder(native), capsLockApi: caps);
        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, result.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UppercaseInputFailure_RestoresCapsAndDoesNotSubmit(bool throws)
    {
        var native = TypingNative() with { SendInputResult = 0, ThrowOnSendInput = throws };
        var caps = new RecordingCapsLockApi(native);
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: new CapsAwareInputBuilder(native), capsLockApi: caps);
        Assert.NotEqual(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(new[] { true, false }, caps.DesiredStates);
        Assert.False(caps.CurrentState);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_UppercaseFocusLostBeforeCapsPreparation_DoesNotToggleOrInput()
    {
        var native = TypingNative();
        native.AfterInputMethodCheck = () => native.InputIsFocused = false;
        var caps = new RecordingCapsLockApi(native);
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: new CapsAwareInputBuilder(native), capsLockApi: caps);
        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, result.Result.Status);
        Assert.Empty(caps.DesiredStates);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
    }

    [Fact]
    public async Task ExecuteAsync_UppercaseCharacterCountTimeout_RestoresCapsWithoutSubmit()
    {
        var native = TypingNative() with { AutoProcessInput = false };
        var caps = new RecordingCapsLockApi(native);
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(timeoutMilliseconds: 30),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: new CapsAwareInputBuilder(native), capsLockApi: caps);
        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, result.Result.Status);
        Assert.Equal(new[] { true, false }, caps.DesiredStates);
        Assert.False(caps.CurrentState);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
    }

    [Fact]
    public async Task ExecuteAsync_UppercaseBuilderDoesNotImplementPinnedMode_FailsClosedWithoutNormalBuildOrInput()
    {
        var native = TypingNative();
        var caps = new RecordingCapsLockApi(native);
        var normalBuildCalls = 0;
        var builder = new CallbackInputBatchBuilder(() => normalBuildCalls++);
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: builder, capsLockApi: caps);
        Assert.NotEqual(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(0, normalBuildCalls);
        Assert.Empty(caps.DesiredStates);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        result.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_PhysicalModifierHeldAfterCapsPreparation_DoesNotSendAndAttemptsRestore()
    {
        var native = TypingNative();
        var caps = new RecordingCapsLockApi(native) { ModifiersReleased = false };
        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(),
            secret: ['A', 'B', 'C', '1', '2', '3'], inputBatchBuilder: new CapsAwareInputBuilder(native), capsLockApi: caps);
        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, result.Result.Status);
        Assert.Equal(new[] { true, false }, caps.DesiredStates);
        Assert.True(native.Operations.IndexOf("caps-True") < native.Operations.IndexOf("check-modifiers"));
        Assert.True(native.Operations.IndexOf("check-modifiers") < native.Operations.IndexOf("caps-False"));
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        result.AssertSecretCleared();
    }

    private static RecordingNativePromptApi TypingNative()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        return new() { Snapshots = [target], FreshSnapshot = target, FallbackForegroundHandle = target.WindowHandle };
    }

    private sealed class RecordingCapsLockApi(RecordingNativePromptApi native) : INativePromptCapsLockApi
    {
        public bool CurrentState { get; set; }
        public bool FailPreparation { get; init; }
        public bool ModifiersReleased { get; init; } = true;
        public List<bool> DesiredStates { get; } = [];
        public bool AreModifiersReleased()
        {
            native.Operations.Add("check-modifiers");
            return ModifiersReleased;
        }
        public bool TrySet(IntPtr target, bool desired, Func<bool> canContinue, out bool? previousState)
        {
            previousState = CurrentState;
            DesiredStates.Add(desired);
            native.Operations.Add($"caps-{desired}");
            if (!canContinue() || (FailPreparation && DesiredStates.Count == 1)) return false;
            CurrentState = desired;
            return true;
        }
    }

    private sealed class CapsAwareInputBuilder(RecordingNativePromptApi native) : INativePromptInputBatchBuilder
    {
        public string ExpectedSource { get; init; } = "ABC123";
        public bool SourceExactlyPreserved { get; private set; } = true;
        public int NormalBuildCalls { get; private set; }
        public int PinnedBuildCalls { get; private set; }
        public NativePromptInput[]? Build(ClearableSecretBuffer secret, IntPtr target)
        {
            NormalBuildCalls++;
            return BuildCore(secret, pinned: false);
        }
        public NativePromptInput[]? BuildWithCapsLock(ClearableSecretBuffer secret, IntPtr target, bool capsLock)
        {
            Assert.True(capsLock);
            PinnedBuildCalls++;
            return BuildCore(secret, pinned: true);
        }
        private NativePromptInput[] BuildCore(ClearableSecretBuffer secret, bool pinned)
        {
            native.Operations.Add(pinned ? "build-pinned" : "build-normal");
            int length = secret.ByteLength / sizeof(char);
            SourceExactlyPreserved &= length == ExpectedSource.Length;
            var inputs = new List<NativePromptInput>();
            for (int i = 0; i < length; i++)
            {
                char c = (char)Marshal.ReadInt16(secret.Pointer, i * sizeof(char));
                SourceExactlyPreserved &= i < ExpectedSource.Length && c == ExpectedSource[i];
                bool shift = !pinned && char.IsAsciiLetterUpper(c);
                if (shift) inputs.Add(NativePromptInput.VirtualKey(0x10, false));
                inputs.Add(NativePromptInput.VirtualKey(char.ToUpperInvariant(c), false));
                inputs.Add(NativePromptInput.VirtualKey(char.ToUpperInvariant(c), true));
                if (shift) inputs.Add(NativePromptInput.VirtualKey(0x10, true));
            }
            return inputs.ToArray();
        }
    }

    [Fact]
    public async Task ExecuteAsync_InputMethodCannotBeClosed_DoesNotTypeOrSubmit()
    {
        var native = ValidNative() with { PrepareInputMethodResult = false };
        var result = await ExecuteAsync(native);
        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, result.Result.Status);
        Assert.Equal(1, native.PrepareInputMethodCalls);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        result.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputMethodReopensBeforeSend_DoesNotTypeOrSubmit()
    {
        var native = ValidNative() with { InputMethodReady = false };
        var result = await ExecuteAsync(native);
        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, result.Result.Status);
        Assert.Equal(1, native.PrepareInputMethodCalls);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
    }

    [Fact]
    public async Task ExecuteAsync_InputMethodStateChangesAfterTyping_StillSubmitsOnce()
    {
        var native = ValidNative();
        native.InputMethodReadyResults.Enqueue(true);
        native.InputMethodReadyResults.Enqueue(false);
        var result = await ExecuteAsync(native);
        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(1, native.SubmitActivationCount);
        Assert.Equal(1, native.PrepareInputMethodCalls);
        Assert.Equal(1, native.Operations.Count(operation => operation == "check-ime"));
        result.AssertSecretCleared();
    }

    [Theory]
    [InlineData("focus", ClientCertificateNativePromptStatus.FocusLost)]
    [InlineData("timeout", ClientCertificateNativePromptStatus.TimedOut)]
    [InlineData("cancel", ClientCertificateNativePromptStatus.Cancelled)]
    public async Task ExecuteAsync_StateChangesDuringInputMethodCheck_DoesNotTypeOrSubmit(
        string change, ClientCertificateNativePromptStatus expected)
    {
        var native = ValidNative();
        var clock = new RecordingExecutionClock();
        using var cancellation = new CancellationTokenSource();
        native.AfterInputMethodCheck = () =>
        {
            if (change == "focus") native.InputIsFocused = false;
            if (change == "timeout") clock.Advance(TimeSpan.FromSeconds(60));
            if (change == "cancel") cancellation.Cancel();
        };
        var result = await ExecuteAsync(native, clock: clock, cancellationToken: cancellation.Token);
        Assert.Equal(expected, result.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
    }

    [Fact]
    public async Task ExecuteAsync_ClosesInputMethodBeforeBuildingAndRechecksBeforeTyping()
    {
        var native = ValidNative();
        var builder = new CallbackInputBatchBuilder(() => native.Operations.Add("build"));
        var result = await ExecuteAsync(native, inputBatchBuilder: builder);
        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(1, native.PrepareInputMethodCalls);
        Assert.True(native.Operations.IndexOf("prepare-ime") < native.Operations.IndexOf("build"));
        Assert.True(native.Operations.IndexOf("check-ime") > native.Operations.IndexOf("build"));
        Assert.True(native.Operations.IndexOf("check-ime") < native.Operations.IndexOf("send-input"));
        Assert.Equal(1, native.SendInputCallCount);
    }

    [Fact]
    public void VirtualKeyMapper_TargetLayoutDiffersFromBroker_UsesTargetThreadLayout()
    {
        var native = new RecordingKeyboardApi();
        var mapper = new WindowsNativePromptVirtualKeyMapper(native);

        Assert.True(mapper.TryMap('A', new IntPtr(321), out var key));

        Assert.Equal(new IntPtr(321), native.ObservedWindow);
        Assert.Equal(73u, native.ObservedThread);
        Assert.Equal(new IntPtr(0x040C), native.ObservedLayout);
        Assert.Equal((ushort)0x51, key.VirtualKey);
        Assert.True(key.RequiresShift);
    }

    [Theory]
    [InlineData(0, 0x040C)]
    [InlineData(73, 0)]
    public void VirtualKeyMapper_TargetLayoutUnavailable_RejectsWithoutCurrentThreadFallback(
        uint thread, long layout)
    {
        var native = new RecordingKeyboardApi { TargetThread = thread, TargetLayout = new IntPtr(layout) };
        var mapper = new WindowsNativePromptVirtualKeyMapper(native);

        Assert.False(mapper.TryMap('A', new IntPtr(321), out _));
        Assert.Equal(0, native.ScanCount);
    }

    [Fact]
    public async Task ExecuteAsync_UsesMatchedInputControlForKeyboardMapping()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        var native = new RecordingNativePromptApi { Snapshots = [target], FreshSnapshot = target, FallbackForegroundHandle = target.WindowHandle };
        var keyboard = new RecordingKeyboardApi();
        var builder = new NativePromptInputBatchBuilder(new WindowsNativePromptVirtualKeyMapper(keyboard));

        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(timeoutMilliseconds: 50), secret: ['a'], inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(target.InputControlHandle, keyboard.ObservedWindow);
    }

    [Fact]
    public async Task ExecuteAsync_TopLevelWithoutInputHandle_UsesMatchedWindowForKeyboardMapping()
    {
        var native = ValidNative();
        var keyboard = new RecordingKeyboardApi();
        var builder = new NativePromptInputBatchBuilder(new WindowsNativePromptVirtualKeyMapper(keyboard));

        var result = await ExecuteAsync(native, secret: ['a'], inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(native.FreshSnapshot!.WindowHandle, keyboard.ObservedWindow);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsInputEnvironmentAndProcessedCharacterCounts()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        var native = new RecordingNativePromptApi { Snapshots = [target], FreshSnapshot = target, FallbackForegroundHandle = target.WindowHandle };
        var diagnostics = new RecordingInputDiagnostics();

        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(timeoutMilliseconds: 50), secret: ['a'], inputDiagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, result.Result.Status);
        Assert.Equal(new[] { "environment", "environment", "before-input:0", "after-input:1" }, diagnostics.Events);
        Assert.All(diagnostics.Targets, window => Assert.Equal(target.InputControlHandle, window));
    }

    private sealed class RecordingInputDiagnostics : INativePromptInputDiagnostics
    {
        public List<string> Events { get; } = [];
        public List<IntPtr> Targets { get; } = [];
        public Action? AfterBeforeInput { get; init; }
        public Action? AfterAfterInput { get; init; }
        public bool ThrowOnFailure { get; init; }
        public Action? AfterFailure { get; init; }
        public List<(NativePromptExecutionStage Stage, int Sent, int Confirmed)> Failures { get; } = [];
        public List<string> FailureReasons { get; } = [];
        public void RecordInputFailure(NativePromptWindowSnapshot snapshot, int trustedClientProcessId,
            NativePromptExecutionStage stage, string failureReason, int sentCharacterCount, int confirmedCharacterCount)
        {
            Failures.Add((stage, sentCharacterCount, confirmedCharacterCount));
            FailureReasons.Add(failureReason);
            AfterFailure?.Invoke();
            if (ThrowOnFailure) throw new IOException("Synthetic diagnostics failure.");
        }
        public void RecordEnvironment(NativePromptWindowSnapshot snapshot, int trustedClientProcessId)
        {
            Events.Add("environment"); Targets.Add(snapshot.InputControlHandle);
        }
        public void RecordCharacterCount(NativePromptWindowSnapshot snapshot, int trustedClientProcessId, string phase, int count)
        {
            Events.Add($"{phase}:{count}"); Targets.Add(snapshot.InputControlHandle);
            if (phase == "before-input") AfterBeforeInput?.Invoke();
            if (phase == "after-input") AfterAfterInput?.Invoke();
        }
    }

    [Theory]
    [InlineData("input-focus", ClientCertificateNativePromptStatus.FocusLost)]
    [InlineData("foreground", ClientCertificateNativePromptStatus.FocusLost)]
    [InlineData("timeout", ClientCertificateNativePromptStatus.TimedOut)]
    [InlineData("cancel", ClientCertificateNativePromptStatus.Cancelled)]
    public async Task ExecuteAsync_StateChangesDuringBeforeInputDiagnostics_DoesNotSendOrSubmit(
        string change, ClientCertificateNativePromptStatus expected)
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        var native = new RecordingNativePromptApi { Snapshots = [target], FreshSnapshot = target, FallbackForegroundHandle = target.WindowHandle };
        var clock = new RecordingExecutionClock();
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new RecordingInputDiagnostics
        {
            AfterBeforeInput = () =>
            {
                switch (change)
                {
                    case "input-focus": native.InputIsFocused = false; break;
                    case "foreground": native.FallbackForegroundHandle = new IntPtr(0x9999); break;
                    case "timeout": clock.Advance(TimeSpan.FromMilliseconds(50)); break;
                    case "cancel": cancellation.Cancel(); break;
                }
            }
        };

        var result = await ExecuteAsync(native, NativePromptWindowMatcherTests.DescendantPolicy(timeoutMilliseconds: 50), clock, cancellation.Token, secret: ['a'], inputDiagnostics: diagnostics);

        Assert.Equal(expected, result.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        result.AssertSecretCleared();
    }

    private sealed class RecordingKeyboardApi : IWindowsNativePromptKeyboardApi
    {
        public uint TargetThread { get; init; } = 73;
        public IntPtr TargetLayout { get; init; } = new(0x040C);
        public IntPtr ObservedWindow { get; private set; }
        public uint ObservedThread { get; private set; }
        public IntPtr ObservedLayout { get; private set; }
        public int ScanCount { get; private set; }
        public uint GetWindowThreadId(IntPtr window) { ObservedWindow = window; return TargetThread; }
        public IntPtr GetKeyboardLayout(uint thread) { ObservedThread = thread; return thread == TargetThread ? TargetLayout : new IntPtr(0x0409); }
        public short VkKeyScan(char character, IntPtr layout) { ScanCount++; ObservedLayout = layout; return layout == TargetLayout ? (short)0x0151 : (short)0x0141; }
        public short GetKeyState(int virtualKey) => 0;
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchingCandidate_FailsClosedWithoutActivationOrInput()
    {
        var native = new RecordingNativePromptApi
        {
            Snapshots = [NativePromptWindowMatcherTests.Candidate() with { IsVisible = false }]
        };
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(native, diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.SendInputCallCount);
        var diagnostic = Assert.Single(diagnostics.Observations);
        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, diagnostic.Status);
        Assert.Equal(NativePromptExecutionStage.WindowEnumeration, diagnostic.Stage);
        Assert.Equal(native.EnumerationCount, diagnostic.EnumerationCount);
        Assert.Equal(native.EnumerationCount, diagnostic.CandidateCount);
        Assert.Equal(0, diagnostic.MatchingCandidateCount);
        Assert.Equal(0, diagnostic.SelectedProcessId);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_PromptAppearsAfterArming_WaitsForUniqueMatchThenSendsOnce()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };
        native.SnapshotSequences.Enqueue([]);
        native.SnapshotSequences.Enqueue([target]);
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy(timeoutMilliseconds: 50),
            clock);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(2, native.EnumerationCount);
        Assert.Equal(1, clock.DelayCount);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_MultipleMatchingCandidates_FailsClosedWithoutActivationOrInput()
    {
        var first = NativePromptWindowMatcherTests.Candidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [first, first with { WindowHandle = new IntPtr(0x5678), ProcessId = 201 }]
        };

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_ActivationFails_SendsNoInput()
    {
        var native = ValidNative() with
        {
            SetForegroundResult = false,
            FallbackForegroundHandle = new IntPtr(0x9999)
        };
        var clock = new RecordingExecutionClock();
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.Policy(timeoutMilliseconds: 2_000),
            clock,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, observation.Result.Status);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Elapsed);
        Assert.Equal(0, native.SendInputCallCount);
        var diagnostic = Assert.Single(diagnostics.Observations);
        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, diagnostic.Status);
        Assert.Equal(NativePromptExecutionStage.ForegroundWait, diagnostic.Stage);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TargetAlreadyForeground_DoesNotRequireActivationResult()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative() with { SetForegroundResult = false };

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(target.WindowHandle, native.ActivatedWindowHandle);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_ActivationReportsFailureButTargetBecomesForeground_SendsOnce()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative() with
        {
            SetForegroundResult = false,
            FallbackForegroundHandle = target.WindowHandle
        };
        native.ForegroundHandles.Enqueue(new IntPtr(0x9999));
        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TrustedClientDescendant_ActivatesRootAndFocusesExactInputBeforeSingleBatch()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.RootWindowHandle
        };

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(target.RootWindowHandle, native.ActivatedWindowHandle);
        Assert.Equal(target.InputControlHandle, native.FocusedInputHandle);
        Assert.Equal(NativePromptWindowMatcherTests.TrustedClientProcessId, native.ObservedTrustedClientProcessId);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TrustedClientOwnedTopLevelDialog_ActivatesDialogBeforeSingleBatch()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate() with { IsTopLevel = true };
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(target.WindowHandle, native.ActivatedWindowHandle);
        Assert.Equal(target.InputControlHandle, native.FocusedInputHandle);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TrustedClientDescendantCannotFocusExactInput_SendsNoInput()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.RootWindowHandle,
            FocusInputResult = false
        };

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TrustedClientDescendantLosesInputFocusBeforeBatch_SendsNoInput()
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.RootWindowHandle
        };
        var builder = new CallbackInputBatchBuilder(() => native.InputIsFocused = false);

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy(),
            inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
        builder.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_ForegroundEventuallyBecomesTarget_WaitsThenSendsOnce()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative();
        native.ForegroundHandles.Enqueue(new IntPtr(0x9999));
        native.ForegroundHandles.Enqueue(target.WindowHandle);
        native.ForegroundHandles.Enqueue(target.WindowHandle);
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(native, clock: clock);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, clock.DelayCount);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TotalTimeoutExpiresBeforeActivationCap_TimesOutWithoutInput()
    {
        var native = ValidNative();
        native.FallbackForegroundHandle = new IntPtr(0x9999);
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.Policy(timeoutMilliseconds: 20),
            clock: clock);

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, observation.Result.Status);
        Assert.InRange(clock.DelayCount, 1, 3);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_ActivationDelayPreservesRemainingAuthenticationTimeout()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative();
        native.SubmitReady = false;
        for (var index = 0; index < 40; index++)
        {
            native.ForegroundHandles.Enqueue(new IntPtr(0x9999));
        }

        native.ForegroundHandles.Enqueue(target.WindowHandle);
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.Policy(timeoutMilliseconds: 600),
            clock);

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, observation.Result.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(600), clock.Elapsed);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_FocusChangesImmediatelyBeforeInput_SendsNoInput()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative();
        native.ForegroundHandles.Enqueue(target.WindowHandle);
        native.ForegroundHandles.Enqueue(new IntPtr(0x9999));

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Theory]
    [MemberData(nameof(NativePromptWindowMatcherTests.IdentityDifferences), MemberType = typeof(NativePromptWindowMatcherTests))]
    public async Task ExecuteAsync_AnyFreshIdentityDifference_SendsNoInput(string difference)
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = ValidNative();
        native.ForegroundHandles.Enqueue(target.WindowHandle);
        native.FreshSnapshot = NativePromptWindowMatcherTests.Mutate(target, difference);

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedPromptTitleChangesAfterFocus_SendsOnce()
    {
        var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy();
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate() with
        {
            WindowTitle = policy.WindowTitle!
        };
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate(),
            FallbackForegroundHandle = target.WindowHandle
        };

        var observation = await ExecuteAsync(native, policy);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeEnumeration_ReturnsCancelledAndClearsSecret()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var native = ValidNative();
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            cancellationToken: cancellation.Token,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, observation.Result.Status);
        Assert.Equal(0, native.EnumerationCount);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(
            NativePromptExecutionStage.PolicyValidation,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForForeground_ReturnsCancelledWithoutInput()
    {
        using var cancellation = new CancellationTokenSource();
        var native = ValidNative();
        native.FallbackForegroundHandle = new IntPtr(0x9999);
        var clock = new RecordingExecutionClock { AfterDelay = cancellation.Cancel };
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            clock: clock,
            cancellationToken: cancellation.Token,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(
            NativePromptExecutionStage.ForegroundWait,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_FocusChangesAfterBatchConstruction_SendsNoInputAndClearsBatch()
    {
        var native = ValidNative();
        var builder = new CallbackInputBatchBuilder(
            () => native.FallbackForegroundHandle = new IntPtr(0x9999));

        var observation = await ExecuteAsync(native, inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
        builder.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_CancellationAfterBatchConstruction_SendsNoInputAndClearsBatch()
    {
        using var cancellation = new CancellationTokenSource();
        var native = ValidNative();
        var builder = new CallbackInputBatchBuilder(cancellation.Cancel);
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            inputBatchBuilder: builder,
            cancellationToken: cancellation.Token,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(
            NativePromptExecutionStage.InputBuild,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
        builder.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutAfterBatchConstruction_SendsNoInputAndClearsBatch()
    {
        var native = ValidNative();
        var clock = new RecordingExecutionClock();
        var builder = new CallbackInputBatchBuilder(
            () => clock.Advance(TimeSpan.FromMilliseconds(50)));

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.Policy(timeoutMilliseconds: 50),
            clock,
            inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
        builder.AssertInputArrayCleared();
    }

    [Fact]
    public void InputBatchBuilder_AlphanumericSecret_UsesVirtualKeysAndShiftInsteadOfUnicodePackets()
    {
        var characters = new[] { 'a', 'B', '7', '@' };
        using var secret = new ClearableSecretBuffer(characters);
        var builder = new NativePromptInputBatchBuilder(new FixedVirtualKeyMapper(
            new NativePromptVirtualKey(0x41, RequiresShift: false),
            new NativePromptVirtualKey(0x42, RequiresShift: true),
            new NativePromptVirtualKey(0x37, RequiresShift: false),
            new NativePromptVirtualKey(0x32, RequiresShift: true)));

        var inputs = Assert.IsType<NativePromptInput[]>(builder.Build(secret, new IntPtr(321)));

        Assert.Equal(12, inputs.Length);
        Assert.DoesNotContain(inputs, input =>
            (input.Data.Keyboard.Flags & NativePromptInput.UnicodeKeyFlag) != 0);
        Assert.DoesNotContain(inputs, input =>
            input.Data.Keyboard.VirtualKey == 0x000D);
        Assert.Equal(NativePromptInput.ShiftVirtualKey, inputs[2].Data.Keyboard.VirtualKey);
        Assert.Equal(NativePromptInput.ShiftVirtualKey, inputs[5].Data.Keyboard.VirtualKey);
        Assert.Equal(NativePromptInput.ShiftVirtualKey, inputs[8].Data.Keyboard.VirtualKey);
        Assert.Equal(NativePromptInput.ShiftVirtualKey, inputs[11].Data.Keyboard.VirtualKey);

        Array.Clear(inputs, 0, inputs.Length);
        Array.Clear(characters, 0, characters.Length);
    }

    [Theory]
    [InlineData('a', 0x0041, false, false)]
    [InlineData('B', 0x0142, false, true)]
    [InlineData('a', 0x0041, true, true)]
    [InlineData('B', 0x0142, true, false)]
    public void VirtualKeyMapper_UsesShiftAndCompensatesForCapsLock(
        char character,
        short mapping,
        bool capsLock,
        bool expectedShift)
    {
        var mapped = WindowsNativePromptVirtualKeyMapper.TryCreateMapping(
            character,
            mapping,
            capsLock,
            out var key);

        Assert.True(mapped);
        Assert.Equal((ushort)char.ToUpperInvariant(character), key.VirtualKey);
        Assert.Equal(expectedShift, key.RequiresShift);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x0241)]
    [InlineData(0x0441)]
    public void VirtualKeyMapper_RejectsUnmappableOrCtrlAltCombinations(short mapping)
    {
        Assert.False(WindowsNativePromptVirtualKeyMapper.TryCreateMapping(
            'A',
            mapping,
            capsLockActive: false,
            out _));
    }

    [Fact]
    public async Task ExecuteAsync_Success_SendsPinBatchThenActivatesSubmitOnce()
    {
        var secret = new[] { 'a', 'B', '7', '@' };
        var native = ValidNative();
        var builder = new NativePromptInputBatchBuilder(new FixedVirtualKeyMapper(
            new NativePromptVirtualKey(0x41, RequiresShift: false),
            new NativePromptVirtualKey(0x42, RequiresShift: true),
            new NativePromptVirtualKey(0x37, RequiresShift: false),
            new NativePromptVirtualKey(0x32, RequiresShift: true)));

        var observation = await ExecuteAsync(
            native,
            secret: secret,
            inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(12, native.ObservedInputCount);
        Assert.True(native.ObservedOnlyPinVirtualKeys);
        Assert.Equal(1, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_SubmitReadyBeforeFullInput_WaitsForExactCharacterCountThenClicksOnce()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };
        native.InputCharacterCountResults.Enqueue(new(true, 0));
        native.InputCharacterCountResults.Enqueue(new(true, 0));
        native.InputCharacterCountResults.Enqueue(new(true, 1));
        native.InputCharacterCountResults.Enqueue(new(true, 1));
        native.InputCharacterCountResults.Enqueue(new(true, 2));
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(),
            clock, secret: ['a', 'b']);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(5, native.InputCharacterCountReadCount);
        Assert.Equal(3, clock.DelayCount);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(1, native.SubmitActivationCount);
        Assert.True(
            native.Operations.LastIndexOf("read-input-count") <
            native.Operations.IndexOf("activate-submit"));
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputContainsPreExistingCharacters_FailsClosedBeforeSend()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle,
            CurrentInputCharacterCount = 1
        };
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(),
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        Assert.Equal(
            NativePromptExecutionStage.InputStateValidation,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputCharacterCountReadFailsBeforeSend_FailsClosed()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle,
            InputCharacterCountAvailable = false
        };

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputCharacterCountReadFailsAfterSend_DoesNotSubmit()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };
        native.InputCharacterCountResults.Enqueue(new(true, 0));
        native.InputCharacterCountResults.Enqueue(new(false, 0));

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputCharacterCountExceedsExpected_DoesNotSubmit()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };
        native.InputCharacterCountResults.Enqueue(new(true, 0));
        native.InputCharacterCountResults.Enqueue(new(true, 21));

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy());

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_InputProcessingNeverCompletes_TimesOutWithoutSubmit()
    {
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle,
            AutoProcessInput = false
        };
        var clock = new RecordingExecutionClock();
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(
                timeoutMilliseconds: 20),
            clock,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        Assert.Equal(
            NativePromptExecutionStage.InputProcessingWait,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForInputProcessing_DoesNotSubmit()
    {
        using var cancellation = new CancellationTokenSource();
        var target = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle,
            AutoProcessInput = false
        };
        var clock = new RecordingExecutionClock { AfterDelay = cancellation.Cancel };
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(),
            clock,
            cancellationToken: cancellation.Token,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        Assert.Equal(
            NativePromptExecutionStage.InputProcessingWait,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_SubmitInitiallyDisabled_WaitsThenActivatesOnce()
    {
        var native = ValidNative();
        native.SubmitReadyResults.Enqueue(false);
        native.SubmitReadyResults.Enqueue(true);
        var clock = new RecordingExecutionClock();

        var observation = await ExecuteAsync(native, clock: clock);

        Assert.Equal(ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(1, native.SubmitActivationCount);
        Assert.Equal(1, clock.DelayCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_SubmitNeverEnables_TimesOutWithoutActivationOrRetry()
    {
        var native = ValidNative();
        native.SubmitReady = false;
        var clock = new RecordingExecutionClock();
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.Policy(timeoutMilliseconds: 20),
            clock,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        Assert.Equal(NativePromptExecutionStage.SubmitWait, Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_IdentityChangesBeforeSubmit_FailsClosedAfterSingleInput(bool submitModeChanged)
    {
        var target = NativePromptWindowMatcherTests.DescendantCandidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.RootWindowHandle
        };
        var diagnostics = new RecordingInputDiagnostics
        {
            AfterAfterInput = () => native.FreshSnapshot = submitModeChanged
                ? target with { UsesDialogEnterSubmit = true }
                : target with { SubmitControlHandle = new IntPtr(0x7FFF) }
        };

        var observation = await ExecuteAsync(
            native,
            NativePromptWindowMatcherTests.DescendantPolicy(), inputDiagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.TargetMismatch, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DialogEnterModeMustRemainUnchangedBeforeSubmit(bool switchToButton)
    {
        var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy();
        var standard = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate();
        var target = standard with { WindowTitle = "网银盾验证密码", SubmitControlHandle = IntPtr.Zero,
            SubmitControlId = 0, SubmitWindowClass = "", HasVerifiedAlternativeControls = true,
            UsesDialogEnterSubmit = true };
        var native = new RecordingNativePromptApi { Snapshots = [target], FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle };
        var diagnostics = new RecordingInputDiagnostics
        { AfterAfterInput = () => { if (switchToButton) native.FreshSnapshot = standard; } };
        var observation = await ExecuteAsync(native, policy, inputDiagnostics: diagnostics);
        Assert.Equal(switchToButton ? ClientCertificateNativePromptStatus.TargetMismatch :
            ClientCertificateNativePromptStatus.Success, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(switchToButton ? 0 : 1, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_SubmitActivationFails_DoesNotRetry()
    {
        var native = ValidNative();
        native.SubmitActivationResult = false;

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(1, native.SubmitActivationCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForSubmit_DoesNotActivate()
    {
        using var cancellation = new CancellationTokenSource();
        var native = ValidNative();
        native.SubmitReady = false;
        var clock = new RecordingExecutionClock { AfterDelay = cancellation.Cancel };
        var diagnostics = new RecordingExecutionDiagnostics();

        var observation = await ExecuteAsync(
            native,
            clock: clock,
            cancellationToken: cancellation.Token,
            diagnostics: diagnostics);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        Assert.Equal(0, native.SubmitActivationCount);
        Assert.Equal(
            NativePromptExecutionStage.SubmitWait,
            Assert.Single(diagnostics.Observations).Stage);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_UnmappableSecret_ReturnsInputFailedWithoutSendingAnything()
    {
        var native = ValidNative();
        var builder = new NativePromptInputBatchBuilder(new RejectingVirtualKeyMapper());

        var observation = await ExecuteAsync(
            native,
            inputBatchBuilder: builder);

        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    [Fact]
    public async Task ExecuteAsync_PartialSend_ReturnsInputFailedWithoutRetryAndClearsInputArray()
    {
        var native = ValidNative();
        native.SendInputResult = 1;

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.InputFailed, observation.Result.Status);
        Assert.Null(observation.Result.PublicErrorCode);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_SendInputThrows_ReturnsUnknownFailureWithoutRetryAndClearsInputArray()
    {
        var native = ValidNative();
        native.ThrowOnSendInput = true;

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.UnknownFailure, observation.Result.Status);
        Assert.Equal(1, native.SendInputCallCount);
        observation.AssertSecretCleared();
        native.AssertInputArrayCleared();
    }

    [Fact]
    public async Task ExecuteAsync_NativeEnumerationThrows_ReturnsUnknownFailureAndClearsSecret()
    {
        var native = ValidNative();
        native.ThrowOnEnumeration = true;

        var observation = await ExecuteAsync(native);

        Assert.Equal(ClientCertificateNativePromptStatus.UnknownFailure, observation.Result.Status);
        Assert.Equal(0, native.SendInputCallCount);
        observation.AssertSecretCleared();
    }

    private static RecordingNativePromptApi ValidNative()
    {
        var target = NativePromptWindowMatcherTests.Candidate();
        var native = new RecordingNativePromptApi
        {
            Snapshots = [target],
            FreshSnapshot = target,
            FallbackForegroundHandle = target.WindowHandle
        };
        return native;
    }

    private static async Task<ExecutionObservation> ExecuteAsync(
        RecordingNativePromptApi native,
        ClientCertificateNativePromptOptions? policy = null,
        RecordingExecutionClock? clock = null,
        CancellationToken cancellationToken = default,
        char[]? secret = null,
        INativePromptInputBatchBuilder? inputBatchBuilder = null,
        INativePromptExecutionDiagnostics? diagnostics = null,
        INativePromptInputDiagnostics? inputDiagnostics = null,
        INativePromptCapsLockApi? capsLockApi = null)
    {
        var sourceSecret = secret ?? CreateRandomSecret(includeSurrogatePair: false);
        var allocator = new RecordingSecretBufferAllocator();
        var ownedPin = new ClearableSecretBuffer(sourceSecret.AsSpan(), allocator);
        try
        {
            native.ExpectedInputCharacterCount = sourceSecret.Length;
            var executorClock = clock ?? new RecordingExecutionClock();
            var executor = new WindowsNativePromptExecutor(
                    native,
                    executorClock,
                    inputBatchBuilder ?? CreateTestInputBuilder(),
                    diagnostics ?? NoOpNativePromptExecutionDiagnostics.Instance,
                    inputDiagnostics,
                    capsLockApi ?? new RecordingCapsLockApi(native));
            var result = await executor.ExecuteAsync(
                policy ?? NativePromptWindowMatcherTests.Policy(),
                ownedPin,
                NativePromptWindowMatcherTests.TrustedClientProcessId,
                cancellationToken);
            return new(result, allocator);
        }
        finally
        {
            Array.Clear(sourceSecret, 0, sourceSecret.Length);
        }
    }

    private static char[] CreateRandomSecret(bool includeSurrogatePair)
    {
        var secret = RandomNumberGenerator.GetBytes(20)
            .Select(value => (char)('a' + (value % 26)))
            .ToArray();
        if (includeSurrogatePair)
        {
            secret[^2] = '\uD83D';
            secret[^1] = '\uDD10';
        }

        return secret;
    }

    private sealed record ExecutionObservation(
        ClientCertificateNativePromptResult Result,
        RecordingSecretBufferAllocator Allocator)
    {
        public void AssertSecretCleared()
        {
            Assert.Equal(1, Allocator.FreeCount);
            Assert.NotEmpty(Allocator.BytesAtFree);
            Assert.All(Allocator.BytesAtFree, value => Assert.Equal((byte)0, value));
            Array.Clear(Allocator.BytesAtFree, 0, Allocator.BytesAtFree.Length);
        }
    }

    private sealed class RecordingSecretBufferAllocator : IUnmanagedSecretBufferAllocator
    {
        public byte[] BytesAtFree { get; private set; } = [];

        public int FreeCount { get; private set; }

        public IntPtr Allocate(int byteCount) => Marshal.AllocHGlobal(byteCount);

        public void Free(IntPtr buffer, int byteCount)
        {
            BytesAtFree = new byte[byteCount];
            Marshal.Copy(buffer, BytesAtFree, 0, byteCount);
            FreeCount++;
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record RecordingNativePromptApi : IWindowsNativePromptApi
    {
        private readonly List<NativePromptInput[]> _inputReferences = [];
        public bool PrepareInputMethodResult { get; init; } = true;
        public bool InputMethodReady { get; init; } = true;
        public Queue<bool> InputMethodReadyResults { get; } = new();
        public int PrepareInputMethodCalls { get; private set; }
        public Action? AfterInputMethodCheck { get; set; }
        public bool PrepareInputMethod(NativePromptWindowSnapshot snapshot)
        {
            Operations.Add("prepare-ime");
            PrepareInputMethodCalls++;
            return PrepareInputMethodResult;
        }
        public bool IsInputMethodReady(NativePromptWindowSnapshot snapshot)
        {
            Operations.Add("check-ime");
            AfterInputMethodCheck?.Invoke();
            return InputMethodReadyResults.Count == 0 ? InputMethodReady : InputMethodReadyResults.Dequeue();
        }

        public IReadOnlyList<NativePromptWindowSnapshot> Snapshots { get; init; } = [];

        public Queue<IReadOnlyList<NativePromptWindowSnapshot>> SnapshotSequences { get; } = new();

        public NativePromptWindowSnapshot? FreshSnapshot { get; set; }

        public Queue<NativePromptWindowSnapshot?> FreshSnapshots { get; } = new();

        public bool SetForegroundResult { get; init; } = true;

        public IntPtr FallbackForegroundHandle { get; set; }

        public Queue<IntPtr> ForegroundHandles { get; } = new();

        public uint? SendInputResult { get; set; }

        public bool ThrowOnSendInput { get; set; }

        public bool ThrowOnEnumeration { get; set; }

        public int EnumerationCount { get; private set; }

        public int ActivationCount { get; private set; }

        public IntPtr ActivatedWindowHandle { get; private set; }

        public int ObservedTrustedClientProcessId { get; private set; }

        public bool FocusInputResult { get; init; } = true;

        public bool InputIsFocused { get; set; } = true;

        public IntPtr FocusedInputHandle { get; private set; }

        public int SendInputCallCount { get; private set; }

        public int ObservedInputCount { get; private set; }

        public bool ObservedOnlyPinVirtualKeys { get; private set; }

        public NativePromptInput[]? ObservedInputReference { get; private set; }

        public bool InputCharacterCountAvailable { get; init; } = true;

        public int CurrentInputCharacterCount { get; set; }

        public int ExpectedInputCharacterCount { get; set; }

        public bool AutoProcessInput { get; init; } = true;

        public Queue<InputCharacterCountObservation> InputCharacterCountResults { get; } = new();

        public int InputCharacterCountReadCount { get; private set; }

        public bool SubmitReady { get; set; } = true;

        public Queue<bool> SubmitReadyResults { get; } = new();

        public bool SubmitActivationResult { get; set; } = true;

        public int SubmitActivationCount { get; private set; }

        public List<string> Operations { get; } = [];

        public IReadOnlyList<NativePromptWindowSnapshot> EnumerateWindowSnapshots(
            ClientCertificateNativePromptOptions policy,
            int trustedClientProcessId)
        {
            ObservedTrustedClientProcessId = trustedClientProcessId;
            EnumerationCount++;
            if (ThrowOnEnumeration)
            {
                throw new InvalidOperationException("Non-secret native test failure.");
            }

            return SnapshotSequences.Count == 0 ? Snapshots : SnapshotSequences.Dequeue();
        }

        public NativePromptWindowSnapshot? CaptureWindowSnapshot(
            IntPtr windowHandle,
            ClientCertificateNativePromptOptions policy,
            int trustedClientProcessId) =>
            FreshSnapshots.Count == 0 ? FreshSnapshot : FreshSnapshots.Dequeue();

        public bool SetForegroundWindow(IntPtr windowHandle)
        {
            ActivationCount++;
            ActivatedWindowHandle = windowHandle;
            return SetForegroundResult;
        }

        public IntPtr GetForegroundWindow() =>
            ForegroundHandles.Count == 0 ? FallbackForegroundHandle : ForegroundHandles.Dequeue();

        public bool FocusInputControl(NativePromptWindowSnapshot snapshot)
        {
            FocusedInputHandle = snapshot.InputControlHandle;
            return FocusInputResult;
        }

        public bool IsInputControlFocused(NativePromptWindowSnapshot snapshot) => InputIsFocused;

        public uint SendInput(NativePromptInput[] inputs)
        {
            Operations.Add("send-input");
            SendInputCallCount++;
            ObservedInputReference = inputs;
            _inputReferences.Add(inputs);
            ObservedInputCount = inputs.Length;
            ObservedOnlyPinVirtualKeys = HasOnlyPinVirtualKeys(inputs);
            if (ThrowOnSendInput)
            {
                throw new InvalidOperationException("Non-secret native test failure.");
            }

            if (AutoProcessInput)
            {
                CurrentInputCharacterCount = ExpectedInputCharacterCount;
            }

            return SendInputResult ?? checked((uint)inputs.Length);
        }

        public bool TryGetInputCharacterCount(
            NativePromptWindowSnapshot snapshot,
            out int characterCount)
        {
            _ = snapshot;
            Operations.Add("read-input-count");
            InputCharacterCountReadCount++;
            if (InputCharacterCountResults.Count > 0)
            {
                var observation = InputCharacterCountResults.Dequeue();
                characterCount = observation.CharacterCount;
                return observation.Success;
            }

            characterCount = CurrentInputCharacterCount;
            return InputCharacterCountAvailable;
        }

        public bool IsSubmitControlReady(NativePromptWindowSnapshot snapshot)
        {
            _ = snapshot;
            Operations.Add("submit-ready");
            return SubmitReadyResults.Count == 0
                ? SubmitReady
                : SubmitReadyResults.Dequeue();
        }

        public bool ActivateSubmitControl(NativePromptWindowSnapshot snapshot)
        {
            _ = snapshot;
            Operations.Add("activate-submit");
            SubmitActivationCount++;
            return SubmitActivationResult;
        }

        public void AssertInputArrayCleared()
        {
            Assert.NotNull(ObservedInputReference);
            Assert.All(_inputReferences.SelectMany(inputs => inputs), input =>
            {
                Assert.Equal(0u, input.Type);
                Assert.Equal((ushort)0, input.Data.Keyboard.VirtualKey);
                Assert.Equal((ushort)0, input.Data.Keyboard.ScanCode);
                Assert.Equal(0u, input.Data.Keyboard.Flags);
                Assert.Equal(0u, input.Data.Keyboard.Time);
                Assert.Equal(UIntPtr.Zero, input.Data.Keyboard.ExtraInfo);
            });
        }

        private static bool HasOnlyPinVirtualKeys(NativePromptInput[] inputs)
        {
            return inputs.Length > 0 && inputs.All(input =>
                input.Type == NativePromptInput.KeyboardType &&
                input.Data.Keyboard.VirtualKey != 0 &&
                input.Data.Keyboard.VirtualKey != 0x000D &&
                input.Data.Keyboard.ScanCode == 0 &&
                (input.Data.Keyboard.Flags & NativePromptInput.UnicodeKeyFlag) == 0);
        }
    }

    private readonly record struct InputCharacterCountObservation(
        bool Success,
        int CharacterCount);

    private sealed class RecordingExecutionClock : INativePromptExecutionClock
    {
        private long _ticks;

        public Action? AfterDelay { get; init; }

        public int DelayCount { get; private set; }

        public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

        public long GetTimestamp() => _ticks;

        public void Advance(TimeSpan elapsed) =>
            _ticks = checked(_ticks + elapsed.Ticks);

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_ticks - startingTimestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DelayCount++;
            _ticks = checked(_ticks + delay.Ticks);
            AfterDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackInputBatchBuilder(Action afterBuild)
        : INativePromptInputBatchBuilder
    {
        private readonly NativePromptInputBatchBuilder _inner = CreateTestInputBuilder();

        public NativePromptInput[]? ObservedInputReference { get; private set; }

        public NativePromptInput[]? Build(ClearableSecretBuffer secret, IntPtr targetWindow)
        {
            var inputs = _inner.Build(secret, targetWindow);
            ObservedInputReference = inputs;
            afterBuild();
            return inputs;
        }

        public void AssertInputArrayCleared()
        {
            Assert.NotNull(ObservedInputReference);
            Assert.All(ObservedInputReference, input =>
            {
                Assert.Equal(0u, input.Type);
                Assert.Equal((ushort)0, input.Data.Keyboard.VirtualKey);
                Assert.Equal((ushort)0, input.Data.Keyboard.ScanCode);
                Assert.Equal(0u, input.Data.Keyboard.Flags);
                Assert.Equal(0u, input.Data.Keyboard.Time);
                Assert.Equal(UIntPtr.Zero, input.Data.Keyboard.ExtraInfo);
            });
        }
    }

    private sealed class FixedVirtualKeyMapper(params NativePromptVirtualKey[] keys)
        : INativePromptVirtualKeyMapper
    {
        private int _index;

        public bool TryMap(ushort codeUnit, IntPtr targetWindow, out NativePromptVirtualKey key)
        {
            _ = codeUnit;
            key = keys[_index++];
            return true;
        }
    }

    private sealed class RejectingVirtualKeyMapper : INativePromptVirtualKeyMapper
    {
        public bool TryMap(ushort codeUnit, IntPtr targetWindow, out NativePromptVirtualKey key)
        {
            _ = codeUnit;
            key = default;
            return false;
        }
    }

    private static NativePromptInputBatchBuilder CreateTestInputBuilder() => new(new TestVirtualKeyMapper());

    private sealed class TestVirtualKeyMapper : INativePromptVirtualKeyMapper
    {
        public bool TryMap(ushort codeUnit, IntPtr targetWindow, out NativePromptVirtualKey key)
        {
            key = new((ushort)char.ToUpperInvariant((char)codeUnit), char.IsUpper((char)codeUnit));
            return !char.IsSurrogate((char)codeUnit);
        }
    }

    private sealed class RecordingExecutionDiagnostics : INativePromptExecutionDiagnostics
    {
        public List<NativePromptExecutionObservation> Observations { get; } = [];

        public void Clear() => Observations.Clear();

        public void Record(NativePromptExecutionObservation observation) =>
            Observations.Add(observation);
    }
}
