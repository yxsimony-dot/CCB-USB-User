namespace UsbWebLauncher.Core.Browser;

using UsbWebLauncher.Core.Authentication;

public interface IBrowserHost
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task NavigateAsync(Uri target, CancellationToken cancellationToken = default);
}

public interface IBrowserHostAvailability
{
    bool IsAvailable { get; }
}

public interface ICorrelatedBrowserHost : IBrowserHost
{
    Task NavigateAsync(
        Uri target,
        long browserOperationId,
        CancellationToken cancellationToken = default);
}

public interface IClientCertificateConfigurableBrowserHost : ICorrelatedBrowserHost, IAsyncDisposable
{
    Task ConfigureClientCertificateAuthenticationAsync(
        IBrowserClientCertificateRequestHandler handler,
        IBrowserClientCertificateAuthenticationReporter reporter,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public static class BrowserOperationIdentity
{
    private static long s_next;

    public static long Create()
    {
        var operationId = Interlocked.Increment(ref s_next);
        if (operationId <= 0)
        {
            throw new InvalidOperationException("Browser operation identities were exhausted.");
        }

        return operationId;
    }
}
