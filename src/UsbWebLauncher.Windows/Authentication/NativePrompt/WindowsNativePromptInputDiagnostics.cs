using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal readonly record struct NativePromptFailureFocus(bool ForegroundMatches,
    bool GuiThreadInfoSucceeded, bool ActiveWindowMatches, bool InputFocusMatches);

// Never read the control text or credential. Keep input-environment evidence across
// launcher restarts so a failed attempt can be compared with a successful attempt.
internal sealed class WindowsNativePromptInputDiagnostics : INativePromptInputDiagnostics
{
    private readonly Func<NativePromptWindowSnapshot, NativePromptFailureFocus> _readFailureFocus;
    private readonly Action<object> _writeFailure;

    public WindowsNativePromptInputDiagnostics()
        : this(ReadFailureFocus, data => NativePromptInputHistory.Append(data)) { }

    internal WindowsNativePromptInputDiagnostics(
        Func<NativePromptWindowSnapshot, NativePromptFailureFocus> readFailureFocus,
        Action<object> writeFailure)
    {
        _readFailureFocus = readFailureFocus;
        _writeFailure = writeFailure;
    }

    public void RecordInputFailure(NativePromptWindowSnapshot snapshot, int trustedClientProcessId,
        NativePromptExecutionStage stage, string failureReason, int sentCharacterCount, int confirmedCharacterCount)
    {
        try
        {
            var focus = _readFailureFocus(snapshot);
            _writeFailure(new
            {
                Phase = "input-failure",
                TrustedClientProcessId = trustedClientProcessId,
                TargetProcessId = snapshot.ProcessId,
                Stage = stage.ToString(),
                FailureReason = failureReason,
                FocusSnapshotTiming = "after-cleanup",
                SentCharacterCount = sentCharacterCount,
                ConfirmedCharacterCount = confirmedCharacterCount,
                focus.ForegroundMatches,
                focus.GuiThreadInfoSucceeded,
                focus.ActiveWindowMatches,
                focus.InputFocusMatches
            });
        }
        catch { /* Failure reporting cannot alter authentication or modifier cleanup. */ }
    }

    private static NativePromptFailureFocus ReadFailureFocus(NativePromptWindowSnapshot snapshot)
    {
        var target = snapshot.InputControlHandle != IntPtr.Zero ? snapshot.InputControlHandle : snapshot.WindowHandle;
        var thread = GetWindowThreadProcessId(target, out var process);
        var info = new GuiThreadInformation { Size = checked((uint)Marshal.SizeOf<GuiThreadInformation>()) };
        var succeeded = thread != 0 && process == (uint)snapshot.ProcessId && GetGUIThreadInfo(thread, ref info);
        return new(GetForegroundWindow() == snapshot.ForegroundWindowHandle, succeeded,
            succeeded && info.ActiveWindow == snapshot.WindowHandle,
            succeeded && info.FocusWindow == snapshot.InputControlHandle);
    }

    public void RecordEnvironment(NativePromptWindowSnapshot snapshot, int trustedClientProcessId)
    {
        try
        {
            var target = snapshot.InputControlHandle != IntPtr.Zero
                ? snapshot.InputControlHandle : snapshot.WindowHandle;
            var thread = GetWindowThreadProcessId(target, out var process);
            if (thread == 0 || process != (uint)snapshot.ProcessId) return;
            NativePromptInputHistory.Append(new
            {
                Phase = "environment",
                TrustedClientProcessId = trustedClientProcessId,
                TargetProcessId = process,
                TargetThreadId = thread,
                TargetLayout = GetKeyboardLayout(thread).ToInt64().ToString("X"),
                BrokerLayout = GetKeyboardLayout(0).ToInt64().ToString("X"),
                BrokerCapsLock = (GetKeyState(0x14) & 1) != 0,
                ShiftDown = IsDown(0x10),
                ControlDown = IsDown(0x11),
                AltDown = IsDown(0x12),
                WindowsKeyDown = IsDown(0x5B) || IsDown(0x5C),
                ImeOpen = ReadImeOpen(target)
            });
        }
        catch
        {
            // Diagnostics cannot change authentication or cause another input attempt.
        }
    }

    public void RecordCharacterCount(NativePromptWindowSnapshot snapshot,
        int trustedClientProcessId, string phase, int count)
    {
        // These are the counts already obtained by the executor, not a second read.
        NativePromptInputHistory.Append(new
        {
            Phase = phase,
            TrustedClientProcessId = trustedClientProcessId,
            TargetProcessId = snapshot.ProcessId,
            CharacterCount = count
        });
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool? ReadImeOpen(IntPtr target)
    {
        var ime = ImmGetDefaultIMEWnd(target);
        // A missing or unresponsive IME window means unknown, not "closed".
        if (ime == IntPtr.Zero) return null;
        return SendMessageTimeoutW(ime, 0x0283 /* WM_IME_CONTROL */,
            new UIntPtr(0x0005) /* IMC_GETOPENSTATUS */, IntPtr.Zero,
            0x0003 /* SMTO_BLOCK | SMTO_ABORTIFHUNG */, 100, out var result) == IntPtr.Zero
            ? null : result != UIntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInformation
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message,
        UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}

internal static class NativePromptInputHistory
{
    private static readonly object Sync = new();
    private const long MaximumBytes = 65_536;

    internal static void Append(object data, string? path = null)
    {
        try
        {
            lock (Sync)
            {
                var target = path ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UsbWebLauncher", "NativePromptBroker", "input-environment.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                if (File.Exists(target) && new FileInfo(target).Length >= MaximumBytes)
                    File.Move(target, target + ".previous", overwrite: true);
                var entry = JsonSerializer.Serialize(new { TimestampUtc = DateTime.UtcNow, Data = data });
                File.AppendAllText(target, entry + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // A locked, full, or unavailable diagnostics directory is non-fatal.
        }
    }
}
