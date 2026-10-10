using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsbWebLauncher.App.Updates;

namespace UsbWebLauncher.Tests.Updates;

public sealed class GitHubApplicationUpdateServiceTests : IDisposable
{
    private const string Repository = "https://github.com/yxsimony-dot/CCB-USB-User";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string cache = Path.Combine(Path.GetTempPath(), "updater-tests-" + Guid.NewGuid());
    private readonly byte[] installer = Encoding.UTF8.GetBytes("synthetic installer bytes, never executable");
    private readonly List<Uri> requests = [];
    private readonly Dictionary<string, object?> overrides = [];
    private Func<HttpRequestMessage, HttpResponseMessage?>? intercept;

    [Fact]
    public async Task Utf8BomFromWindowsPublishedManifestIsAccepted()
    {
        // The legacy Windows build can write UTF-8 JSON with a BOM.
        var service = Service([Release("1.1.10")], manifestBom: true);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await service.CheckAsync(default)).Status);
    }

    [Fact]
    public async Task ReleaseNotesHideManagedPublishingIdentityButKeepOtherTextUnexecuted()
    {
        var result = await Service([Release("1.1.10")]).CheckAsync(default);
        Assert.Equal("<b>plain notes</b>", result.Release?.ReleaseNotes);
    }

    [Fact]
    public async Task MissingRequiredManifestFieldsRejectCandidate()
    {
        intercept = r => r.RequestUri!.AbsolutePath.EndsWith("release-manifest.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") } : null;
        Assert.Equal(UpdateCheckStatus.InvalidRelease, (await Service([Release("1.1.10")]).CheckAsync(default)).Status);
    }

    [Fact]
    public async Task MissingTagRejectsCandidateButAllowsLowerValidUpdate()
    {
        intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/git/ref/tags/v1.1.20") ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        var result = await Service([Release("1.1.20"), Release("1.1.10")]).CheckAsync(default);
        Assert.Equal("1.1.10", result.Release?.Version);
        Assert.Contains("1.1.20", result.Message);
    }

    [Fact]
    public async Task CheckDeadlineCancelsPendingNetworkAndReturnsIncomplete()
    {
        var service = new GitHubApplicationUpdateService(new HttpClient(new BlockingHandler()), "1.1.0", cache,
            timeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal(UpdateCheckStatus.Incomplete, (await service.CheckAsync(default)).Status);
    }

    [Fact]
    public async Task OversizedListCannotReportLatest()
    {
        intercept = _ => Bytes(new byte[3 * 1024 * 1024]);
        Assert.Equal(UpdateCheckStatus.Incomplete, (await Service([]).CheckAsync(default)).Status);
    }

    [Fact]
    public async Task AllowsKnownGitHubAssetRedirectAndReportsCompleteProgress()
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        intercept = r => r.RequestUri!.Host == "github.com" && r.RequestUri.AbsolutePath.EndsWith(".exe")
            ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/test?signature=example") : null;
        var progress = new CaptureProgress();
        var result = await service.DownloadAsync(release, progress, default);
        Assert.Equal(installer.Length, progress.Latest?.BytesReceived);
        Assert.Equal(100, progress.Latest?.Percentage);
        Assert.Equal(installer, await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public async Task CancellationDuringStreamCleansPartialFile()
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        using var cts = new CancellationTokenSource();
        intercept = r => r.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAfterFirstReadStream(installer, cts)) } : null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(release, null, cts.Token));
        Assert.Empty(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InventedReleaseOrCopiedFileCannotBeExecuted()
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Version = "1.1.11" }, null, default));
        var downloaded = await service.DownloadAsync(release, null, default);
        var copied = Path.Combine(cache, release.InstallerFileName);
        File.Copy(downloaded.FilePath, copied);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyDownloadedAsync(downloaded with { FilePath = copied }, default));
    }

    [Fact]
    public async Task SelectsHighestNumericVersionAcrossPagesIgnoringPublicationOrderAndDrafts()
    {
        var service = Service([Release("1.1.9"), Release("9.0.0", draft: true)], [Release("1.1.10"), Release("1.1.8")]);
        var result = await service.CheckAsync(default);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.1.10", result.Release?.Version);
        Assert.Contains(requests, x => x.Query.Contains("page=2"));
    }

    [Fact]
    public async Task NoReleaseAndInstalledLatestAreDistinct()
    {
        Assert.Equal(UpdateCheckStatus.NoReleases, (await Service([]).CheckAsync(default)).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await Service([Release("1.1.0")]).CheckAsync(default)).Status);
    }

    [Theory]
    [InlineData("schemaVersion", 9)]
    [InlineData("productId", "other")]
    [InlineData("architecture", "arm64")]
    [InlineData("platform", "linux")]
    [InlineData("sourceRepository", "https://github.com/other/repo")]
    [InlineData("sourceDirty", true)]
    [InlineData("sourceCommit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("minimumOsVersion", "99.0.0")]
    [InlineData("installerFileName", "../evil.exe")]
    [InlineData("version", "1.1.20")]
    [InlineData("sha256", "not-a-hash")]
    public async Task RejectsInvalidOrIncompatibleNewerManifest(string field, object value)
    {
        overrides[field] = value;
        var result = await Service([Release("1.1.10")]).CheckAsync(default);
        Assert.Equal(UpdateCheckStatus.InvalidRelease, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("1.1.10", result.Message);
    }

    [Fact]
    public async Task ValidLowerUpdateRetainsWarningAboutBrokenHigherVersion()
    {
        intercept = r => r.RequestUri!.AbsolutePath.Contains("/v1.1.20/release-manifest.json") ? Json(new { schemaVersion = 99 }) : null;
        var result = await Service([Release("1.1.20"), Release("1.1.10")]).CheckAsync(default);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.1.10", result.Release?.Version);
        Assert.Contains("1.1.20", result.Message);
    }

    [Fact]
    public async Task PageLimitNeverReportsUpToDate()
    {
        var result = await Service([Release("1.1.0")], [Release("1.1.20")], maxPages: 1).CheckAsync(default);
        Assert.Equal(UpdateCheckStatus.Incomplete, result.Status);
    }

    [Theory]
    [InlineData(403, UpdateCheckStatus.RateLimited)]
    [InlineData(429, UpdateCheckStatus.RateLimited)]
    [InlineData(500, UpdateCheckStatus.NetworkError)]
    public async Task HttpFailuresRemainRetryable(int status, UpdateCheckStatus expected)
    {
        intercept = _ => new HttpResponseMessage((HttpStatusCode)status);
        Assert.Equal(expected, (await Service([]).CheckAsync(default)).Status);
    }

    [Fact]
    public async Task DownloadVerifiesBytesAndDetectsSubsequentCacheMutation()
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        var downloaded = await service.DownloadAsync(release, null, default);
        Assert.Equal(installer, await File.ReadAllBytesAsync(downloaded.FilePath));
        await service.VerifyDownloadedAsync(downloaded, default);
        await File.WriteAllTextAsync(downloaded.FilePath, "modified");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyDownloadedAsync(downloaded, default));
    }

    [Theory]
    [InlineData("https://evil.example/payload")]
    [InlineData("http://github.com/yxsimony-dot/CCB-USB-User/releases/download/v1.1.10/a.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v1.1.10/a.exe")]
    public async Task RefusesUntrustedRedirectWithoutRequestingDestination(string location)
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        intercept = r => r.RequestUri!.AbsolutePath.EndsWith(".exe") ? Redirect(location) : null;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null, default));
        Assert.DoesNotContain(requests, u => u.AbsoluteUri == location);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(0)]
    public async Task CorruptTruncatedOrOversizedDownloadCleansOnlyOwnPartialFile(int lengthChange)
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        Directory.CreateDirectory(cache);
        var keep = Path.Combine(cache, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep");
        intercept = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith(".exe")) return null;
            var response = Bytes(new byte[installer.Length + lengthChange]);
            // Exercise actual stream bounds even when the server claims the expected length.
            response.Content.Headers.ContentLength = installer.Length;
            return response;
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null, default));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationReturnsCancelledCheckAndDoesNotCreateDownload()
    {
        var service = Service([Release("1.1.10")]);
        var release = (await service.CheckAsync(default)).Release!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(UpdateCheckStatus.Cancelled, (await service.CheckAsync(cts.Token)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(release, null, cts.Token));
        Assert.False(Directory.Exists(cache));
    }

    private GitHubApplicationUpdateService Service(object[] first, object[]? second = null, int maxPages = 10, bool manifestBom = false)
    {
        var handler = new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            var intercepted = intercept?.Invoke(request);
            if (intercepted != null) return intercepted;
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/releases"))
            {
                var response = Json(uri.Query.Contains("page=2") ? second! : first);
                if (second != null && !uri.Query.Contains("page=2")) response.Headers.Add("Link", "<https://api.github.com/repos/yxsimony-dot/CCB-USB-User/releases?per_page=100&page=2>; rel=\"next\"");
                return response;
            }
            if (uri.AbsolutePath.Contains("/git/ref/tags/")) return Json(new { @object = new { type = "commit", sha = Commit } });
            if (uri.AbsolutePath.EndsWith("release-manifest.json"))
            {
                var version = uri.Segments[^2].Trim('/')[1..];
                var manifest = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = 1, ["productId"] = "usb-web-launcher", ["version"] = version,
                    ["channel"] = "stable", ["platform"] = "windows", ["architecture"] = "x64", ["minimumOsVersion"] = "10.0.19041",
                    ["sourceRepository"] = Repository, ["sourceCommit"] = Commit, ["sourceDirty"] = false,
                    ["installerFileName"] = InstallerName(version), ["size"] = installer.Length,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(installer)), ["signatureStatus"] = "unsigned",
                    ["brokerProtocolVersion"] = 1, ["settingsSchemaVersion"] = 1
                };
                foreach (var pair in overrides) manifest[pair.Key] = pair.Value;
                return manifestBom ? Bytes([0xef, 0xbb, 0xbf, .. JsonSerializer.SerializeToUtf8Bytes(manifest)]) : Json(manifest);
            }
            return Bytes(installer);
        });
        return new GitHubApplicationUpdateService(new HttpClient(handler), "1.1.0", cache, new Version(10, 0, 22621), maxPages);
    }

    private object Release(string version, bool draft = false) => new
    {
        tag_name = "v" + version, draft, prerelease = false,
        body = "<b>plain notes</b>\n<!-- usb-web-launcher-release:{\"sourceCommit\":\"internal identity\"} -->",
        assets = new[] { Asset(version, "release-manifest.json", 1024), Asset(version, "SHA256SUMS", 120), Asset(version, InstallerName(version), installer.Length) }
    };
    private static object Asset(string version, string name, long size) => new { name, size, state = "uploaded", browser_download_url = $"{Repository}/releases/download/v{version}/{name}" };
    private static string InstallerName(string version) => $"UsbWebLauncher-{version}-win-x64-setup.exe";
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage Redirect(string location) { var result = new HttpResponseMessage(HttpStatusCode.Redirect); result.Headers.Location = new Uri(location); return result; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
    }
    private sealed class CaptureProgress : IProgress<UpdateDownloadProgress>
    {
        public UpdateDownloadProgress? Latest { get; private set; }
        public void Report(UpdateDownloadProgress value) => Latest = value;
    }
    private sealed class CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cts.Cancel(); return base.ReadAsync(buffer, cancellationToken); }
    }
    public void Dispose() { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
}
