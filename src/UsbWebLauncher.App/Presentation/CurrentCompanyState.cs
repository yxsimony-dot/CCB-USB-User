namespace UsbWebLauncher.App.Presentation;

// A read belongs to one connection/navigation epoch. Never retain a name across it.
public sealed class CurrentCompanyState
{
    private long epoch;
    private long? navigationEpoch;
    private bool devicePresent;
    private bool canRead;
    private bool navigationCanRetainDocument;
    public string Text { get; private set; } = "启动业务并登录后显示";

    public void SetDevicePresent(bool present)
    {
        if (present == devicePresent) return;
        devicePresent = present;
        epoch++;
        navigationEpoch = null;
        navigationCanRetainDocument = false;
        canRead = false;
        Text = present ? "登录后自动识别" : "USB Key 未连接";
    }

    public void NavigationStarting()
    {
        epoch++;
        navigationCanRetainDocument = canRead;
        navigationEpoch = devicePresent ? epoch : null;
        canRead = false;
        Text = devicePresent ? "登录后自动识别" : "USB Key 未连接";
    }

    public void NavigationCompleted(bool trustedAndSuccessful, bool documentRetained = false)
    {
        canRead = devicePresent && navigationEpoch == epoch &&
            (trustedAndSuccessful || documentRetained && navigationCanRetainDocument);
    }

    public long? BeginRead() => canRead ? epoch : null;

    public void Apply(long ticket, string? status, string? companyName)
    {
        if (!canRead || ticket != epoch) return;
        var name = companyName?.Trim();
        Text = status == "ready" && !string.IsNullOrWhiteSpace(name) && name.Length <= 232 &&
            !name.Any(char.IsControl) && !name.Contains('…') && !name.Contains("...")
            ? name : status == "ambiguous" ? "存在多个公司，暂无法确认" : "登录后自动识别";
    }
}
