using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Browser;

internal interface IPsbcPageLoginInput
{
    Task<bool> PrepareAsync(CancellationToken token);
    Task<string?> CheckAsync(int length, CancellationToken token);
    bool Send(ClientCertificatePin pin, CancellationToken token);
    Task<string?> SubmitAsync(int length, CancellationToken token);
    Task ReleaseAsync();
}

internal static class PsbcPageLoginSubmission
{
    internal static async Task<CertificateLoginResult> RunAsync(IPsbcPageLoginInput input,
        ClientCertificatePin pin, CancellationToken token,
        TimeSpan? acknowledgementTimeout = null, TimeSpan? activationDelay = null)
    {
        var length = pin.AsMemory().Length;
        if (length is < 6 or > 20) return CertificateLoginResult.Unsupported;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!await input.PrepareAsync(token)) return CertificateLoginResult.Failed;
            // The focus handler starts the bank's asynchronous local input service.
            await Task.Delay(activationDelay ?? TimeSpan.FromMilliseconds(350), token);
            if (await input.CheckAsync(length, token) != "empty") return CertificateLoginResult.Failed;
            token.ThrowIfCancellationRequested();
            if (!input.Send(pin, token)) return CertificateLoginResult.Failed;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(acknowledgementTimeout ?? TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var status = await input.CheckAsync(length, deadline.Token);
                    if (status == "filled") break;
                    if (status is not ("empty" or "pending")) return CertificateLoginResult.Failed;
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                return CertificateLoginResult.Failed;
            }
            token.ThrowIfCancellationRequested();
            return await input.SubmitAsync(length, token) == "submitted"
                ? CertificateLoginResult.Submitted : CertificateLoginResult.Failed;
        }
        finally
        {
            // Never resend keys, including after partial input or ambiguous completion.
            try { await input.ReleaseAsync(); }
            catch { }
        }
    }
}
