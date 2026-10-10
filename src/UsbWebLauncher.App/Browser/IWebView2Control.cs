namespace UsbWebLauncher.App.Browser;

public interface IWebView2Control : IAsyncDisposable
{
    event EventHandler<WebNavigationStartingEventArgs>? NavigationStarting;

    event EventHandler<WebNavigationCompletedEventArgs>? NavigationCompleted;

    Task EnsureInitializedAsync(CancellationToken cancellationToken);

    Task RegisterClientCertificateRequestHandlerAsync(
        WebClientCertificateRequestHandler handler,
        CancellationToken cancellationToken);

    Task<long> StartNavigationAsync(Uri target, CancellationToken cancellationToken);

    Task StopNavigationAsync(CancellationToken cancellationToken);
}

public sealed class WebNavigationStartingEventArgs(
    long navigationId,
    bool isUserInitiated,
    Uri? targetUri = null) : EventArgs
{
    private readonly TaskCompletionSource<Task> _authenticationBinding =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long NavigationId { get; } = navigationId;

    public bool IsUserInitiated { get; } = isUserInitiated;

    public Uri? TargetUri { get; } = targetUri;

    internal Task AuthenticationBindingTask => _authenticationBinding.Task.Unwrap();

    public void BindAuthentication(Task authenticationBinding)
    {
        ArgumentNullException.ThrowIfNull(authenticationBinding);
        _authenticationBinding.TrySetResult(authenticationBinding);
    }

    internal void CompleteWithoutAuthenticationBinding()
    {
        _authenticationBinding.TrySetResult(Task.FromException(
            new InvalidOperationException(
                "The browser navigation was not bound to an authentication operation.")));
    }
}

public sealed class WebNavigationCompletedEventArgs(
    long navigationId,
    bool isSuccess,
    string? errorStatus = null) : EventArgs
{
    public long NavigationId { get; } = navigationId;

    public bool IsSuccess { get; } = isSuccess;

    public string? ErrorStatus { get; } = errorStatus;
}
