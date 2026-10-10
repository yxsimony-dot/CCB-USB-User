namespace UsbWebLauncher.Core.Coordination;

public enum LauncherStage
{
    WaitingForUsb,
    UsbDetected,
    OpeningBrowser,
    Ready,
    Error
}

public sealed record LauncherState(
    LauncherStage Stage,
    bool IsUsbPresent,
    string Message,
    string? ErrorDetail = null)
{
    public bool IsBrowserVisible => Stage is LauncherStage.OpeningBrowser or LauncherStage.Ready;

    public bool CanRetry => Stage == LauncherStage.Error;
}
