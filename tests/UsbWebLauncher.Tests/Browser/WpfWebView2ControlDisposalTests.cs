using Microsoft.Web.WebView2.Wpf;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class WpfWebView2ControlDisposalTests
{
    [Fact]
    public async Task DisposingWrapperActuallyDisposesOwnedWebViewAndAllowsRepeatedDispose()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try
            {
                using var control = new WebView2();
                var wrapper = new WpfWebView2Control(control);
                control.GoBack(); // Uninitialized but live controls accept this without navigation.
                await wrapper.DisposeAsync();
                await wrapper.DisposeAsync();
                Assert.Throws<ObjectDisposedException>(() => control.GoBack());
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
