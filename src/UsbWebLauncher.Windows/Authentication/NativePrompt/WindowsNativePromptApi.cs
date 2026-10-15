using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePromptInput
{
    internal const uint MouseType = 0;
    internal const uint KeyboardType = 1;
    internal const uint HardwareType = 2;
    internal const uint KeyUpFlag = 0x0002;
    internal const uint UnicodeKeyFlag = 0x0004;
    internal const ushort ShiftVirtualKey = 0x0010;

    public uint Type;
    public NativePromptInputUnion Data;

    internal static NativePromptInput VirtualKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = KeyboardType,
        Data = new NativePromptInputUnion
        {
            Keyboard = new NativePromptKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyUpFlag : 0
            }
        }
    };

}

[StructLayout(LayoutKind.Explicit)]
internal struct NativePromptInputUnion
{
    [FieldOffset(0)]
    public NativePromptMouseInput Mouse;

    [FieldOffset(0)]
    public NativePromptKeyboardInput Keyboard;

    [FieldOffset(0)]
    public NativePromptHardwareInput Hardware;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePromptMouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePromptKeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePromptHardwareInput
{
    public uint Message;
    public ushort ParameterLow;
    public ushort ParameterHigh;
}

internal interface IWindowsNativePromptApi
{
    IReadOnlyList<NativePromptWindowSnapshot> EnumerateWindowSnapshots(
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId = 0);

    NativePromptWindowSnapshot? CaptureWindowSnapshot(
        IntPtr windowHandle,
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId = 0);

    bool SetForegroundWindow(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    bool FocusInputControl(NativePromptWindowSnapshot snapshot);

    bool IsInputControlFocused(NativePromptWindowSnapshot snapshot);

    bool PrepareInputMethod(NativePromptWindowSnapshot snapshot);

    bool IsInputMethodReady(NativePromptWindowSnapshot snapshot);


    uint SendInput(NativePromptInput[] inputs);

    bool TryGetInputCharacterCount(
        NativePromptWindowSnapshot snapshot,
        out int characterCount);

    bool IsSubmitControlReady(NativePromptWindowSnapshot snapshot);

    bool ActivateSubmitControl(NativePromptWindowSnapshot snapshot);
}

internal sealed class WindowsNativePromptApi : IWindowsNativePromptApi
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int ProcessBasicInformationClass = 0;
    private const uint TokenQuery = 0x00000008;
    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint GetAncestorRoot = 2;
    private const uint GetWindowOwner = 4;
    private const uint GetWindowChild = 5;
    private const uint GetWindowNext = 2;
    private const int MaximumPathCharacters = 32_768;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;
    private const uint GetTextLengthMessage = 0x000E;
    private const uint ButtonClickMessage = 0x00F5;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint SendMessageBlock = 0x0001;
    private const uint InputStateQueryTimeoutMilliseconds = 100;
    private const uint SubmitActivationTimeoutMilliseconds = 1_000;
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public IReadOnlyList<NativePromptWindowSnapshot> EnumerateWindowSnapshots(
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!TryGetCurrentIdentity(out var currentUserSid, out var currentSessionId))
        {
            return [];
        }

        var snapshots = new List<NativePromptWindowSnapshot>();
        var isTrustedDescendant = IsTrustedClientDescendant(policy);
        EnumWindowsCallback callback = (windowHandle, parameter) =>
        {
            _ = parameter;
            if (isTrustedDescendant)
            {
                GetWindowThreadProcessId(windowHandle, out var rootProcessId);
                if (trustedClientProcessId <= 0)
                {
                    return true;
                }

                if (rootProcessId == checked((uint)trustedClientProcessId))
                {
                    EnumWindowsCallback childCallback = (childHandle, childParameter) =>
                    {
                        _ = childParameter;
                        var childSnapshot = CaptureWindowSnapshot(
                            childHandle,
                            policy,
                            trustedClientProcessId,
                            currentUserSid,
                            currentSessionId);
                        if (childSnapshot is not null)
                        {
                            snapshots.Add(childSnapshot);
                        }

                        return true;
                    };
                    EnumChildWindows(windowHandle, childCallback, IntPtr.Zero);
                    GC.KeepAlive(childCallback);
                }

                var ownedTopLevelSnapshot = CaptureWindowSnapshot(
                    windowHandle,
                    policy,
                    trustedClientProcessId,
                    currentUserSid,
                    currentSessionId);
                if (ownedTopLevelSnapshot is not null)
                {
                    snapshots.Add(ownedTopLevelSnapshot);
                }

                return true;
            }

            var snapshot = CaptureWindowSnapshot(
                windowHandle,
                policy,
                trustedClientProcessId,
                currentUserSid,
                currentSessionId);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            return [];
        }

