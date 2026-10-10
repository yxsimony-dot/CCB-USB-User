using System.Windows;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App;

public partial class PinSetupDialog : Window, ILocalPinDialog
{
    public PinSetupDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    public Task<ClientCertificatePin?> ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = ShowDialog() == true;
        if (!accepted)
        {
            PinTextBox.Clear();
            return Task.FromResult<ClientCertificatePin?>(null);
        }

        var pin = ClientCertificatePin.CopyOf(PinTextBox.Text.AsSpan());
        try
        {
            return Task.FromResult<ClientCertificatePin?>(pin);
        }
        finally
        {
            PinTextBox.Clear();
        }
    }

    private void OnConfirmClicked(object sender, RoutedEventArgs eventArgs) => DialogResult = true;
}
