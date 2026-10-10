namespace UsbWebLauncher.Tests.Windows;

internal static class HardwareTestGate
{
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("USB_WEB_LAUNCHER_RUN_HARDWARE_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            Environment.GetEnvironmentVariable("USB_WEB_LAUNCHER_RUN_HARDWARE_TESTS"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("USB_WEB_LAUNCHER_HARDWARE_TEST"),
            "1",
            StringComparison.Ordinal);
}