        GC.KeepAlive(callback);
        return snapshots;
    }

    public NativePromptWindowSnapshot? CaptureWindowSnapshot(
        IntPtr windowHandle,
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return TryGetCurrentIdentity(out var currentUserSid, out var currentSessionId)
            ? CaptureWindowSnapshot(
                windowHandle,
                policy,
                trustedClientProcessId,
                currentUserSid,
                currentSessionId)
            : null;
    }

    public bool SetForegroundWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindowNative();
        if (foregroundWindow == windowHandle)
        {
            return true;
        }

        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
        if (currentThreadId == 0 || targetThreadId == 0)
        {
            return false;
        }

        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var attachedToForeground = false;
        var attachedToTarget = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedToForeground = AttachThreadInput(
                    currentThreadId,
                    foregroundThreadId,
                    attach: true);
                if (!attachedToForeground)
                {
                    return false;
                }
            }

            if (targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                attachedToTarget = AttachThreadInput(
                    currentThreadId,
                    targetThreadId,
                    attach: true);
                if (!attachedToTarget)
                {
                    return false;
                }
            }

            _ = BringWindowToTop(windowHandle);
            _ = SetActiveWindow(windowHandle);
            _ = SetForegroundWindowNative(windowHandle);
            return GetForegroundWindowNative() == windowHandle;
        }
        finally
        {
            if (attachedToTarget)
            {
                _ = AttachThreadInput(currentThreadId, targetThreadId, attach: false);
            }

            if (attachedToForeground)
            {
                _ = AttachThreadInput(currentThreadId, foregroundThreadId, attach: false);
            }
        }
    }

    public IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    public bool FocusInputControl(NativePromptWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.InputControlHandle == IntPtr.Zero)
        {
            return snapshot.IsTopLevel;
        }

        var targetThreadId = GetWindowThreadProcessId(snapshot.InputControlHandle, out _);
        var currentThreadId = GetCurrentThreadId();
        if (targetThreadId == 0 || currentThreadId == 0)
        {
            return false;
        }

        var attached = targetThreadId == currentThreadId ||
            AttachThreadInput(currentThreadId, targetThreadId, attach: true);
        if (!attached)
        {
            return false;
        }

        try
        {
            _ = SetFocus(snapshot.InputControlHandle);
            return IsInputControlFocused(snapshot);
        }
        finally
        {
            if (targetThreadId != currentThreadId)
            {
                _ = AttachThreadInput(currentThreadId, targetThreadId, attach: false);
            }
        }
    }

    public bool IsInputControlFocused(NativePromptWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.InputControlHandle == IntPtr.Zero)
        {
            return snapshot.IsTopLevel;
        }

        var targetThreadId = GetWindowThreadProcessId(snapshot.InputControlHandle, out _);
        var information = new GuiThreadInformation
        {
            Size = checked((uint)Marshal.SizeOf<GuiThreadInformation>())
        };
        return targetThreadId != 0 &&
               GetGUIThreadInfo(targetThreadId, ref information) &&
               information.ActiveWindow == snapshot.WindowHandle &&
               information.FocusWindow == snapshot.InputControlHandle;
    }

    public uint SendInput(NativePromptInput[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return SendInputNative(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativePromptInput>());
    }

    public bool PrepareInputMethod(NativePromptWindowSnapshot snapshot) =>
        TryGetFocusedInputTarget(snapshot, out var target) &&
        new WindowsNativePromptInputMethod().Prepare(target);

    public bool IsInputMethodReady(NativePromptWindowSnapshot snapshot) =>
        TryGetFocusedInputTarget(snapshot, out var target) &&
        new WindowsNativePromptInputMethod().IsReady(target);

    private bool TryGetFocusedInputTarget(NativePromptWindowSnapshot snapshot, out IntPtr target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        target = snapshot.InputControlHandle != IntPtr.Zero
            ? snapshot.InputControlHandle : snapshot.WindowHandle;
        return target != IntPtr.Zero &&
            GetWindowThreadProcessId(target, out var processId) != 0 &&
            processId == (uint)snapshot.ProcessId &&
            GetForegroundWindow() == snapshot.ForegroundWindowHandle &&
            IsInputControlFocused(snapshot);
    }

    public bool TryGetInputCharacterCount(
        NativePromptWindowSnapshot snapshot,
        out int characterCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        characterCount = 0;
        var handle = snapshot.InputControlHandle;
        if (handle == IntPtr.Zero ||
            snapshot.ProcessId <= 0 ||
            snapshot.InputControlId <= 0 ||
            string.IsNullOrEmpty(snapshot.InputWindowClass) ||
            !IsChild(snapshot.WindowHandle, handle) ||
            !IsWindowVisible(handle) ||
            !IsWindowEnabled(handle) ||
            GetDlgCtrlID(handle) != snapshot.InputControlId ||
            !TryGetWindowClass(handle, out var windowClass) ||
            !string.Equals(
                windowClass,
                snapshot.InputWindowClass,
                StringComparison.Ordinal))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId != (uint)snapshot.ProcessId ||
            SendMessageTimeoutNative(
                handle,
                GetTextLengthMessage,
                UIntPtr.Zero,
                IntPtr.Zero,
                SendMessageAbortIfHung | SendMessageBlock,
                InputStateQueryTimeoutMilliseconds,
                out var result) == IntPtr.Zero)
        {
            return false;
        }

        var nativeCount = result.ToUInt64();
        if (nativeCount > int.MaxValue)
        {
            return false;
        }

        characterCount = checked((int)nativeCount);
        return true;
    }

    public bool IsSubmitControlReady(NativePromptWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.UsesDialogEnterSubmit) return IsDialogEnterReady(snapshot);
        var handle = snapshot.SubmitControlHandle;
        if (handle == IntPtr.Zero ||
            snapshot.ProcessId <= 0 ||
            snapshot.SubmitControlId <= 0 ||
            string.IsNullOrEmpty(snapshot.SubmitWindowClass) ||
            !IsChild(snapshot.WindowHandle, handle) ||
            !IsWindowVisible(handle) ||
            !IsWindowEnabled(handle) ||
            GetDlgCtrlID(handle) != snapshot.SubmitControlId ||
            !TryGetWindowClass(handle, out var windowClass) ||
            !string.Equals(
                windowClass,
                snapshot.SubmitWindowClass,
                StringComparison.Ordinal))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        return processId == (uint)snapshot.ProcessId;
    }

    public bool ActivateSubmitControl(NativePromptWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.UsesDialogEnterSubmit)
        {
            if (!IsDialogEnterReady(snapshot)) return false;
            var inputs = new[] { NativePromptInput.VirtualKey(0x0D, false), NativePromptInput.VirtualKey(0x0D, true) };
            var complete = false;
            try
            {
                complete = SendInput(inputs) == inputs.Length;
                return complete;
            }
            finally
            {
                // A partial batch may have delivered only key-down. Release it,
                // but never retry the submission key-down.
                if (!complete) _ = SendInput([NativePromptInput.VirtualKey(0x0D, true)]);
                Array.Clear(inputs);
            }
        }
        return IsSubmitControlReady(snapshot) &&
               SendMessageTimeoutNative(
                   snapshot.SubmitControlHandle,
                   ButtonClickMessage,
                   UIntPtr.Zero,
                   IntPtr.Zero,
                   SendMessageAbortIfHung | SendMessageBlock,
                   SubmitActivationTimeoutMilliseconds,
                   out _) != IntPtr.Zero;
    }

    private bool IsDialogEnterReady(NativePromptWindowSnapshot snapshot)
    {
        if (snapshot.ProcessId <= 0 || snapshot.WindowHandle == IntPtr.Zero ||
            snapshot.SubmitControlHandle != IntPtr.Zero || snapshot.SubmitControlId != 0 ||
            snapshot.SubmitWindowClass.Length != 0 || !snapshot.HasVerifiedAlternativeControls ||
            GetAncestor(snapshot.WindowHandle, GetAncestorRoot) != snapshot.WindowHandle ||
            !IsWindowVisible(snapshot.WindowHandle) || !IsWindowEnabled(snapshot.WindowHandle) ||
            GetForegroundWindow() != snapshot.ForegroundWindowHandle || !IsInputControlFocused(snapshot) ||
            !TryGetWindowClass(snapshot.WindowHandle, out var dialogClass) || dialogClass != "#32770" ||
            !TryGetWindowTextAllowEmpty(snapshot.WindowHandle, out var title)) return false;
        GetWindowThreadProcessId(snapshot.WindowHandle, out var processId);
        if (processId != (uint)snapshot.ProcessId) return false;
        var controls = CaptureDescendantControls(
            snapshot.WindowHandle,
            includeButtonText:
                snapshot.DialogEnterLayout == NativePromptDialogEnterLayout.CaptionlessCcbKeyboard);
        NativePromptControlSnapshot? input = null;
        var layoutMatches = snapshot.DialogEnterLayout switch
        {
            NativePromptDialogEnterLayout.TitledPasswordEdit =>
                NativePromptWindowMatcher.TryFindDialogEnterInput(
                    controls, snapshot.ProcessId, title, out input),
            NativePromptDialogEnterLayout.CaptionlessCcbKeyboard =>
                NativePromptWindowMatcher.TryFindCaptionlessCcbKeyboardInput(
                    controls, snapshot.ProcessId, title, out input),
            _ => false
        };
        if (!layoutMatches) return false;
        return input!.WindowHandle == snapshot.InputControlHandle &&
            input.ControlId == snapshot.InputControlId && input.WindowClass == snapshot.InputWindowClass;
    }

    private static NativePromptWindowSnapshot? CaptureWindowSnapshot(
        IntPtr windowHandle,
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId,
        string currentUserSid,
        int currentSessionId)
    {
        if (IsTrustedClientDescendant(policy))
        {
            return CaptureTrustedClientDescendantSnapshot(
                windowHandle,
                policy,
                trustedClientProcessId,
                currentUserSid,
                currentSessionId);
        }

        if (IsTrustedClientTopLevel(policy))
        {
            return CaptureTrustedClientTopLevelSnapshot(
                windowHandle,
                policy,
                trustedClientProcessId,
                currentUserSid,
                currentSessionId);
        }

        try
        {
            if (windowHandle == IntPtr.Zero ||
                GetAncestor(windowHandle, GetAncestorRoot) != windowHandle ||
                !IsWindowVisible(windowHandle) ||
                !IsWindowEnabled(windowHandle) ||
                !TryGetWindowText(windowHandle, out var windowTitle) ||
                !TryGetWindowClass(windowHandle, out var windowClass) ||
                !TryGetDirectDescendantClassAncestry(
                    windowHandle,
                    policy.RequiredChildWindowClasses,
                    out var childAncestry))
            {
                return null;
            }

            _ = GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
            if (nativeProcessId is 0 or > int.MaxValue)
            {
                return null;
            }

            var processId = checked((int)nativeProcessId);
            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                nativeProcessId);
            if (process.IsInvalid ||
                !TryGetProcessPath(process, out var processPath) ||
                !TryGetProcessSecurity(process, out var ownerSid, out var integrityRid) ||
                !ProcessIdToSessionId(nativeProcessId, out var nativeSessionId) ||
                nativeSessionId > int.MaxValue ||
                !TryComputeSha256(processPath, out var processSha256) ||
                !TryVerifyAuthenticode(processPath, out var signerThumbprint))
            {
                return null;
            }

            return new NativePromptWindowSnapshot(
                WindowHandle: windowHandle,
                IsTopLevel: true,
                IsVisible: true,
                IsEnabled: true,
                WindowTitle: windowTitle,
                WindowClass: windowClass,
                DirectDescendantClassAncestry: childAncestry,
                ProcessId: processId,
                ProcessPath: processPath,
                ProcessSha256: processSha256,
                IsAuthenticodeValid: true,
                SignerCertificateThumbprint: signerThumbprint,
                ProcessOwnerSid: ownerSid,
                CurrentUserSid: currentUserSid,
                ProcessSessionId: checked((int)nativeSessionId),
                CurrentInteractiveSessionId: currentSessionId,
                IntegrityRid: integrityRid);
        }
        catch
        {
            return null;
        }
    }

    private static NativePromptWindowSnapshot? CaptureTrustedClientDescendantSnapshot(
        IntPtr windowHandle,
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId,
        string currentUserSid,
        int currentSessionId)
    {
        try
        {
            if (trustedClientProcessId <= 0 ||
                windowHandle == IntPtr.Zero ||
                !IsWindowVisible(windowHandle) ||
                !IsWindowEnabled(windowHandle) ||
                !TryGetWindowTextAllowEmpty(windowHandle, out var windowTitle) ||
                !TryGetWindowClass(windowHandle, out var windowClass) ||
                !string.Equals(windowClass, policy.WindowClass, StringComparison.Ordinal))
            {
                return null;
            }

            var ancestorRoot = GetAncestor(windowHandle, GetAncestorRoot);
            var isOwnedTopLevel = ancestorRoot == windowHandle;
            var rootWindow = isOwnedTopLevel
                ? GetWindow(windowHandle, GetWindowOwner)
                : ancestorRoot;
            if (rootWindow == IntPtr.Zero ||
                rootWindow == windowHandle ||
                !IsWindowVisible(rootWindow) ||
                (!isOwnedTopLevel && !IsWindowEnabled(rootWindow)) ||
                (isOwnedTopLevel && GetAncestor(rootWindow, GetAncestorRoot) != rootWindow) ||
                !TryGetWindowText(rootWindow, out var rootWindowTitle) ||
                !TryGetWindowClass(rootWindow, out var rootWindowClass))
            {
                return null;
            }

            GetWindowThreadProcessId(rootWindow, out var nativeRootProcessId);
            if (nativeRootProcessId != checked((uint)trustedClientProcessId) ||
                !string.Equals(
                    rootWindowTitle,
                    policy.TrustedRootWindowTitle,
                    StringComparison.Ordinal) ||
                string.IsNullOrEmpty(policy.TrustedRootWindowClassPrefix) ||
                !rootWindowClass.StartsWith(
                    policy.TrustedRootWindowClassPrefix,
                    StringComparison.Ordinal))
            {
                return null;
            }

            GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
            if (nativeProcessId is 0 or > int.MaxValue)
            {
                return null;
            }

            if (!TryFindUniqueDescendantControl(
                    windowHandle,
                    nativeProcessId,
                    policy.InputWindowClass,
                    policy.InputControlId,
                    requireEnabled: true,
                    out var inputControl) ||
                !TryFindUniqueDescendantControl(
                    windowHandle,
                    nativeProcessId,
                    policy.SubmitWindowClass,
                    policy.SubmitControlId,
                    requireEnabled: false,
                    out var submitControl) ||
                inputControl == submitControl)
            {
                return null;
            }

            var processId = checked((int)nativeProcessId);
            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                nativeProcessId);
            if (process.IsInvalid ||
                !TryGetProcessPath(process, out var processPath) ||
                !TryGetProcessSecurity(process, out var ownerSid, out var integrityRid) ||
                !ProcessIdToSessionId(nativeProcessId, out var nativeSessionId) ||
                nativeSessionId > int.MaxValue ||
                !TryComputeSha256(processPath, out var processSha256) ||
                !TryVerifyAuthenticode(processPath, out var signerThumbprint))
            {
                return null;
            }

            return new NativePromptWindowSnapshot(
                WindowHandle: windowHandle,
                IsTopLevel: isOwnedTopLevel,
                IsVisible: true,
                IsEnabled: true,
                WindowTitle: windowTitle,
                WindowClass: windowClass,
                DirectDescendantClassAncestry: [],
                ProcessId: processId,
                ProcessPath: processPath,
                ProcessSha256: processSha256,
                IsAuthenticodeValid: true,
                SignerCertificateThumbprint: signerThumbprint,
                ProcessOwnerSid: ownerSid,
                CurrentUserSid: currentUserSid,
                ProcessSessionId: checked((int)nativeSessionId),
                CurrentInteractiveSessionId: currentSessionId,
                IntegrityRid: integrityRid,
                RootWindowHandle: rootWindow,
                RootProcessId: checked((int)nativeRootProcessId),
                RootWindowTitle: rootWindowTitle,
                RootWindowClass: rootWindowClass,
                InputControlHandle: inputControl,
                InputWindowClass: policy.InputWindowClass ?? string.Empty,
                InputControlId: policy.InputControlId ?? 0,
                SubmitControlHandle: submitControl,
                SubmitWindowClass: policy.SubmitWindowClass ?? string.Empty,
                SubmitControlId: policy.SubmitControlId ?? 0);
        }
        catch
        {
            return null;
        }
    }

    private static NativePromptWindowSnapshot? CaptureTrustedClientTopLevelSnapshot(
        IntPtr windowHandle,
        ClientCertificateNativePromptOptions policy,
        int trustedClientProcessId,
        string currentUserSid,
        int currentSessionId)
    {
        try
        {
            if (trustedClientProcessId <= 0 ||
                windowHandle == IntPtr.Zero ||
                GetAncestor(windowHandle, GetAncestorRoot) != windowHandle ||
                !IsWindowVisible(windowHandle) ||
                !IsWindowEnabled(windowHandle) ||
                !TryGetWindowTextAllowEmpty(windowHandle, out var windowTitle) ||
                !TryGetWindowClass(windowHandle, out var windowClass) ||
                !string.Equals(windowClass, policy.WindowClass, StringComparison.Ordinal))
            {
                return null;
            }

            GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
            if (nativeProcessId is 0 or > int.MaxValue)
            {
                return null;
            }

            var processId = checked((int)nativeProcessId);
            var controls = CaptureDescendantControls(windowHandle, includeButtonText: true);
            var usesDialogEnter = false;
            var dialogEnterLayout = NativePromptDialogEnterLayout.None;
            if (!NativePromptWindowMatcher.TryFindTrustedClientTopLevelControls(
                    controls,
                    processId, policy, out var input, out var submit, out var verifiedAlternative,
                    requireCaptionlessEvidence: windowTitle.Length == 0))
            {
                if (policy.InputWindowClass != "Edit" || policy.SubmitWindowClass != "Button")
                {
                    return null;
                }

                if (NativePromptWindowMatcher.TryFindDialogEnterInput(
                        controls, processId, windowTitle, out input))
                {
                    dialogEnterLayout = NativePromptDialogEnterLayout.TitledPasswordEdit;
                }
                else if (NativePromptWindowMatcher.TryFindCaptionlessCcbKeyboardInput(
                             controls, processId, windowTitle, out input))
                {
                    dialogEnterLayout = NativePromptDialogEnterLayout.CaptionlessCcbKeyboard;
                }
                else
                {
                    return null;
                }

                usesDialogEnter = true;
                verifiedAlternative = true;
            }
            if (input is null || (!usesDialogEnter && submit is null)) return null;
            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                nativeProcessId);
            if (process.IsInvalid)
            {
                return null;
            }

            var parentProcessId = 0;
            if (processId == trustedClientProcessId)
            {
                _ = TryGetParentProcessId(process, out parentProcessId);
            }
            else if (!TryGetParentProcessId(process, out parentProcessId) ||
                     parentProcessId != trustedClientProcessId)
            {
                return null;
            }

            if (!TryGetProcessPath(process, out var processPath) ||
                !TryGetProcessSecurity(process, out var ownerSid, out var integrityRid) ||
                !ProcessIdToSessionId(nativeProcessId, out var nativeSessionId) ||
                nativeSessionId > int.MaxValue ||
                !TryComputeSha256(processPath, out var processSha256))
            {
                return null;
            }

            return new NativePromptWindowSnapshot(
                WindowHandle: windowHandle,
                IsTopLevel: true,
                IsVisible: true,
                IsEnabled: true,
                WindowTitle: windowTitle,
                WindowClass: windowClass,
                DirectDescendantClassAncestry: [],
                ProcessId: processId,
                ProcessPath: processPath,
                ProcessSha256: processSha256,
                IsAuthenticodeValid: false,
                SignerCertificateThumbprint: string.Empty,
                ProcessOwnerSid: ownerSid,
                CurrentUserSid: currentUserSid,
                ProcessSessionId: checked((int)nativeSessionId),
                CurrentInteractiveSessionId: currentSessionId,
                IntegrityRid: integrityRid,
                RootWindowHandle: windowHandle,
                RootProcessId: processId,
                InputControlHandle: input.WindowHandle,
                InputWindowClass: input.WindowClass,
                InputControlId: input.ControlId,
                SubmitControlHandle: submit?.WindowHandle ?? IntPtr.Zero,
                SubmitWindowClass: submit?.WindowClass ?? string.Empty,
                SubmitControlId: submit?.ControlId ?? 0,
                ParentProcessId: parentProcessId,
                HasVerifiedAlternativeControls: verifiedAlternative,
                UsesDialogEnterSubmit: usesDialogEnter,
                DialogEnterLayout: dialogEnterLayout);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFindUniqueDescendantControl(
        IntPtr root,
        uint expectedProcessId,
        string? requiredClass,
        int? requiredControlId,
        bool requireEnabled,
        out IntPtr match)
    {
        match = IntPtr.Zero;
        if (string.IsNullOrEmpty(requiredClass) || requiredControlId is not > 0)
        {
            return false;
        }

        return NativePromptWindowMatcher.TryFindUniqueControl(
            CaptureDescendantControls(root, includeButtonText: false),
            checked((int)expectedProcessId),
            requiredClass,
            requiredControlId.Value,
            requireEnabled,
            out match);
    }

    private static IReadOnlyList<NativePromptControlSnapshot> CaptureDescendantControls(
        IntPtr root, bool includeButtonText)
    {
        var candidates = new List<NativePromptControlSnapshot>();
        EnumWindowsCallback callback = (candidate, parameter) =>
        {
            _ = parameter;
            GetWindowThreadProcessId(candidate, out var processId);
            if (processId <= int.MaxValue &&
                TryGetWindowClass(candidate, out var windowClass))
            {
                candidates.Add(new NativePromptControlSnapshot(
                    candidate,
                    IsWindowVisible(candidate),
                    IsWindowEnabled(candidate),
                    checked((int)processId),
                    windowClass,
                    GetDlgCtrlID(candidate),
                    includeButtonText && windowClass == "Button" && TryGetWindowText(candidate, out var buttonText)
                        ? buttonText : string.Empty,
                    IsPassword: windowClass == "Edit" && (GetWindowStyle(candidate) & 0x20) != 0));
            }

            return true;
        };
        EnumChildWindows(root, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return candidates;
    }

    private static bool IsTrustedClientDescendant(ClientCertificateNativePromptOptions policy) =>
        string.Equals(
            policy.WindowScope,
            NativePromptWindowScopes.TrustedClientDescendant,
            StringComparison.Ordinal);

    private static bool IsTrustedClientTopLevel(ClientCertificateNativePromptOptions policy) =>
        string.Equals(
            policy.WindowScope,
            NativePromptWindowScopes.TrustedClientTopLevel,
            StringComparison.Ordinal);

    private static bool TryGetCurrentIdentity(out string userSid, out int sessionId)
    {
        userSid = string.Empty;
        sessionId = -1;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            userSid = identity.User?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(userSid) ||
                !ProcessIdToSessionId(checked((uint)Environment.ProcessId), out var nativeSessionId) ||
                nativeSessionId > int.MaxValue)
            {
                userSid = string.Empty;
                return false;
            }

            sessionId = checked((int)nativeSessionId);
            return true;
        }
        catch
        {
            userSid = string.Empty;
            sessionId = -1;
            return false;
        }
    }

    private static bool TryGetWindowText(IntPtr windowHandle, out string text)
    {
        text = string.Empty;
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return false;
        }

        var buffer = new StringBuilder(checked(length + 1));
        var copied = GetWindowText(windowHandle, buffer, buffer.Capacity);
        if (copied != length)
        {
            return false;
        }

        text = buffer.ToString();
        return text.Length > 0;
    }

    private static bool TryGetWindowTextAllowEmpty(IntPtr windowHandle, out string text)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length == 0)
        {
            text = string.Empty;
            return true;
        }

        return TryGetWindowText(windowHandle, out text);
    }

    private static bool TryGetWindowClass(IntPtr windowHandle, out string windowClass)
    {
        var buffer = new StringBuilder(256);
        var copied = GetClassName(windowHandle, buffer, buffer.Capacity);
        windowClass = copied > 0 ? buffer.ToString() : string.Empty;
        return copied > 0;
    }

    private static bool TryGetDirectDescendantClassAncestry(
        IntPtr root,
        IReadOnlyList<string> requiredClasses,
        out IReadOnlyList<string> ancestry)
    {
        ancestry = [];
        if (requiredClasses is null || requiredClasses.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        var observed = new string[requiredClasses.Count];
        var parent = root;
        for (var level = 0; level < requiredClasses.Count; level++)
        {
            var match = IntPtr.Zero;
            var matchCount = 0;
            var child = GetWindow(parent, GetWindowChild);
            while (child != IntPtr.Zero)
            {
                if (GetParent(child) == parent &&
                    TryGetWindowClass(child, out var childClass) &&
                    string.Equals(childClass, requiredClasses[level], StringComparison.Ordinal))
                {
                    match = child;
                    matchCount++;
                }

                child = GetWindow(child, GetWindowNext);
            }

            if (matchCount != 1)
            {
                Array.Clear(observed, 0, observed.Length);
                return false;
            }

            observed[level] = requiredClasses[level];
            parent = match;
        }

        ancestry = observed;
        return true;
    }

    private static bool TryGetProcessPath(SafeProcessHandle process, out string processPath)
    {
        var buffer = new StringBuilder(MaximumPathCharacters);
        var length = checked((uint)buffer.Capacity);
        if (!QueryFullProcessImageName(process, 0, buffer, ref length) || length == 0)
        {
            processPath = string.Empty;
            return false;
        }

        processPath = buffer.ToString(0, checked((int)length));
        return Path.IsPathFullyQualified(processPath) &&
               !processPath.StartsWith(@"\\", StringComparison.Ordinal) &&
               !processPath.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool TryGetParentProcessId(
        SafeProcessHandle process,
        out int parentProcessId)
    {
        parentProcessId = 0;
        var informationSize = Marshal.SizeOf<ProcessBasicInformation>();
        var status = NtQueryInformationProcess(
            process,
            ProcessBasicInformationClass,
            out var information,
            informationSize,
            out var returnLength);
        if (status != 0 ||
            returnLength < informationSize)
        {
            return false;
        }

        var nativeParentProcessId = information.InheritedFromUniqueProcessId.ToUInt64();
        if (nativeParentProcessId is 0 or > int.MaxValue)
        {
            return false;
        }

        parentProcessId = checked((int)nativeParentProcessId);
        return true;
    }

    private static bool TryGetProcessSecurity(
        SafeProcessHandle process,
        out string ownerSid,
        out int integrityRid)
    {
        ownerSid = string.Empty;
        integrityRid = 0;
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            return TryGetTokenUserSid(token, out ownerSid) &&
                   TryGetTokenIntegrityRid(token, out integrityRid);
        }
    }

    private static bool TryGetTokenUserSid(SafeAccessTokenHandle token, out string sid)
    {
        sid = string.Empty;
        if (!TryGetTokenInformationBuffer(token, TokenUser, out var buffer))
        {
            return false;
        }

        try
        {
            var tokenUser = Marshal.PtrToStructure<TokenUserRecord>(buffer);
            if (tokenUser.User.Sid == IntPtr.Zero || !IsValidSid(tokenUser.User.Sid))
            {
                return false;
            }

            sid = new SecurityIdentifier(tokenUser.User.Sid).Value;
            return !string.IsNullOrEmpty(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenIntegrityRid(
        SafeAccessTokenHandle token,
        out int integrityRid)
    {
        integrityRid = 0;
        if (!TryGetTokenInformationBuffer(token, TokenIntegrityLevel, out var buffer))
        {
            return false;
        }

        try
        {
            var label = Marshal.PtrToStructure<TokenMandatoryLabelRecord>(buffer);
            if (label.Label.Sid == IntPtr.Zero || !IsValidSid(label.Label.Sid))
            {
                return false;
            }

            var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
            if (countPointer == IntPtr.Zero)
            {
                return false;
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return false;
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, checked((uint)(count - 1)));
            if (ridPointer == IntPtr.Zero)
            {
                return false;
            }

            integrityRid = Marshal.ReadInt32(ridPointer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenInformationBuffer(
        SafeAccessTokenHandle token,
        int informationClass,
        out IntPtr buffer)
    {
        buffer = IntPtr.Zero;
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var requiredLength);
        if (requiredLength <= 0)
        {
            return false;
        }

        buffer = Marshal.AllocHGlobal(requiredLength);
        if (GetTokenInformation(token, informationClass, buffer, requiredLength, out _))
        {
            return true;
        }

        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        return false;
    }

    private static bool TryComputeSha256(string processPath, out string sha256)
    {
        sha256 = string.Empty;
        try
        {
            using var stream = new FileStream(
                processPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return sha256.Length == 64;
        }
        catch
        {
            sha256 = string.Empty;
            return false;
        }
    }

    private static bool TryVerifyAuthenticode(
        string processPath,
        out string signerThumbprint)
    {
        signerThumbprint = string.Empty;
        IntPtr path = IntPtr.Zero;
        IntPtr fileInfoBuffer = IntPtr.Zero;
        var trustData = new WinTrustData();
        var verificationAttempted = false;
        try
        {
            path = Marshal.StringToCoTaskMemUni(processPath);
            var fileInfo = new WinTrustFileInfo
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = path
            };
            fileInfoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoBuffer, fDeleteOld: false);
            trustData = new WinTrustData
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoBuffer,
                StateAction = WinTrustStateActionVerify,
                ProviderFlags = WinTrustCacheOnlyUrlRetrieval
            };

            var action = WinTrustActionGenericVerifyV2;
            verificationAttempted = true;
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trustData) != 0)
            {
                return false;
            }

            if (trustData.StateData == IntPtr.Zero)
            {
                return false;
            }

            var providerData = WTHelperProvDataFromStateData(trustData.StateData);
            if (providerData == IntPtr.Zero)
            {
                return false;
            }

            var providerSigner = WTHelperGetProvSignerFromChain(
                providerData,
                signerIndex: 0,
                counterSigner: false,
                counterSignerIndex: 0);
            if (providerSigner == IntPtr.Zero)
            {
                return false;
            }

            var providerCertificate = WTHelperGetProvCertFromChain(
                providerSigner,
                certificateIndex: 0);
            if (providerCertificate == IntPtr.Zero)
            {
                return false;
            }

            var leaf = Marshal.PtrToStructure<CryptProviderCertificateHeader>(
                providerCertificate);
            if (leaf.CertificateContext == IntPtr.Zero)
            {
                return false;
            }

            using var certificate = new X509Certificate2(leaf.CertificateContext);
            signerThumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA1);
            return signerThumbprint.Length == 40;
        }
        catch
        {
            signerThumbprint = string.Empty;
            return false;
        }
        finally
        {
            if (verificationAttempted)
            {
                try
                {
                    trustData.StateAction = WinTrustStateActionClose;
                    var action = WinTrustActionGenericVerifyV2;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
                }
                catch
                {
                }
            }

            if (fileInfoBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoBuffer);
            }

            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

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
        public System.Drawing.Rectangle CaretRectangle;
    }

    private static long GetWindowStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtrW(window, -16).ToInt64() : GetWindowLongW(window, -16);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("User32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumCount);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("User32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("User32.dll")]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr windowHandle);

    [DllImport("User32.dll")]
    private static extern int GetDlgCtrlID(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("User32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("User32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint threadIdAttach,
        uint threadIdAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(
        uint threadId,
        ref GuiThreadInformation information);

    [DllImport("User32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(
        uint inputCount,
        [In] NativePromptInput[] inputs,
        int inputSize);

    [DllImport("User32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutNative(
        IntPtr windowHandle,
        uint message,
        UIntPtr wordParameter,
        IntPtr longParameter,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DllImport("Kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("Ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder processPath,
        ref uint size);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);

    [DllImport("Advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("Wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(
        IntPtr providerSigner,
        uint certificateIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributesRecord
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUserRecord
    {
        public SidAndAttributesRecord User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabelRecord
    {
        public SidAndAttributesRecord Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public UIntPtr AffinityMask;
        public int BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificateHeader
    {
        public uint Size;
        public IntPtr CertificateContext;
    }
}
