using System.Security.Cryptography;
using System.Text.Json;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsValidationStampStoreTests
{
    [Fact]
    public async Task SaveReadAndDelete_PersistsOnlyVerifiedNonSecretStamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsbWebLauncher.Tests", Guid.NewGuid().ToString("N"));
        var store = new WindowsValidationStampStore("UsbWebLauncher.Tests", root);
        var authenticationId = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var secretMarker = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            await store.SaveAsync(new(authenticationId, hash));

            var loaded = await store.ReadAsync(authenticationId);
            var persisted = Assert.Single(
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            using var document = JsonDocument.Parse(persisted);

            Assert.Equal(authenticationId, loaded?.AuthenticationId);
            Assert.Equal(hash, loaded?.AuthenticationHash);
            Assert.Equal(
                ["ConfigurationHash", "Verified"],
                document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(hash, document.RootElement.GetProperty("ConfigurationHash").GetString());
            Assert.True(document.RootElement.GetProperty("Verified").GetBoolean());
            Assert.DoesNotContain(authenticationId, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMarker, persisted, StringComparison.Ordinal);

            await store.DeleteAsync(authenticationId);

            Assert.Null(await store.ReadAsync(authenticationId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
