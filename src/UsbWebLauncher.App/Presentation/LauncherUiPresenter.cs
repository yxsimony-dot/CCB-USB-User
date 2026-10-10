using System.Windows.Threading;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Coordination;

namespace UsbWebLauncher.App.Presentation;

public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class LauncherUiPresenter(
    LauncherViewModel viewModel,
    IUiDispatcher dispatcher,
    Action activateBrowserWindow,
    Func<bool>? isCurrent = null)
{
    public void Present(LauncherState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        dispatcher.Post(() =>
        {
            if (isCurrent?.Invoke() == false) return;
            viewModel.UpdateState(state);
            if (state.Stage == LauncherStage.UsbDetected)
            {
                activateBrowserWindow();
            }
        });
    }

    public void PresentAuthentication(AuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        dispatcher.Post(() =>
        {
            if (isCurrent?.Invoke() != false) viewModel.UpdateAuthenticationState(state);
        });
    }
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => _ = dispatcher.InvokeAsync(action);
}
