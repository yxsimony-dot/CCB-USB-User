namespace UsbWebLauncher.Windows.Authentication;

internal static class WindowsCredentialTarget
{
    public const string ApplicationNamespace = "UsbWebLauncher";

    public static string Create(string authenticationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationId);
        if (authenticationId.Length is < 1 or > 64 ||
            authenticationId.Any(character => !IsCanonicalCharacter(character)))
        {
            throw new ArgumentException(
                "Authentication ID must contain 1-64 lowercase ASCII letters, digits, '.', '_' or '-'.",
                nameof(authenticationId));
        }

        return $"{ApplicationNamespace}/ClientCertificate/{authenticationId}";
    }

    private static bool IsCanonicalCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
