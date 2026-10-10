using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface INativePromptCapsLockApi
{
    bool AreModifiersReleased();
    bool TrySet(IntPtr target, bool desired, Func<bool> canChange, out bool? originalState);
}

internal interface IWindowsNativePromptCapsLockNativeApi
{
    INativePromptKeyboardSession? OpenSession(IntPtr target);
    short GetKeyState(int virtualKey);
    short GetAsyncKeyState(int virtualKey);
    uint SendInput(NativePromptInput[] inputs);
    long ElapsedMilliseconds { get; }
    void Delay(int milliseconds);
}

internal sealed class WindowsNativePromptCapsLock : INativePromptCapsLockApi
{
    private const ushort CapsLockVirtualKey = 0x14;
    private readonly IWindowsNativePromptCapsLockNativeApi _native;
    public WindowsNativePromptCapsLock() : this(new WindowsNativePromptCapsLockNativeApi()) { }
    internal WindowsNativePromptCapsLock(IWindowsNativePromptCapsLockNativeApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));
    public bool AreModifiersReleased()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x14 })
            if ((_native.GetAsyncKeyState(key) & 0x8000) != 0) return false;
        return true;
    }
    public bool TrySet(IntPtr target, bool desired, Func<bool> canChange, out bool? originalState)
    {
        originalState = null;
        ArgumentNullException.ThrowIfNull(canChange);
        if (target == IntPtr.Zero || !canChange() || !AreModifiersReleased()) return false;

        // This method is deliberately synchronous: session open, shared state reads,
        // and disposal must stay on the same OS thread. No character is sent here.
        using var session = _native.OpenSession(target);
        if (session is null) return false;
        originalState = (_native.GetKeyState(CapsLockVirtualKey) & 1) != 0;
        if (!canChange() || !AreModifiersReleased()) return false;
        if (originalState == desired) return true;

        var started = _native.ElapsedMilliseconds;
        var capsMayBeHeld = true;
        try
        {
            var sent = _native.SendInput([
                NativePromptInput.VirtualKey(CapsLockVirtualKey, false),
                NativePromptInput.VirtualKey(CapsLockVirtualKey, true)]);
            capsMayBeHeld = sent != 0 && sent != 2;
            if (sent != 2) return false;
        }
        finally
        {
            if (capsMayBeHeld)
            {
                // A partial native send or exception may leave our down event held.
                // Release only; never send another down event or retry the toggle.
                try { _ = _native.SendInput([NativePromptInput.VirtualKey(CapsLockVirtualKey, true)]); }
                catch { /* Preserve the original failure and still detach the session. */ }
            }
        }

        while (true)
        {
            if (!canChange()) return false;
            if (((_native.GetKeyState(CapsLockVirtualKey) & 1) != 0) == desired) return true;
            if (_native.ElapsedMilliseconds - started >= 500) return false;
            _native.Delay(10);
        }
    }
}

internal sealed class WindowsNativePromptCapsLockNativeApi : IWindowsNativePromptCapsLockNativeApi
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public INativePromptKeyboardSession? OpenSession(IntPtr target) => WindowsNativePromptKeyboardSession.TryOpen(target);
    public short GetKeyState(int virtualKey) => GetKeyStateNative(virtualKey);
    public short GetAsyncKeyState(int virtualKey) => GetAsyncKeyStateNative(virtualKey);
    public uint SendInput(NativePromptInput[] inputs) =>
        SendInputNative((uint)inputs.Length, inputs, Marshal.SizeOf<NativePromptInput>());
    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);

    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetKeyStateNative(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyStateNative(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(uint count, [In] NativePromptInput[] inputs, int size);
}
