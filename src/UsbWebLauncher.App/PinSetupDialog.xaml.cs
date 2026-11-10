using System.Windows;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App;

public partial class PinSetupDialog : Window, ILocalPinDialog
{
    private readonly Func<bool?> _showDialog;

    public PinSetupDialog(Window owner)
        : this(owner, "当前银行", null)
    {
    }

    public PinSetupDialog(Window owner, string bankDisplayName)
        : this(owner, bankDisplayName, null)
    {
    }

    internal PinSetupDialog(Window? owner, Func<bool?>? showDialog)
        : this(owner, "当前银行", showDialog)
    {
    }

    internal PinSetupDialog(
        Window? owner,
        string bankDisplayName,
        Func<bool?>? showDialog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankDisplayName);
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        HeadingTextBlock.Text = $"为{bankDisplayName}设置固定本机 PIN";
        DescriptionTextBlock.Text =
            $"PIN 只会保存到{bankDisplayName}专用的当前 Windows 用户凭据；" +
            "USB Key 出现后程序会自动读取并验证。";
        _showDialog = showDialog ?? ShowDialog;
    }

    public Task<ClientCertificatePin?> ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(RequestCancellation);
        try
        {
            var accepted = _showDialog() == true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!accepted)
            {
                return Task.FromResult<ClientCertificatePin?>(null);
            }

            return Task.FromResult<ClientCertificatePin?>(
                ClientCertificatePin.CopyOf(PinTextBox.Text.AsSpan()));
        }
        finally
        {
            PinTextBox.Clear();
        }
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs eventArgs) => DialogResult = true;

    private void RequestCancellation()
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                CloseIfVisible();
            }
            else
            {
                _ = Dispatcher.BeginInvoke(CloseIfVisible);
            }
        }
        catch (InvalidOperationException)
        {
            // The dispatcher or dialog has already reached its terminal state.
        }
    }

    private void CloseIfVisible()
    {
        if (IsVisible)
        {
            Close();
        }
    }
}
