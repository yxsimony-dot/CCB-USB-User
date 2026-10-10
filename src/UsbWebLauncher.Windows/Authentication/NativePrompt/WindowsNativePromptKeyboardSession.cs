using System.Runtime.InteropServices;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface INativePromptKeyboardSession : IDisposable
{
    bool IsShiftDown { get; }
}

internal interface IWindowsNativePromptKeyboardSessionApi
{
    uint GetWindowThreadId(IntPtr target);
    uint GetCurrentThreadId();
    void EnsureMessageQueue();
    bool AttachThreadInput(uint currentThread, uint targetThread, bool attach);
    short GetKeyState(int virtualKey);
}

internal sealed class WindowsNativePromptKeyboardSessionApi : IWindowsNativePromptKeyboardSessionApi
{
    public uint GetWindowThreadId(IntPtr target) => GetWindowThreadProcessId(target, out _);
    public uint GetCurrentThreadId() => GetCurrentThreadIdNative();
    public void EnsureMessageQueue() => _ = PeekMessageW(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */);
    public bool AttachThreadInput(uint currentThread, uint targetThread, bool attach) =>
        AttachThreadInputNative(currentThread, targetThread, attach);
    public short GetKeyState(int virtualKey) => GetKeyStateNative(virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window,
        uint minimumMessage, uint maximumMessage, uint removeMessage);
    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInputNative(uint currentThread, uint targetThread,
        [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetKeyStateNative(int virtualKey);
}

// Open, all reads and disposal must remain on one OS thread. Attach only once:
// reattaching between events resets the very queue state used as acknowledgement.
internal sealed class WindowsNativePromptKeyboardSession : INativePromptKeyboardSession
{
    private readonly IWindowsNativePromptKeyboardSessionApi _native;
    private readonly uint _ownerThread;
    private readonly uint _targetThread;
    private readonly bool _attached;
    private bool _disposed;

    private WindowsNativePromptKeyboardSession(IWindowsNativePromptKeyboardSessionApi native,
        uint ownerThread, uint targetThread, bool attached)
    {
        _native = native;
        _ownerThread = ownerThread;
        _targetThread = targetThread;
        _attached = attached;
    }

    public static INativePromptKeyboardSession? TryOpen(IntPtr target) =>
        TryOpen(target, new WindowsNativePromptKeyboardSessionApi());

    internal static INativePromptKeyboardSession? TryOpen(IntPtr target,
        IWindowsNativePromptKeyboardSessionApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (target == IntPtr.Zero) return null;
        var targetThread = native.GetWindowThreadId(target);
        var ownerThread = native.GetCurrentThreadId();
        if (targetThread == 0 || ownerThread == 0) return null;

        native.EnsureMessageQueue();
        var attached = ownerThread != targetThread;
        if (attached && !native.AttachThreadInput(ownerThread, targetThread, true)) return null;
        return new WindowsNativePromptKeyboardSession(native, ownerThread, targetThread, attached);
    }

    public bool IsShiftDown
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireOwnerThread();
            return (_native.GetKeyState(0x10) & 0x8000) != 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        RequireOwnerThread();
        try
        {
            if (_attached) _ = _native.AttachThreadInput(_ownerThread, _targetThread, false);
        }
        finally
        {
            _disposed = true;
        }
    }

    private void RequireOwnerThread()
    {
        if (_native.GetCurrentThreadId() != _ownerThread)
            throw new InvalidOperationException("Keyboard session must remain on its creating OS thread.");
    }
}
