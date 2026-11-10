using System.IO;
using System.Text.Json;

namespace UsbWebLauncher.App.Browser;

internal static class PsbcInputTrace
{
    private static readonly object LogSync = new();

    internal static bool ManualObservation { get; } =
        IsManualMode(Environment.GetEnvironmentVariable("USBWEBLAUNCHER_PSBC_INPUT_TRACE"));

    internal static bool IsManualMode(string? value) =>
        string.Equals(value, "manual", StringComparison.Ordinal);

    internal static void Write(string eventName, object data)
    {
        if (!ManualObservation) return;
        lock (LogSync)
        {
            try
            {
                Directory.CreateDirectory(CertificateLoginDiagnostics.LogDirectory);
                var path = Path.Combine(CertificateLoginDiagnostics.LogDirectory,
                    $"psbc-real-input-{Environment.ProcessId}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) return;
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now, eventName, data
                }) + Environment.NewLine);
            }
            catch { }
        }
    }

    private static readonly HashSet<string> EventTypes =
    [
        "state", "pointerdown", "pointerup", "mousedown", "mouseup", "click",
        "focus", "blur", "keydown", "keyup", "keypress", "beforeinput", "input",
        "compositionstart", "compositionupdate", "compositionend", "paste"
    ];

    internal sealed record Sample(string EventType, int ElapsedMs, int MaskLength,
        bool DocumentFocused, bool FieldFocused, bool Trusted, int Connected,
        int Stopped, int Busy);

    // Reconstruct an allowlisted payload instead of ever logging page-supplied JSON.
    internal static IReadOnlyList<Sample> Parse(string json)
    {
        if (json.Length > 65536) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 128) return [];
            var samples = new List<Sample>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Count() != 9 ||
                    !item.TryGetProperty("eventType", out var kind) ||
                    kind.ValueKind != JsonValueKind.String ||
                    kind.GetString() is not { } eventType || !EventTypes.Contains(eventType) ||
                    !Integer(item, "elapsedMs", 0, 600000, out var elapsed) ||
                    !Integer(item, "maskLength", -1, 64, out var length) ||
                    !Boolean(item, "documentFocused", out var documentFocused) ||
                    !Boolean(item, "fieldFocused", out var fieldFocused) ||
                    !Boolean(item, "trusted", out var trusted) ||
                    !Integer(item, "connected", -1, 1, out var connected) ||
                    !Integer(item, "stopped", -1, 1, out var stopped) ||
                    !Integer(item, "busy", -1, 1, out var busy)) continue;
                samples.Add(new(eventType, elapsed, length, documentFocused,
                    fieldFocused, trusted, connected, stopped, busy));
            }
            return samples;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Integer(JsonElement item, string name, int min, int max, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value) &&
            value >= min && value <= max;
    }

    private static bool Boolean(JsonElement item, string name, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }
}

internal sealed class PsbcInputTraceNavigation
{
    private ulong? _navigationId;
    private bool _loaded;

    internal void Starting(ulong navigationId)
    {
        _navigationId = navigationId;
        _loaded = false;
    }

    internal void Completed(ulong navigationId, bool success, int httpStatus, Uri? page)
    {
        if (_navigationId != navigationId) return;
        _loaded = success && httpStatus == 200 && PsbcNavigationContinuation.IsBankOrigin(page);
    }

    internal bool CanObserve(Uri? page) =>
        _loaded && WpfPsbcCertificateLoginPage.IsAllowedPage(page);
}
