using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptKeyboardSessionTests
{
    [Theory]
    [InlineData(0, 7, 9)]
    [InlineData(42, 0, 9)]
    [InlineData(42, 7, 0)]
    public void TryOpen_InvalidWindowOrThread_DoesNotAttach(int window, uint targetThread, uint currentThread)
    {
        var native = new RecordingNative { TargetThread = targetThread, CurrentThread = currentThread };
        Assert.Null(WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(window), native));
        Assert.Empty(native.Attachments);
    }

    [Fact]
    public void TryOpen_AttachesOnceAfterCreatingQueue_AndDisposeDetachesOnce()
    {
        var native = new RecordingNative();
        var session = WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native);
        Assert.NotNull(session);
        Assert.Equal(new[] { "queue", "attach" }, native.Operations);
        Assert.Equal(new IntPtr(42), native.TargetWindow);

        session.Dispose();
        session.Dispose();

        Assert.Equal(new[] { (9u, 7u, true), (9u, 7u, false) }, native.Attachments);
    }

    [Fact]
    public void TryOpen_AttachFailure_DoesNotDetach()
    {
        var native = new RecordingNative { AttachSucceeded = false };
        Assert.Null(WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native));
        Assert.Equal(new[] { (9u, 7u, true) }, native.Attachments);
    }

    [Fact]
    public void TryOpen_SameThread_NeverAttachesOrDetachesItself()
    {
        var native = new RecordingNative { TargetThread = 9 };
        using var session = WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native);
        Assert.NotNull(session);
        session.Dispose();
        Assert.Empty(native.Attachments);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-32768, true)]
    [InlineData(-32767, true)]
    public void IsShiftDown_UsesCurrentSharedStateHighBit(short keyState, bool expected)
    {
        var native = new RecordingNative { KeyState = keyState };
        using var session = WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native);
        Assert.Equal(expected, session!.IsShiftDown);
        Assert.Equal(0x10, native.LastVirtualKey);
        native.KeyState = expected ? (short)0 : unchecked((short)0x8000);
        Assert.Equal(!expected, session.IsShiftDown);
    }

    [Fact]
    public void WrongThreadCannotReadOrDetach_OriginalThreadCanStillDispose()
    {
        var native = new RecordingNative();
        var session = WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native)!;
        native.CurrentThread = 19;
        Assert.Throws<InvalidOperationException>(() => _ = session.IsShiftDown);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        Assert.Single(native.Attachments);

        native.CurrentThread = 9;
        session.Dispose();
        Assert.Equal((9u, 7u, false), native.Attachments[1]);
        Assert.Throws<ObjectDisposedException>(() => _ = session.IsShiftDown);
    }

    [Fact]
    public void ExceptionInUsingScope_StillDetaches()
    {
        var native = new RecordingNative();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var session = WindowsNativePromptKeyboardSession.TryOpen(new IntPtr(42), native);
            throw new InvalidOperationException("Synthetic failure.");
        }));
        Assert.Equal(new[] { (9u, 7u, true), (9u, 7u, false) }, native.Attachments);
    }

    private sealed class RecordingNative : IWindowsNativePromptKeyboardSessionApi
    {
        public uint TargetThread { get; init; } = 7;
        public uint CurrentThread { get; set; } = 9;
        public bool AttachSucceeded { get; init; } = true;
        public short KeyState { get; set; }
        public IntPtr TargetWindow { get; private set; }
        public int LastVirtualKey { get; private set; }
        public List<string> Operations { get; } = [];
        public List<(uint Current, uint Target, bool Attach)> Attachments { get; } = [];
        public uint GetWindowThreadId(IntPtr target) { TargetWindow = target; return TargetThread; }
        public uint GetCurrentThreadId() => CurrentThread;
        public void EnsureMessageQueue() => Operations.Add("queue");
        public bool AttachThreadInput(uint current, uint target, bool attach)
        {
            Operations.Add(attach ? "attach" : "detach");
            Attachments.Add((current, target, attach));
            return AttachSucceeded;
        }
        public short GetKeyState(int virtualKey) { LastVirtualKey = virtualKey; return KeyState; }
    }
}
