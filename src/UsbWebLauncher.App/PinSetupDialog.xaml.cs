using System.Windows;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App;

public partial class PinSetupDialog : Window, ILocalPinDialog
{
    private readonly Func<bool?> _showDialog;

    public PinSetupDialog(Window owner)
        : this(owner, null)
    {
    }

    internal PinSetupDialog(Window? owner, Func<bool?>? showDialog)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

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

            using var securePassword = PinPasswordBox.SecurePassword;
            return Task.FromResult<ClientCertificatePin?>(
                SecurePasswordPinReader.CopyFrom(securePassword));
        }
        finally
        {
            PinPasswordBox.Clear();
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
