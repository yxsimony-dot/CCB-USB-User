using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Composition;

internal sealed record PsbcAuthenticationLogEntry(
    string Stage,
    string BankId,
    string? VendorFamily,
    string ErrorCategory,
    double ElapsedMilliseconds);

internal sealed class PsbcAuthenticationDiagnosticsObserver
    : IPsbcAuthenticationObserver
{
    private readonly Action<string, object?> writer;

    public PsbcAuthenticationDiagnosticsObserver()
        : this(CertificateLoginDiagnostics.Write)
    {
    }

    internal PsbcAuthenticationDiagnosticsObserver(
        Action<string, object?> writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Record(PsbcAuthenticationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        try
        {
            writer(
                "psbc-authentication",
                new PsbcAuthenticationLogEntry(
                    observation.Stage.ToString(),
                    observation.BankId,
                    observation.VendorFamily?.ToString(),
                    observation.ErrorCategory.ToString(),
                    Math.Max(0, observation.Elapsed.TotalMilliseconds)));
        }
        catch
        {
            // Ordinary logging must not affect the authentication state machine.
        }
    }
}
