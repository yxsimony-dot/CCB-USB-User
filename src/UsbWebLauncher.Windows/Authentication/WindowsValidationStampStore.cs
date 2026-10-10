using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication;

internal sealed class WindowsValidationStampStore : IClientCertificateValidationStampStore
{
    private readonly string _directory;

    public WindowsValidationStampStore(string applicationName, string? localApplicationDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        var root = localApplicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(root, applicationName.Trim(), "AuthenticationValidation");
    }

    public async ValueTask<ClientCertificateValidationStamp?> ReadAsync(
        string authenticationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationId);
        var path = GetPath(authenticationId);
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<ValidationStampDocument>(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return document is { Verified: true } &&
               !string.IsNullOrWhiteSpace(document.ConfigurationHash)
            ? new(authenticationId, document.ConfigurationHash)
            : null;
    }

    public async ValueTask SaveAsync(
        ClientCertificateValidationStamp stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp.AuthenticationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp.AuthenticationHash);
        Directory.CreateDirectory(_directory);

        var path = GetPath(stamp.AuthenticationId);
        var temporaryPath = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new ValidationStampDocument(stamp.AuthenticationHash, true);
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(document),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public ValueTask DeleteAsync(
        string authenticationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(authenticationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private string GetPath(string authenticationId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(authenticationId));
        return Path.Combine(_directory, $"{Convert.ToHexString(bytes)}.json");
    }

    private sealed record ValidationStampDocument(string ConfigurationHash, bool Verified);
}
