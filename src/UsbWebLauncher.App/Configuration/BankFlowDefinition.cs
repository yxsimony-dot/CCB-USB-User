namespace UsbWebLauncher.App.Configuration;

public enum BankFlowKind
{
    ChinaConstructionBank,
    PostalSavingsBank
}

public enum BankWebView2EnvironmentKind
{
    Standard,
    ExtensionEnabled
}

public sealed record BankBrowserCapabilities(
    BankWebView2EnvironmentKind EnvironmentKind,
    bool InstallCcbUKeyExtension,
    bool EnableCcbPageCoordination,
    bool EnablePsbcClientCertificateHandling,
    string SessionDirectoryName)
{
    public static BankBrowserCapabilities ChinaConstructionBank { get; } = new(
        BankWebView2EnvironmentKind.ExtensionEnabled,
        InstallCcbUKeyExtension: true,
        EnableCcbPageCoordination: true,
        EnablePsbcClientCertificateHandling: false,
        SessionDirectoryName: "ccb");

    public static BankBrowserCapabilities PostalSavingsBank { get; } = new(
        BankWebView2EnvironmentKind.Standard,
        InstallCcbUKeyExtension: false,
        EnableCcbPageCoordination: false,
        EnablePsbcClientCertificateHandling: true,
        SessionDirectoryName: "psbc");
}

public sealed record BankFlowDefinition(
    BankFlowKind Kind,
    string DisplayName,
    string? ProfileFileName,
    string UnavailableStatusText,
    BankBrowserCapabilities BrowserCapabilities)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProfileFileName);
}

public static class BankFlowCatalog
{
    public static BankFlowDefinition ChinaConstructionBank { get; } = new(
        BankFlowKind.ChinaConstructionBank,
        "建设银行",
        "launcher-profile.json",
        string.Empty,
        BankBrowserCapabilities.ChinaConstructionBank);

    public static BankFlowDefinition PostalSavingsBank { get; } = new(
        BankFlowKind.PostalSavingsBank,
        "邮储银行",
        "psbc-launcher-profile.json",
        string.Empty,
        BankBrowserCapabilities.PostalSavingsBank);

    public static IReadOnlyList<BankFlowDefinition> All { get; } =
        Array.AsReadOnly([ChinaConstructionBank, PostalSavingsBank]);
}

public sealed class BankFlowChangedEventArgs(
    BankFlowDefinition previous,
    BankFlowDefinition current) : EventArgs
{
    public BankFlowDefinition Previous { get; } = previous;
    public BankFlowDefinition Current { get; } = current;
}
