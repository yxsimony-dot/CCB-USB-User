using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class CcbUKeyBrowserExtensionTests
{
    private const string OfficialPublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA5Q2XCcKExQ4pi2vmtOvHHeerDSh3FJvYLPIZXypeSxuPjMG+evrLCGD+zJ+D1sHR+AiRv+4v3QQKemUSQzrhBEH61/qEeaNQmSnafZDb43hQPlb4E4EoMPLzKgXuNG1xXqSXQjt/rEtcef4yq6E99+q70etthoJPLCY5EiezsZFtWF+y51OLq0+LlUX40PBZoNgd93nZzq2tBqYfn3E9fi1aq+1rqQFDdyLxJCH3LUjAToK0lyZGqHHogjmaDM5FNdnhHTG9mBX7xZ24KrZZP3iwAc9RL8+HAkpRRUH8KM3ByhR/Ukpqlf/YVRutcEyUyO3pgcgBQwQkjKqbbAqRWwIDAQAB";

    [Fact]
    public void Load_AcceptsPackagedOfficialExtension()
    {
        var extensionPath = FindRepoRootPath(
            "src",
            "UsbWebLauncher.App",
            "browser-extensions",
            "ccb-ukey");

        var package = CcbUKeyBrowserExtensionPackage.Load(extensionPath);

        Assert.Equal(CcbUKeyBrowserExtensionPackage.ExpectedExtensionId, package.ExtensionId);
        Assert.Equal(CcbUKeyBrowserExtensionPackage.ExpectedName, package.Name);
        Assert.Equal(CcbUKeyBrowserExtensionPackage.ExpectedVersion, package.Version);
    }

    [Fact]
    public void Load_RejectsMissingManifest()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        File.Delete(Path.Combine(packageDirectory.Path, "manifest.json"));

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains("manifest.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsMissingRequiredFile()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        File.Delete(Path.Combine(packageDirectory.Path, "background.js"));

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains("background.js", exception.Message);
    }

    [Fact]
    public void Load_RejectsMissingLocaleMessages()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        File.Delete(Path.Combine(
            packageDirectory.Path,
            "_locales",
            CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale,
            "messages.json"));

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains("_locales/zh_CN/messages.json", exception.Message);
    }

    [Fact]
    public void Load_RejectsMalformedLocaleMessages()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        File.WriteAllText(
            Path.Combine(
                packageDirectory.Path,
                "_locales",
                CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale,
                "messages.json"),
            "{");

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains("locale messages", exception.Message);
    }

    [Fact]
    public void Load_RejectsUnexpectedVersion()
    {
        using var packageDirectory = TestPackageDirectory.Create(version: "1.0.0.8");

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains(CcbUKeyBrowserExtensionPackage.ExpectedVersion, exception.Message);
    }

    [Fact]
    public void Load_RejectsUnexpectedName()
    {
        using var packageDirectory = TestPackageDirectory.Create(name: "Unexpected extension");

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains("name", exception.Message);
    }

    [Fact]
    public void Load_RejectsPublicKeyWithUnexpectedDerivedId()
    {
        using var packageDirectory = TestPackageDirectory.Create(
            publicKey: Convert.ToBase64String([1, 2, 3, 4]));

        var exception = Assert.Throws<InvalidDataException>(
            () => CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path));

        Assert.Contains(CcbUKeyBrowserExtensionPackage.ExpectedExtensionId, exception.Message);
    }

    [Fact]
    public async Task Installer_LeavesExistingEnabledExtensionRunning()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        var extension = FakeBrowserExtension.CreateAuthorized(isEnabled: true);
        var profile = new FakeBrowserExtensionProfile(extension);
        var installer = new CcbUKeyBrowserExtensionInstaller();

        await installer.EnsureInstalledAsync(
            profile,
            CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path),
            CancellationToken.None);

        Assert.Equal(0, profile.AddCalls);
        Assert.Equal(0, extension.EnableCalls);
    }

    [Fact]
    public async Task Installer_EnablesExistingDisabledExtension()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        var extension = FakeBrowserExtension.CreateAuthorized(isEnabled: false);
        var profile = new FakeBrowserExtensionProfile(extension);
        var installer = new CcbUKeyBrowserExtensionInstaller();

        await installer.EnsureInstalledAsync(
            profile,
            CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path),
            CancellationToken.None);

        Assert.Equal(0, profile.AddCalls);
        Assert.Equal(1, extension.EnableCalls);
        Assert.True(extension.IsEnabled);
    }

    [Fact]
    public async Task Installer_AddsMissingExtension()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        var addedExtension = FakeBrowserExtension.CreateAuthorized(isEnabled: true);
        var profile = new FakeBrowserExtensionProfile
        {
            AddResult = addedExtension
        };
        var installer = new CcbUKeyBrowserExtensionInstaller();
        var package = CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path);

        await installer.EnsureInstalledAsync(
            profile,
            package,
            CancellationToken.None);

        Assert.Equal(1, profile.AddCalls);
        Assert.Equal(package.DirectoryPath, profile.LastAddedPath);
        Assert.Equal(0, addedExtension.EnableCalls);
    }

    [Fact]
    public void Installer_PreservesCapturedContextAcrossProfileOperations()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        using var context = new PumpSynchronizationContext();
        var extension = new ContextBoundBrowserExtension(context);
        var profile = new ContextBoundBrowserExtensionProfile(context, extension);
        var installer = new CcbUKeyBrowserExtensionInstaller();

        context.Run(async () =>
        {
            await installer.EnsureInstalledAsync(
                profile,
                CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path),
                CancellationToken.None);
            context.VerifyAccess();
        });

        Assert.Equal(1, profile.AddCalls);
        Assert.Equal(1, extension.EnableCalls);
    }

    [Fact]
    public async Task Installer_RejectsUnexpectedAddedExtensionId()
    {
        using var packageDirectory = TestPackageDirectory.Create();
        var profile = new FakeBrowserExtensionProfile
        {
            AddResult = new(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                CcbUKeyBrowserExtensionPackage.ExpectedName,
                isEnabled: true)
        };
        var installer = new CcbUKeyBrowserExtensionInstaller();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.EnsureInstalledAsync(
                profile,
                CcbUKeyBrowserExtensionPackage.Load(packageDirectory.Path),
                CancellationToken.None));

        Assert.Contains("unexpected browser extension ID", exception.Message);
    }

    [Fact]
    public void EnvironmentOptions_EnableBrowserExtensions()
    {
        var options = CcbUKeyWebView2Environment.CreateOptions();

        Assert.True(options.AreBrowserExtensionsEnabled);
    }

    private static string FindRepoRootPath(params string[] relativeSegments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UsbWebLauncher.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. relativeSegments]);
    }

    private sealed class FakeBrowserExtension(
        string id,
        string name,
        bool isEnabled) : IBrowserExtension
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool IsEnabled { get; private set; } = isEnabled;

        public int EnableCalls { get; private set; }

        public static FakeBrowserExtension CreateAuthorized(bool isEnabled) =>
            new(
                CcbUKeyBrowserExtensionPackage.ExpectedExtensionId,
                CcbUKeyBrowserExtensionPackage.ExpectedName,
                isEnabled);

        public Task EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            IsEnabled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBrowserExtensionProfile(
        params FakeBrowserExtension[] installed) : IBrowserExtensionProfile
    {
        private readonly IReadOnlyList<IBrowserExtension> _installed = installed;

        public FakeBrowserExtension? AddResult { get; init; }

        public int AddCalls { get; private set; }

        public string? LastAddedPath { get; private set; }

        public Task<IReadOnlyList<IBrowserExtension>> GetBrowserExtensionsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_installed);
        }

        public Task<IBrowserExtension> AddBrowserExtensionAsync(
            string extensionDirectoryPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCalls++;
            LastAddedPath = extensionDirectoryPath;
            return Task.FromResult<IBrowserExtension>(
                AddResult ?? throw new InvalidOperationException("No add result configured."));
        }
    }

    private sealed class ContextBoundBrowserExtension(
        PumpSynchronizationContext context) : IBrowserExtension
    {
        private bool _isEnabled;

        public string Id
        {
            get
            {
                context.VerifyAccess();
                return CcbUKeyBrowserExtensionPackage.ExpectedExtensionId;
            }
        }

        public string Name
        {
            get
            {
                context.VerifyAccess();
                return CcbUKeyBrowserExtensionPackage.ExpectedName;
            }
        }

        public bool IsEnabled
        {
            get
            {
                context.VerifyAccess();
                return _isEnabled;
            }
        }

        public int EnableCalls { get; private set; }

        public Task EnableAsync(CancellationToken cancellationToken)
        {
            context.VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            return Task.Run(
                () => _isEnabled = true,
                cancellationToken);
        }
    }

    private sealed class ContextBoundBrowserExtensionProfile(
        PumpSynchronizationContext context,
        ContextBoundBrowserExtension extension) : IBrowserExtensionProfile
    {
        public int AddCalls { get; private set; }

        public Task<IReadOnlyList<IBrowserExtension>> GetBrowserExtensionsAsync(
            CancellationToken cancellationToken)
        {
            context.VerifyAccess();
            return Task.Run<IReadOnlyList<IBrowserExtension>>(
                Array.Empty<IBrowserExtension>,
                cancellationToken);
        }

        public Task<IBrowserExtension> AddBrowserExtensionAsync(
            string extensionDirectoryPath,
            CancellationToken cancellationToken)
        {
            context.VerifyAccess();
            AddCalls++;
            return Task.Run<IBrowserExtension>(
                () => extension,
                cancellationToken);
        }
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _work.Add((callback, state));
        }

        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var task = action();
                while (!task.IsCompleted)
                {
                    if (_work.TryTake(out var item, millisecondsTimeout: 50))
                    {
                        item.Callback(item.State);
                    }
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void VerifyAccess()
        {
            if (Environment.CurrentManagedThreadId != _threadId ||
                !ReferenceEquals(Current, this))
            {
                throw new InvalidOperationException(
                    "Browser extension COM access left its captured UI context.");
            }
        }

        public void Dispose()
        {
            _work.Dispose();
        }
    }

    private sealed class TestPackageDirectory : IDisposable
    {
        private TestPackageDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestPackageDirectory Create(
            string? version = null,
            string? name = null,
            string? publicKey = null)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "UsbWebLauncher.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);

            var manifest = $$"""
                {
                  "manifest_version": 2,
                  "name": {{JsonSerializer.Serialize(name ?? CcbUKeyBrowserExtensionPackage.ExpectedName)}},
                  "version": {{JsonSerializer.Serialize(version ?? CcbUKeyBrowserExtensionPackage.ExpectedVersion)}},
                  "default_locale": {{JsonSerializer.Serialize(CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale)}},
                  "key": {{JsonSerializer.Serialize(publicKey ?? OfficialPublicKey)}},
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
                System.IO.Path.Combine(directoryPath, "manifest.json"),
                manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var requiredFile in new[]
                     {
                         "background.js",
                         "content.js",
                         "icon-16.png",
                         "icon-48.png",
                         "icon-128.png"
                     })
            {
                File.WriteAllBytes(System.IO.Path.Combine(directoryPath, requiredFile), []);
            }

            var localeDirectory = System.IO.Path.Combine(
                directoryPath,
                "_locales",
                CcbUKeyBrowserExtensionPackage.ExpectedDefaultLocale);
            Directory.CreateDirectory(localeDirectory);
            File.WriteAllText(
                System.IO.Path.Combine(localeDirectory, "messages.json"),
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

            return new(directoryPath);
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
