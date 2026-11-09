using System.Reflection;
using System.Text.Json;
using UsbWebLauncher.App.Composition;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Composition;

public sealed class PsbcAuthenticationDiagnosticsObserverTests
{
    [Fact]
    public void Record_WritesOnlyApprovedOrdinaryLogFields()
    {
        string? eventName = null;
        object? data = null;
        var observer = new PsbcAuthenticationDiagnosticsObserver(
            (observedEventName, observedData) =>
            {
                eventName = observedEventName;
                data = observedData;
            });

        observer.Record(new(
            AuthenticationStage.Failed,
            "psbc",
            UsbKeyVendorFamily.Tendyron,
            AuthenticationErrorCategory.CertificateConflict,
            TimeSpan.FromMilliseconds(125)));

        Assert.Equal("psbc-authentication", eventName);
        var entry = Assert.IsType<PsbcAuthenticationLogEntry>(data);
        Assert.Equal("Failed", entry.Stage);
        Assert.Equal("psbc", entry.BankId);
        Assert.Equal("Tendyron", entry.VendorFamily);
        Assert.Equal("CertificateConflict", entry.ErrorCategory);
        Assert.Equal(125, entry.ElapsedMilliseconds);
        Assert.Equal(
            new[]
            {
                nameof(PsbcAuthenticationLogEntry.BankId),
                nameof(PsbcAuthenticationLogEntry.ElapsedMilliseconds),
                nameof(PsbcAuthenticationLogEntry.ErrorCategory),
                nameof(PsbcAuthenticationLogEntry.Stage),
                nameof(PsbcAuthenticationLogEntry.VendorFamily)
            },
            entry.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .OrderBy(name => name));

        var output = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("TokenIdentity", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Subject", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Serial", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fingerprint", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Window", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Control", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PinLength", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Record_IgnoresWriterFailure()
    {
        var observer = new PsbcAuthenticationDiagnosticsObserver(
            (_, _) => throw new IOException("Synthetic log failure."));

        observer.Record(new(
            AuthenticationStage.AwaitingCertificate,
            "psbc",
            UsbKeyVendorFamily.Tendyron,
            AuthenticationErrorCategory.None,
            TimeSpan.Zero));
    }
}
