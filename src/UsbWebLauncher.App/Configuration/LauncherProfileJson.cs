using System.Text.Json;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.App.Configuration;

public static class LauncherProfileJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LauncherProfile Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LauncherProfileException("配置文件不能为空。");
        }

        LauncherProfile? profile;
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectSecretFields(document.RootElement);
            profile = document.RootElement.Deserialize<LauncherProfile>(SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new LauncherProfileException($"配置文件不是有效 JSON：{exception.Message}", exception);
        }

        var validation = LauncherProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            var details = string.Join(
                Environment.NewLine,
                validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new LauncherProfileException(details);
        }

        return profile!;
    }

    private static void RejectSecretFields(JsonElement rootElement)
    {
        var secretFieldPath = FindSecretFieldPath(rootElement, "$");
        if (secretFieldPath is null)
        {
            return;
        }

        throw new LauncherProfileException($"{secretFieldPath}: 配置文件不能包含 PIN、password 或 secret 等秘密字段。");
    }

    private static string? FindSecretFieldPath(JsonElement element, string currentPath)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => FindSecretFieldPathInObject(element, currentPath),
            JsonValueKind.Array => FindSecretFieldPathInArray(element, currentPath),
            _ => null
        };
    }

    private static string? FindSecretFieldPathInObject(JsonElement element, string currentPath)
    {
        foreach (var property in element.EnumerateObject())
        {
            var propertyPath = $"{currentPath}.{property.Name}";
            if (IsSecretFieldName(property.Name))
            {
                return propertyPath;
            }

            var nestedPath = FindSecretFieldPath(property.Value, propertyPath);
            if (nestedPath is not null)
            {
                return nestedPath;
            }
        }

        return null;
    }

    private static string? FindSecretFieldPathInArray(JsonElement element, string currentPath)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{currentPath}[{index}]";
            var nestedPath = FindSecretFieldPath(item, itemPath);
            if (nestedPath is not null)
            {
                return nestedPath;
            }

            index++;
        }

        return null;
    }

    private static bool IsSecretFieldName(string propertyName) =>
        propertyName.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase);
}

public sealed class LauncherProfileException : Exception
{
    public LauncherProfileException(string message)
        : base(message)
    {
    }

    public LauncherProfileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
