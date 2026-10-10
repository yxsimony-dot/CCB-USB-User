namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal sealed record NativePromptWindowSnapshot(
    IntPtr WindowHandle,
    bool IsTopLevel,
    bool IsVisible,
    bool IsEnabled,
    string WindowTitle,
    string WindowClass,
    IReadOnlyList<string> DirectDescendantClassAncestry,
    int ProcessId,
    string ProcessPath,
    string ProcessSha256,
    bool IsAuthenticodeValid,
    string SignerCertificateThumbprint,
    string ProcessOwnerSid,
    string CurrentUserSid,
    int ProcessSessionId,
    int CurrentInteractiveSessionId,
    int IntegrityRid,
    IntPtr RootWindowHandle = default,
    int RootProcessId = 0,
    string RootWindowTitle = "",
    string RootWindowClass = "",
    IntPtr InputControlHandle = default,
    string InputWindowClass = "",
    int InputControlId = 0,
    IntPtr SubmitControlHandle = default,
    string SubmitWindowClass = "",
    int SubmitControlId = 0,
    int ParentProcessId = 0,
    bool HasVerifiedAlternativeControls = false,
    bool UsesDialogEnterSubmit = false)
{
    public IntPtr ForegroundWindowHandle =>
        IsTopLevel || RootWindowHandle == IntPtr.Zero ? WindowHandle : RootWindowHandle;
}
