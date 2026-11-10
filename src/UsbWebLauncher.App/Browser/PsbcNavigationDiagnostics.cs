using System.Text.Json;

namespace UsbWebLauncher.App.Browser;

// Keep only the main document's opaque request ID and a validated Chromium error
// code. Never retain URLs, headers, response bodies, or certificate identities.
internal sealed class PsbcNavigationDiagnostics
{
    private readonly string _mainFrameId;
    private long? _navigationId;
    private string? _documentRequestId;
    private string? _networkError;

    public PsbcNavigationDiagnostics(string mainFrameId)
    {
        _mainFrameId = mainFrameId;
    }

    public void BeginNavigation(long navigationId)
    {
        if (_navigationId == navigationId) return;
        _navigationId = navigationId;
        _documentRequestId = null;
        _networkError = null;
    }

    public void ObserveRequest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (_navigationId is null ||
                ReadString(root, "type") != "Document" ||
                ReadString(root, "frameId") != _mainFrameId)
                return;

            _documentRequestId = ReadString(root, "requestId");
            _networkError = null;
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    public string? ObserveFailure(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (_documentRequestId is null ||
                ReadString(root, "requestId") != _documentRequestId ||
                ReadString(root, "type") != "Document")
                return null;

            var error = ReadString(root, "errorText");
            if (error is null || !error.StartsWith("net::ERR_", StringComparison.Ordinal) ||
                error.Length > 96 ||
                error.AsSpan(5).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"))
                return null;

            return _networkError = error[5..];
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public string? GetError(long navigationId) =>
        _navigationId == navigationId ? _networkError : null;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
