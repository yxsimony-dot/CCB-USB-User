using System.Runtime.InteropServices;
using System.Text;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Windows.Authentication;

internal interface IWebViewSecureInputNative
{
    IntPtr CaptureFocus(IntPtr owner, uint browserProcessId);
    bool PrepareInputMethod(IntPtr target);
    bool IsInputMethodReady(IntPtr target);
    bool AreModifiersReleased();
    NativePromptInput[]? Build(ClearableSecretBuffer pin, IntPtr target);
    uint Send(NativePromptInput[] inputs);
}

// Only the foreground WebView child of our own window may receive this batch.
// No clipboard, debug endpoint, page-script PIN, or global focus activation is used.
public sealed class WindowsWebViewSecureInput
{
    private readonly IWebViewSecureInputNative _native;
    public WindowsWebViewSecureInput() : this(new WebViewSecureInputNative()) { }
    internal WindowsWebViewSecureInput(IWebViewSecureInputNative native) => _native = native;

    public IntPtr Prepare(IntPtr owner, uint browserProcessId)
    {
        var target = _native.CaptureFocus(owner, browserProcessId);
        return target != IntPtr.Zero && _native.AreModifiersReleased() &&
            _native.PrepareInputMethod(target) &&
            _native.CaptureFocus(owner, browserProcessId) == target ? target : IntPtr.Zero;
    }

    public bool IsCurrent(IntPtr owner, uint browserProcessId, IntPtr target) =>
        target != IntPtr.Zero && _native.CaptureFocus(owner, browserProcessId) == target;

    public bool Send(IntPtr owner, uint browserProcessId, IntPtr target,
        ClientCertificatePin pin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(owner, browserProcessId, target) || !_native.AreModifiersReleased() ||
            !_native.IsInputMethodReady(target)) return false;
        using var secret = new ClearableSecretBuffer(pin.AsMemory().Span);
        var inputs = _native.Build(secret, target);
        if (inputs is null || inputs.Length == 0) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(owner, browserProcessId, target) || !_native.AreModifiersReleased() ||
                !_native.IsInputMethodReady(target)) return false;
            var sent = _native.Send(inputs);
            if (sent == inputs.Length) return true;
            // A partial batch may leave one of our keys held. Release only those
            // accepted down events; never retry a character or send a new down event.
            var held = new HashSet<ushort>();
            for (var i = 0; i < Math.Min(sent, inputs.Length); i++)
            {
                var keyboard = inputs[i].Data.Keyboard;
                if ((keyboard.Flags & NativePromptInput.KeyUpFlag) == 0) held.Add(keyboard.VirtualKey);
                else held.Remove(keyboard.VirtualKey);
            }
            if (held.Count != 0)
            {
                var release = held.Select(key => NativePromptInput.VirtualKey(key, true)).ToArray();
                try { _native.Send(release); }
                finally { Array.Clear(release); }
            }
            return false;
        }
        finally { Array.Clear(inputs); }
    }
}

internal sealed class WebViewSecureInputNative : IWebViewSecureInputNative
{
    private readonly WindowsNativePromptInputMethod _ime = new();
    private readonly WindowsNativePromptCapsLock _modifiers = new();
    public IntPtr CaptureFocus(IntPtr owner, uint browserProcessId)
    {
        if (owner == IntPtr.Zero || browserProcessId == 0 || GetForegroundWindow() != owner) return IntPtr.Zero;
        var thread = GetWindowThreadProcessId(owner, out var ownerProcess);
        if (ownerProcess != Environment.ProcessId) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero ||
            !IsChild(owner, info.Focus)) return IntPtr.Zero;
        GetWindowThreadProcessId(info.Focus, out var focusProcess);
        var name = new StringBuilder(128);
        GetClassNameW(info.Focus, name, name.Capacity);
        return focusProcess == browserProcessId &&
            name.ToString().StartsWith("Chrome_", StringComparison.Ordinal) ? info.Focus : IntPtr.Zero;
    }
    public bool PrepareInputMethod(IntPtr target) => _ime.Prepare(target);
    public bool IsInputMethodReady(IntPtr target) => _ime.IsReady(target);
    public bool AreModifiersReleased() => _modifiers.AreModifiersReleased();
    public NativePromptInput[]? Build(ClearableSecretBuffer pin, IntPtr target)
    {
        using var session = WindowsNativePromptKeyboardSession.TryOpen(target);
        return session is null ? null : new NativePromptInputBatchBuilder().Build(pin, target);
    }
    public uint Send(NativePromptInput[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativePromptInput>());

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] NativePromptInput[] inputs, int size);
}
