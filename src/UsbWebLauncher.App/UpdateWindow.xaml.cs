using System.Windows;
using UsbWebLauncher.App.Updates;

namespace UsbWebLauncher.App;

public partial class UpdateWindow : Window
{
    private readonly UpdatePresentationController _updates;

    public UpdateWindow(UpdatePresentationController updates)
    {
        InitializeComponent();
        _updates = updates;
        DataContext = updates;
        Closed += (_, _) =>
        {
            DataContext = null;
            _updates.CancelPending();
        };
    }

    private async void OnCheckClicked(object sender, RoutedEventArgs e) => await _updates.CheckAsync();
    private async void OnDownloadClicked(object sender, RoutedEventArgs e) => await _updates.DownloadAsync();
    private async void OnInstallClicked(object sender, RoutedEventArgs e) => await _updates.InstallAsync();
    private void OnCancelClicked(object sender, RoutedEventArgs e) => _updates.CancelPending();
}
