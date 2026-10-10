using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptCapsLockTests
{
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0x14)]
    public void AreModifiersReleased_RejectsEveryPhysicallyHeldModifier(int held)
    {
        var native = new FakeNative { HeldKey = held };
        Assert.False(new WindowsNativePromptCapsLock(native).AreModifiersReleased());
        Assert.Contains(held, native.AsyncReads);
        Assert.Empty(native.Calls);
        Assert.Empty(native.Sent);
    }

    [Fact]
    public void AreModifiersReleased_UsesHighBit_NotPreviouslyPressedLowBit()
    {
        var native = new FakeNative { AsyncReleasedValue = 1 };
        Assert.True(new WindowsNativePromptCapsLock(native).AreModifiersReleased());
        Assert.Equal(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x14 }, native.AsyncReads);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void ModifierHeldInitially_RejectsBeforeAttachmentOrReadingOriginalState()
    {
        var native = new FakeNative { HeldKey = 0x10 };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Null(original);
        Assert.Empty(native.Calls);
        Assert.Empty(native.Sent);
    }

    [Fact]
    public void ModifierBecomesHeldBeforeToggle_DetachesWithoutSending()
    {
        var native = new FakeNative { HoldCtrlAfterStateRead = true };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Equal((bool?)false, original);
        Assert.Empty(native.Sent);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-32768, false)]
    [InlineData(-32767, true)]
    public void AlreadyDesired_ReadsToggleBitInSession_AndDetachesWithoutInput(int state, bool desired)
    {
        var native = new FakeNative { State = (short)state };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.True(helper.TrySet(new IntPtr(42), desired, () => true, out var original));
        Assert.Equal((bool?)desired, original);
        Assert.Equal(new[] { "open:42", "state:20", "dispose" }, native.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Toggle_WaitsForObservedState_ThenDetaches(bool desired)
    {
        var native = new FakeNative { State = (short)(desired ? 0 : 1), ChangeAfterDelay = true };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.True(helper.TrySet(new IntPtr(42), desired, () => true, out var original));
        Assert.Equal((bool?)!desired, original);
        Assert.Equal(10, native.ElapsedMilliseconds);
        Assert.Equal("dispose", native.Calls[^1]);
        var batch = Assert.Single(native.Sent);
        Assert.Equal(2, batch.Length);
        for (var i = 0; i < batch.Length; i++)
        {
            Assert.Equal(NativePromptInput.KeyboardType, batch[i].Type);
            Assert.Equal((ushort)0x14, batch[i].Data.Keyboard.VirtualKey);
            Assert.Equal(i == 0 ? 0u : NativePromptInput.KeyUpFlag, batch[i].Data.Keyboard.Flags);
            Assert.Equal((ushort)0, batch[i].Data.Keyboard.ScanCode);
            Assert.Equal(0u, batch[i].Data.Keyboard.Time);
            Assert.Equal(UIntPtr.Zero, batch[i].Data.Keyboard.ExtraInfo);
        }
    }

    [Fact]
    public void Timeout_DoesNotRetoggle_AndKeepsOriginalState()
    {
        var native = new FakeNative();
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Equal((bool?)false, original);
        Assert.Equal(500, native.ElapsedMilliseconds);
        Assert.Single(native.Sent);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Fact]
    public void LostFocusDuringWait_StopsWithoutRetoggle()
    {
        var native = new FakeNative();
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => native.ElapsedMilliseconds < 10, out _));
        Assert.Single(native.Sent);
        Assert.Equal(10, native.ElapsedMilliseconds);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Fact]
    public void LostFocusBeforeSend_DetachesWithoutInput()
    {
        var native = new FakeNative();
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => !native.Calls.Contains("state:20"), out var original));
        Assert.Equal((bool?)false, original);
        Assert.Empty(native.Sent);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void SendFailure_DoesNotRetoggle(uint sent)
    {
        var native = new FakeNative { SendResult = sent };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Equal((bool?)false, original);
        Assert.Equal(sent == 1 ? 2 : 1, native.Sent.Count);
        if (sent == 1) AssertCapsKeyUpOnly(native.Sent[1]);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Fact]
    public void UnavailableSession_LeavesOriginalUnknown()
    {
        var native = new FakeNative { SessionAvailable = false };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Null(original);
        Assert.Equal(new[] { "open:42" }, native.Calls);
    }

    [Fact]
    public void InitialGuardFailure_DoesNotOpenSessionEvenWhenAlreadyDesired()
    {
        var native = new FakeNative { State = 1 };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => false, out var original));
        Assert.Null(original);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void GuardExceptionAfterStateRead_DetachesAndPreservesOriginal()
    {
        var native = new FakeNative();
        var helper = new WindowsNativePromptCapsLock(native);
        bool? original = null;
        Assert.Throws<InvalidOperationException>(() => helper.TrySet(new IntPtr(42), true,
            () => native.Calls.Contains("state:20") ? throw new InvalidOperationException("guard") : true, out original));
        Assert.Equal((bool?)false, original);
        Assert.Empty(native.Sent);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Fact]
    public void SendException_PropagatesAfterDetaching_AndKeepsOriginalState()
    {
        var failure = new InvalidOperationException("send");
        var native = new FakeNative { SendFailure = failure };
        var helper = new WindowsNativePromptCapsLock(native);
        bool? original = null;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            helper.TrySet(new IntPtr(42), true, () => true, out original)));
        Assert.Equal((bool?)false, original);
        Assert.Equal(2, native.Sent.Count);
        AssertCapsKeyUpOnly(native.Sent[1]);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialSend_KeyUpCleanupFailure_DoesNotRetryOrPreventDetach(bool throws)
    {
        var native = new FakeNative { SendResult = 1, CleanupResult = 0,
            CleanupFailure = throws ? new InvalidOperationException("cleanup") : null };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Equal((bool?)false, original);
        Assert.Equal(2, native.Sent.Count);
        AssertCapsKeyUpOnly(native.Sent[1]);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    [Fact]
    public void SendException_KeyUpCleanupException_DoesNotReplaceOriginalFailure()
    {
        var originalFailure = new InvalidOperationException("original");
        var native = new FakeNative { SendFailure = originalFailure,
            CleanupFailure = new ArgumentException("cleanup") };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.Same(originalFailure, Assert.Throws<InvalidOperationException>(() =>
            helper.TrySet(new IntPtr(42), true, () => true, out _)));
        Assert.Equal(2, native.Sent.Count);
        AssertCapsKeyUpOnly(native.Sent[1]);
        Assert.Equal("dispose", native.Calls[^1]);
    }

    private static void AssertCapsKeyUpOnly(NativePromptInput[] batch)
    {
        var input = Assert.Single(batch);
        Assert.Equal(NativePromptInput.KeyboardType, input.Type);
        Assert.Equal((ushort)0x14, input.Data.Keyboard.VirtualKey);
        Assert.Equal(NativePromptInput.KeyUpFlag, input.Data.Keyboard.Flags);
        Assert.Equal((ushort)0, input.Data.Keyboard.ScanCode);
        Assert.Equal(0u, input.Data.Keyboard.Time);
        Assert.Equal(UIntPtr.Zero, input.Data.Keyboard.ExtraInfo);
    }

    [Fact]
    public void LaterFailedAttempt_DoesNotReuseOriginalStateFromEarlierCall()
    {
        var native = new FakeNative { State = 1 };
        var helper = new WindowsNativePromptCapsLock(native);
        Assert.True(helper.TrySet(new IntPtr(42), true, () => true, out var original));
        Assert.Equal((bool?)true, original);
        Assert.False(helper.TrySet(new IntPtr(42), true, () => false, out original));
        Assert.Null(original);
    }

    private sealed class FakeNative : IWindowsNativePromptCapsLockNativeApi
    {
        public short State { get; set; }
        public bool ChangeAfterDelay { get; set; }
        public bool SessionAvailable { get; set; } = true;
        public uint SendResult { get; set; } = 2;
        public Exception? SendFailure { get; set; }
        public int? HeldKey { get; set; }
        public short AsyncReleasedValue { get; set; }
        public bool HoldCtrlAfterStateRead { get; set; }
        public List<int> AsyncReads { get; } = [];
        public Exception? CleanupFailure { get; set; }
        public uint CleanupResult { get; set; } = 1;
        public long ElapsedMilliseconds { get; private set; }
        public List<string> Calls { get; } = [];
        public List<NativePromptInput[]> Sent { get; } = [];
        private bool open;
        public INativePromptKeyboardSession? OpenSession(IntPtr target)
        {
            Calls.Add($"open:{target}");
            if (!SessionAvailable) return null;
            Assert.False(open);
            open = true;
            return new Session(this);
        }
        public short GetKeyState(int virtualKey)
        {
            Assert.True(open); Calls.Add($"state:{virtualKey}");
            if (HoldCtrlAfterStateRead) HeldKey = 0x11;
            return State;
        }
        public short GetAsyncKeyState(int virtualKey)
        {
            AsyncReads.Add(virtualKey);
            return HeldKey == virtualKey ? unchecked((short)0x8000) : AsyncReleasedValue;
        }
        public uint SendInput(NativePromptInput[] inputs)
        {
            Assert.True(open); Calls.Add("send"); Sent.Add(inputs);
            if (Sent.Count > 1)
            {
                if (CleanupFailure is not null) throw CleanupFailure;
                return CleanupResult;
            }
            if (SendFailure is not null) throw SendFailure;
            return SendResult;
        }
        public void Delay(int milliseconds)
        {
            Assert.True(open);
            ElapsedMilliseconds += milliseconds;
            if (ChangeAfterDelay) { State ^= 1; ChangeAfterDelay = false; }
        }
        private sealed class Session(FakeNative native) : INativePromptKeyboardSession
        {
            public bool IsShiftDown => throw new InvalidOperationException("Caps helper must not query Shift.");
            public void Dispose() { Assert.True(native.open); native.open = false; native.Calls.Add("dispose"); }
        }
    }
}
