using System.Runtime.InteropServices;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface IWindowsNativePromptImeApi
{
    IntPtr GetDefaultWindow(IntPtr target);
    bool TryGetOpen(IntPtr ime, out bool open);
    bool TrySetClosed(IntPtr ime);
}

internal sealed class WindowsNativePromptImeApi : IWindowsNativePromptImeApi
{
    private const uint ImeControlMessage = 0x0283;
    private const uint GetOpenStatus = 0x0005;
    private const uint SetOpenStatus = 0x0006;
    private const uint BlockAndAbortIfHung = 0x0003;
    private const uint TimeoutMilliseconds = 100;

    public IntPtr GetDefaultWindow(IntPtr target) => ImmGetDefaultIMEWnd(target);

    public bool TryGetOpen(IntPtr ime, out bool open)
    {
        var succeeded = SendMessageTimeoutW(
            ime, ImeControlMessage, new UIntPtr(GetOpenStatus), IntPtr.Zero,
            BlockAndAbortIfHung, TimeoutMilliseconds, out var result) != IntPtr.Zero;
        open = succeeded && result != UIntPtr.Zero;
        return succeeded;
    }

    public bool TrySetClosed(IntPtr ime) => SendMessageTimeoutW(
        ime, ImeControlMessage, new UIntPtr(SetOpenStatus), IntPtr.Zero,
        BlockAndAbortIfHung, TimeoutMilliseconds, out _) != IntPtr.Zero;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message,
        UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}

internal sealed class WindowsNativePromptInputMethod
{
    private readonly IWindowsNativePromptImeApi _native;

    public WindowsNativePromptInputMethod()
        : this(new WindowsNativePromptImeApi())
    {
    }

    internal WindowsNativePromptInputMethod(IWindowsNativePromptImeApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public bool Prepare(IntPtr target)
    {
        if (!TryGetState(target, out var ime, out var open)) return false;
        if (!open) return true;

        // Address only this control's IME window. Never switch the global layout,
        // synthesize an IME shortcut, or repeat a failed state-changing request.
        return _native.TrySetClosed(ime) &&
               _native.TryGetOpen(ime, out var remainsOpen) &&
               !remainsOpen;
    }

    public bool IsReady(IntPtr target) =>
        TryGetState(target, out _, out var open) && !open;

    private bool TryGetState(IntPtr target, out IntPtr ime, out bool open)
    {
        ime = IntPtr.Zero;
        open = false;
        if (target == IntPtr.Zero) return false;
        ime = _native.GetDefaultWindow(target);
        return ime != IntPtr.Zero && _native.TryGetOpen(ime, out open);
    }
}
