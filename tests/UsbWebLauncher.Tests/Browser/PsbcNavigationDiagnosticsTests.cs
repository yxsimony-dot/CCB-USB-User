using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PsbcNavigationDiagnosticsTests
{
    [Fact]
    public void OnlyMainDocumentFailureIsRetained()
    {
        var diagnostics = new PsbcNavigationDiagnostics("main");
        diagnostics.BeginNavigation(1);
        diagnostics.ObserveRequest(Request("main", "main-request"));
        diagnostics.ObserveRequest(Request("iframe", "child-request"));
        Assert.Null(diagnostics.ObserveFailure(Failure("child-request")));
        Assert.Null(diagnostics.ObserveFailure(Failure("main-request", type: "Image")));
        Assert.Equal("ERR_SSL_CLIENT_AUTH_SIGNATURE_FAILED",
            diagnostics.ObserveFailure(Failure("main-request")));
        Assert.Equal("ERR_SSL_CLIENT_AUTH_SIGNATURE_FAILED", diagnostics.GetError(1));
        Assert.Null(diagnostics.GetError(2));
    }

    [Fact]
    public void RedirectKeepsCurrentNavigationButNewNavigationClearsOldFailure()
    {
        var diagnostics = new PsbcNavigationDiagnostics("main");
        diagnostics.BeginNavigation(1);
        diagnostics.ObserveRequest(Request("main", "request"));
        diagnostics.BeginNavigation(1);
        diagnostics.ObserveFailure(Failure("request"));
        Assert.NotNull(diagnostics.GetError(1));
        diagnostics.BeginNavigation(2);
        Assert.Null(diagnostics.GetError(2));
        Assert.Null(diagnostics.ObserveFailure(Failure("request")));
        diagnostics.ObserveRequest(Request("main", "next-request"));
        Assert.Null(diagnostics.ObserveFailure(Failure("request")));
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("net::ERR_FAILED https://example.test/?pin=secret")]
    [InlineData("net::ERR_FAILED\nsecret")]
    [InlineData("net::ERR_FAILED_SECRET=1234")]
    [InlineData("net::err_failed")]
    public void DoesNotRetainFreeFormErrors(string error)
    {
        var diagnostics = new PsbcNavigationDiagnostics("main");
        diagnostics.BeginNavigation(1);
        diagnostics.ObserveRequest(Request("main", "request"));
        Assert.Null(diagnostics.ObserveFailure(Failure("request", error)));
        Assert.Null(diagnostics.GetError(1));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"requestId\":123,\"type\":\"Document\"}")]
    public void MalformedEventsDoNotAffectNavigation(string json)
    {
        var diagnostics = new PsbcNavigationDiagnostics("main");
        diagnostics.BeginNavigation(1);
        diagnostics.ObserveRequest(json);
        Assert.Null(diagnostics.ObserveFailure(json));
    }

    [Fact]
    public void EventsBeforeNavigationAreIgnored()
    {
        var diagnostics = new PsbcNavigationDiagnostics("main");
        diagnostics.ObserveRequest(Request("main", "request"));
        Assert.Null(diagnostics.ObserveFailure(Failure("request")));
    }

    private static string Request(string frameId, string requestId) =>
        JsonSerializer.Serialize(new { frameId, requestId, type = "Document",
            request = new { url = "https://example.test/?private=SYNTHETIC_SECRET" } });

    private static string Failure(string requestId,
        string errorText = "net::ERR_SSL_CLIENT_AUTH_SIGNATURE_FAILED", string type = "Document") =>
        JsonSerializer.Serialize(new { requestId, errorText, type });
}
