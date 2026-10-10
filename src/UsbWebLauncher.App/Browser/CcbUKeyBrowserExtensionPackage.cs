using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsbWebLauncher.App.Browser;

internal sealed record CcbUKeyBrowserExtensionPackage(
    string DirectoryPath,
    string ExtensionId,
    string Name,
    string Version)
{
    internal const string RelativeDirectory = "browser-extensions/ccb-ukey";
    internal const string ExpectedExtensionId = "ahjacjkbgeefngpfnfhccehkfnaeahbf";
    internal const string ExpectedName = "\u4E2D\u56FD\u5EFA\u8BBE\u94F6\u884CUkey\u6269\u5C55";
    internal const string ExpectedVersion = "1.0.0.9";
    internal const string ExpectedDefaultLocale = "zh_CN";

    private static readonly string[] RequiredFiles =
    [
        "manifest.json",
        "background.js",
        "content.js",
        "icon-16.png",
        "icon-48.png",
        "icon-128.png",
        "_locales/zh_CN/messages.json"
    ];

    public static CcbUKeyBrowserExtensionPackage ResolveFromBaseDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Load(Path.Combine(baseDirectory, RelativeDirectory));
    }

    public static CcbUKeyBrowserExtensionPackage Load(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectoryPath))
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension directory is missing: {fullDirectoryPath}");
        }

        foreach (var requiredFile in RequiredFiles)
        {
            var requiredPath = Path.Combine(fullDirectoryPath, requiredFile);
            if (!File.Exists(requiredPath))
            {
                throw new InvalidDataException(
                    $"The packaged CCB UKey browser extension is missing {requiredFile}.");
            }
        }

        JsonDocument manifest;
        try
        {
            manifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(fullDirectoryPath, "manifest.json")));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "The packaged CCB UKey browser extension manifest is unreadable or malformed.",
                exception);
        }

        using (manifest)
        {
            var root = manifest.RootElement;
            var manifestVersion = GetRequiredInt32(root, "manifest_version");
            if (manifestVersion != 2)
            {
                throw new InvalidDataException(
                    $"The packaged CCB UKey browser extension manifest version must be 2, but was {manifestVersion}.");
            }

            var name = GetRequiredString(root, "name");
            if (!string.Equals(name, ExpectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The packaged CCB UKey browser extension name does not match the authorized extension.");
            }

            var version = GetRequiredString(root, "version");
            if (!string.Equals(version, ExpectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The packaged CCB UKey browser extension version must be {ExpectedVersion}, but was {version}.");
            }

            var defaultLocale = GetRequiredString(root, "default_locale");
            if (!string.Equals(defaultLocale, ExpectedDefaultLocale, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The packaged CCB UKey browser extension default_locale must be {ExpectedDefaultLocale}, but was {defaultLocale}.");
            }

            ValidateLocaleMessages(Path.Combine(
                fullDirectoryPath,
                "_locales",
                defaultLocale,
                "messages.json"));

            RequireArrayValue(root, "permissions", "nativeMessaging");
            RequireNestedArrayValue(root, "background", "scripts", "background.js");
            RequireArrayObjectNestedArrayValue(
                root,
                "content_scripts",
                "matches",
                "*://*.ccb.com/*");

            var extensionId = DeriveExtensionId(GetRequiredString(root, "key"));
            if (!string.Equals(
                    extensionId,
                    ExpectedExtensionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The packaged CCB UKey browser extension ID must be {ExpectedExtensionId}, but was {extensionId}.");
            }

            return new(fullDirectoryPath, extensionId, name, version);
        }
    }

    internal static string DeriveExtensionId(string manifestPublicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPublicKey);

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(manifestPublicKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The packaged CCB UKey browser extension public key is not valid Base64.",
                exception);
        }

        var digest = SHA256.HashData(publicKey);
        Span<char> extensionId = stackalloc char[32];
        for (var index = 0; index < 16; index++)
        {
            extensionId[index * 2] = (char)('a' + (digest[index] >> 4));
            extensionId[(index * 2) + 1] = (char)('a' + (digest[index] & 0x0F));
        }

        return new string(extensionId);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires a non-empty {propertyName} string.");
        }

        return value.GetString()!;
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires an integer {propertyName}.");
        }

        return result;
    }

    private static void ValidateLocaleMessages(string localeMessagesPath)
    {
        JsonDocument messages;
        try
        {
            messages = JsonDocument.Parse(File.ReadAllText(localeMessagesPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "The packaged CCB UKey browser extension locale messages are unreadable or malformed.",
                exception);
        }

        using (messages)
        {
            if (messages.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The packaged CCB UKey browser extension locale messages must be a JSON object.");
            }

            RequireLocaleMessage(messages.RootElement, "appName");
            RequireLocaleMessage(messages.RootElement, "appDesc");
        }
    }

    private static void RequireLocaleMessage(JsonElement root, string messageName)
    {
        if (!root.TryGetProperty(messageName, out var entry) ||
            entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(message.GetString()))
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension locale messages require a non-empty {messageName}.message string.");
        }
    }

    private static void RequireArrayValue(
        JsonElement root,
        string propertyName,
        string expectedValue)
    {
        if (!root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            !array.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), expectedValue, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires {propertyName} to contain {expectedValue}.");
        }
    }

    private static void RequireNestedArrayValue(
        JsonElement root,
        string objectPropertyName,
        string arrayPropertyName,
        string expectedValue)
    {
        if (!root.TryGetProperty(objectPropertyName, out var nestedObject) ||
            nestedObject.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires {objectPropertyName}.");
        }

        RequireArrayValue(nestedObject, arrayPropertyName, expectedValue);
    }

    private static void RequireArrayObjectNestedArrayValue(
        JsonElement root,
        string arrayPropertyName,
        string nestedArrayPropertyName,
        string expectedValue)
    {
        if (!root.TryGetProperty(arrayPropertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires {arrayPropertyName}.");
        }

        var hasExpectedValue = array.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(nestedArrayPropertyName, out var nestedArray) &&
            nestedArray.ValueKind == JsonValueKind.Array &&
            nestedArray.EnumerateArray().Any(nestedItem =>
                nestedItem.ValueKind == JsonValueKind.String &&
                string.Equals(nestedItem.GetString(), expectedValue, StringComparison.Ordinal)));
        if (!hasExpectedValue)
        {
            throw new InvalidDataException(
                $"The packaged CCB UKey browser extension manifest requires {arrayPropertyName}.{nestedArrayPropertyName} to contain {expectedValue}.");
        }
    }
}
