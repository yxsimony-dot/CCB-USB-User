using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Browser;

public delegate ValueTask<WebClientCertificateSelection> WebClientCertificateRequestHandler(
    WebClientCertificateRequest request,
    CancellationToken cancellationToken);

public sealed class WebClientCertificate
{
    internal WebClientCertificate(
        string sha1Fingerprint,
        string subject,
        string issuer,
        object nativeCertificate)
        : this(sha1Fingerprint, subject, issuer)
    {
        NativeCertificate = nativeCertificate;
    }

    public WebClientCertificate(string sha1Fingerprint, string subject, string issuer)
    {
        Candidate = new ClientCertificateCandidate(sha1Fingerprint, subject, issuer);
    }

    internal object? NativeCertificate { get; }

    internal ClientCertificateCandidate Candidate { get; }
}

public sealed class WebClientCertificateRequest
{
    public WebClientCertificateRequest(
        Uri requestUri,
        bool isProxyRequest,
        IEnumerable<WebClientCertificate> availableCertificates)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(availableCertificates);

        RequestUri = requestUri;
        IsProxyRequest = isProxyRequest;
        AvailableCertificates = availableCertificates.ToArray();
    }

    public Uri RequestUri { get; }

    public bool IsProxyRequest { get; }

    public IReadOnlyList<WebClientCertificate> AvailableCertificates { get; }

    internal ClientCertificateRequestSnapshot ToSnapshot(long browserOperationId) => new(
        RequestUri,
        IsProxyRequest,
        AvailableCertificates.Select(certificate => certificate.Candidate),
        browserOperationId);
}

public sealed record WebClientCertificateSelection(
    bool Handled,
    WebClientCertificate? SelectedCertificate,
    ClientCertificateSelectionAuthorization? Authorization = null)
{
    public static WebClientCertificateSelection Deny() => new(true, null);

    public static WebClientCertificateSelection PassThrough(
        ClientCertificateSelectionAuthorization authorization) =>
        new(false, null, authorization);

    public static WebClientCertificateSelection Select(
        WebClientCertificate certificate,
        ClientCertificateSelectionAuthorization authorization) =>
        new(true, certificate, authorization);
}

public static class WebClientCertificateRequestDeferralDispatcher
{
    public static async ValueTask DispatchAsync(
        WebClientCertificateRequest request,
        WebClientCertificateRequestHandler handler,
        Action<WebClientCertificateSelection> applySelection,
        Action deny,
        Action markHandled,
        Action completeDeferral,
        CancellationToken cancellationToken)
    {
        await DispatchAsync(
            () => completeDeferral,
            () => request,
            handler,
            applySelection,
            deny,
            markHandled,
            cancellationToken);
    }

    public static async ValueTask DispatchAsync(
        Func<Action> acquireDeferralCompletion,
        Func<WebClientCertificateRequest> createRequest,
        WebClientCertificateRequestHandler handler,
        Action<WebClientCertificateSelection> applySelection,
        Action deny,
        Action markHandled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acquireDeferralCompletion);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(applySelection);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(markHandled);
        Action? completeDeferral = null;
        var shouldMarkHandled = false;

        try
        {
            completeDeferral = acquireDeferralCompletion();
            cancellationToken.ThrowIfCancellationRequested();
            var request = createRequest();
            // The caller may apply a WebView2 COM object after the handler returns;
            // preserve the UI synchronization context for that operation.
            var selection = await handler(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!selection.Handled)
            {
                if (selection.SelectedCertificate is not null ||
                    selection.Authorization is null ||
                    !selection.Authorization.TryCommit(static () => { }))
                {
                    deny();
                    shouldMarkHandled = true;
                }
            }
            else
            {
                shouldMarkHandled = true;
                if (selection.SelectedCertificate is null ||
                    selection.Authorization is null ||
                    !selection.Authorization.TryCommit(() => applySelection(selection)))
                {
                    deny();
                }
            }
        }
        catch
        {
            shouldMarkHandled = true;
            try
            {
                deny();
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                if (shouldMarkHandled)
                {
                    markHandled();
                }
            }
            catch
            {
            }

            finally
            {
                if (completeDeferral is not null)
                {
                    try
                    {
                        completeDeferral();
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
