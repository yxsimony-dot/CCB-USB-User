using System.Text;
using System.Text.Json;
using UsbWebLauncher.App.Browser;
using UsbWebLauncher.App.Configuration;

namespace UsbWebLauncher.Tests.Browser;

public sealed class BankBrowserIsolationTests : IDisposable
{
    private const string OfficialPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA5Q2XCcKExQ4pi2vmtOvHHeerDSh3FJvYLPIZXypeSxuPjMG+evrLCGD+zJ+D1sHR+AiRv+4v3QQKemUSQzrhBEH61/qEeaNQmSnafZDb43hQPlb4E4EoMPLzKgXuNG1xXqSXQjt/rEtcef4yq6E99+q70etthoJPLCY5EiezsZFtWF+y51OLq0+LlUX40PBZoNgd93nZzq2tBqYfn3E9fi1aq+1rqQFDdyLxJCH3LUjAToK0lyZGqHHogjmaDM5FNdnhHTG9mBX7xZ24KrZZP3iwAc9RL8+HAkpRRUH8KM3ByhR/Ukpqlf/YVRutcEyUyO3pgcgBQwQkjKqbbAqRWwIDAQAB";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "UsbWebLauncherBankBrowserTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnvironmentOptionsAreBankSpecific()
    {
        var ccb = BankWebView2Environment.CreateOptions(
            BankBrowserCapabilities.ChinaConstructionBank);
        var psbc = BankWebView2Environment.CreateOptions(
            BankBrowserCapabilities.PostalSavingsBank);

        Assert.True(ccb.AreBrowserExtensionsEnabled);
        Assert.False(psbc.AreBrowserExtensionsEnabled);
    }

    [Fact]
    public void SessionDirectoriesUseSeparateBankRootsAndUniqueSessionIds()
    {
        var ccb = BankBrowserSessionDirectory.Create(
            BankBrowserCapabilities.ChinaConstructionBank,
            _root);
        var anotherCcb = BankBrowserSessionDirectory.Create(
            BankBrowserCapabilities.ChinaConstructionBank,
            _root);
        var psbc = BankBrowserSessionDirectory.Create(
            BankBrowserCapabilities.PostalSavingsBank,
            _root);

        Assert.Equal(
            Path.Combine(_root, "ccb"),
            Path.GetDirectoryName(ccb.Path),
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(_root, "psbc"),
            Path.GetDirectoryName(psbc.Path),
            ignoreCase: true);
        Assert.NotEqual(ccb.Path, anotherCcb.Path);
        Assert.NotEqual(ccb.Path, psbc.Path);
        Assert.True(Guid.TryParseExact(Path.GetFileName(ccb.Path), "N", out _));
        Assert.True(Guid.TryParseExact(Path.GetFileName(psbc.Path), "N", out _));
    }

    [Fact]
    public async Task PostalSavingsInitializationDoesNotTouchCcbExtensionProfileOrPackage()
    {
        var initializer = new BankBrowserExtensionInitializer();
        var profileFactoryCalls = 0;
        var packageFactoryCalls = 0;

        await initializer.EnsureConfiguredAsync(
            BankBrowserCapabilities.PostalSavingsBank,
            () =>
            {
                profileFactoryCalls++;
                throw new InvalidOperationException("PSBC must not access the extension profile.");
            },
            () =>
            {
                packageFactoryCalls++;
                throw new InvalidOperationException("PSBC must not resolve the CCB package.");
            },
            CancellationToken.None);

        Assert.Equal(0, profileFactoryCalls);
        Assert.Equal(0, packageFactoryCalls);
    }

    [Fact]
    public async Task ConstructionBankInitializationStillInstallsVerifiedCcbExtension()
    {
        using var package = TestPackageDirectory.Create();
        var profile = new RecordingBrowserExtensionProfile();
        var initializer = new BankBrowserExtensionInitializer();

        await initializer.EnsureConfiguredAsync(
            BankBrowserCapabilities.ChinaConstructionBank,
            () => profile,
            () => CcbUKeyBrowserExtensionPackage.Load(package.Path),
            CancellationToken.None);

        Assert.Equal(1, profile.GetCalls);
        Assert.Equal(1, profile.AddCalls);
        Assert.Equal(
            CcbUKeyBrowserExtensionPackage.ExpectedExtensionId,
            profile.AddedExtension?.Id);
        Assert.Equal(
            CcbUKeyBrowserExtensionPackage.ExpectedName,
            profile.AddedExtension?.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingBrowserExtensionProfile : IBrowserExtensionProfile
    {
        public int GetCalls { get; private set; }

        public int AddCalls { get; private set; }

        public RecordingBrowserExtension? AddedExtension { get; private set; }

        public Task<IReadOnlyList<IBrowserExtension>> GetBrowserExtensionsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult<IReadOnlyList<IBrowserExtension>>([]);
        }

        public Task<IBrowserExtension> AddBrowserExtensionAsync(
            string extensionDirectoryPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCalls++;
            AddedExtension = new RecordingBrowserExtension();
            return Task.FromResult<IBrowserExtension>(AddedExtension);
        }
    }

    private sealed class RecordingBrowserExtension : IBrowserExtension
    {
        public string Id => CcbUKeyBrowserExtensionPackage.ExpectedExtensionId;

        public string Name => CcbUKeyBrowserExtensionPackage.ExpectedName;

        public bool IsEnabled => true;

        public Task EnableAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestPackageDirectory : IDisposable
    {
        private TestPackageDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestPackageDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "UsbWebLauncher.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var manifest = $$"""
                {
                  "manifest_version": 2,
                  "name": {{JsonSerializer.Serialize(CcbUKeyBrowserExtensionPackage.ExpectedName)}},
                  "version": {{JsonSerializer.Serialize(CcbUKeyBrowserExtensionPackage.ExpectedVersion)}},
                  "default_locale": {{JsonSerializer.Serialize(CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale)}},
                  "key": {{JsonSerializer.Serialize(OfficialPublicKey)}},
                  "permissions": [ "nativeMessaging" ],
                  "background": {
                    "persistent": true,
                    "scripts": [ "background.js" ]
                  },
                  "content_scripts": [
                    {
                      "all_frames": true,
                      "js": [ "content.js" ],
                      "matches": [ "*://*.ccb.com/*" ]
                    }
                  ]
                }
                """;
            File.WriteAllText(
                System.IO.Path.Combine(path, "manifest.json"),
                manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var file in new[]
                     {
                         "background.js",
                         "content.js",
                         "icon-16.png",
                         "icon-48.png",
                         "icon-128.png"
                     })
            {
                File.WriteAllBytes(System.IO.Path.Combine(path, file), []);
            }

            var locale = System.IO.Path.Combine(
                path,
                "_locales",
                CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale);
            Directory.CreateDirectory(locale);
            File.WriteAllText(
                System.IO.Path.Combine(locale, "messages.json"),
                """
                {
                  "appName": {
                    "message": "CCB UKey Extension"
                  },
                  "appDesc": {
                    "message": "CCB UKey Extension"
                  }
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
