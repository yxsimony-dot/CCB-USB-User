using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsbWebLauncher.App.Updates;

/// <summary>Anonymous, bounded access to the one supported public release repository.</summary>
public sealed class GitHubApplicationUpdateService : IApplicationUpdateService
{
    private const string Repository = "https://github.com/yxsimony-dot/CCB-USB-User";
    private const string ApiRoot = "https://api.github.com/repos/yxsimony-dot/CCB-USB-User";
    private const int ListLimit = 2 * 1024 * 1024;
    private const int ManifestLimit = 64 * 1024;
    private const long InstallerLimit = 1024L * 1024 * 1024;
    private const int DownloadMaxAttempts = 4;
    private const int DownloadBufferSize = 81920;
    private readonly HttpClient client;
    private readonly Version currentVersion;
    private readonly Version osVersion;
    private readonly string cacheRoot;
    private readonly int maxPages;
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private UpdateCheckResult? cachedCheck;
    private DateTimeOffset cacheExpires;
    private DateTimeOffset rateLimitUntil;
    private static readonly Uri LatestManifest = new(Repository + "/releases/latest/download/release-manifest.json");
    private readonly ConcurrentDictionary<UpdateRelease, byte> selected = new();
    private readonly ConcurrentDictionary<string, UpdateRelease> downloads = new(StringComparer.OrdinalIgnoreCase);

    // Injected clients must disable automatic redirects; CreateDefault enforces this for production.
    public GitHubApplicationUpdateService(HttpClient client, string currentVersion, string cacheRoot,
        Version? osVersion = null, int maxPages = 10, TimeSpan? timeout = null, Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.client = client;
        this.currentVersion = ParseVersion(currentVersion) ?? throw new ArgumentException("Invalid current version.", nameof(currentVersion));
        this.cacheRoot = Path.GetFullPath(cacheRoot);
        this.osVersion = osVersion ?? Environment.OSVersion.Version;
        this.maxPages = maxPages > 0 ? maxPages : throw new ArgumentOutOfRangeException(nameof(maxPages));
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public static GitHubApplicationUpdateService CreateDefault(string currentVersion) => new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan },
        currentVersion, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbWebLauncher", "updates"));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var entered = false;
        try
        {
            await checkGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            ct.ThrowIfCancellationRequested();
            var now = utcNow();
            if (cachedCheck != null && now < cacheExpires)
                return cachedCheck with { Message = cachedCheck.Message + "（使用最近一分钟内的检查结果。）" };
            if (now < rateLimitUntil) return RateLimitedResult(now);
            UpdateCheckResult result;
            try { result = await CheckLatestManifestAsync(ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Only the latest-manifest endpoint being absent permits the older API discovery path.
                result = await CheckLegacyReleasesAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (result.Status is UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.UpToDate or UpdateCheckStatus.NoReleases)
            {
                cachedCheck = result;
                cacheExpires = utcNow().AddMinutes(1);
            }
            return result;
        }
        catch (RateLimitException ex)
        {
            rateLimitUntil = ex.RetryAt;
            return RateLimitedResult(utcNow());
        }
        catch (OperationCanceledException) { return new(cancellationToken.IsCancellationRequested ? UpdateCheckStatus.Cancelled : UpdateCheckStatus.Incomplete, cancellationToken.IsCancellationRequested ? "已取消更新检查。" : "更新检查超时，未完成检查，请重试。"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        { return new(UpdateCheckStatus.NetworkError, "GitHub 拒绝访问更新文件，请检查网络、代理或访问权限后重试（HTTP 403）。"); }
        catch (HttpRequestException) { return new(UpdateCheckStatus.NetworkError, "无法连接 GitHub 完成更新检查，请检查网络后重试。"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { return new(UpdateCheckStatus.Incomplete, "更新检查未完成：" + ex.Message); }
        finally { if (entered) checkGate.Release(); }
    }

    private UpdateCheckResult RateLimitedResult(DateTimeOffset now) => new(UpdateCheckStatus.RateLimited,
        $"GitHub 暂时限制请求，请在约 {Math.Max(1, Math.Ceiling((rateLimitUntil - now).TotalSeconds))} 秒后重试；等待期间不会重复发送检查请求。");

    private async Task<UpdateCheckResult> CheckLatestManifestAsync(CancellationToken ct)
    {
        string? version = null;
        try
        {
            using var response = await SendAsync(LatestManifest, true, ct, uri =>
            {
                if (uri == LatestManifest && version == null) return;
                if (uri.Host == "github.com")
                {
                    var prefix = "/yxsimony-dot/CCB-USB-User/releases/download/v";
                    var suffix = "/release-manifest.json";
                    Require(uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
                        && uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) && uri.Query.Length == 0, "版本清单重定向不匹配。");
                    var tagVersion = uri.AbsolutePath[prefix.Length..^suffix.Length];
                    Require(ParseVersion(tagVersion) != null && (version == null || version == tagVersion), "清单重定向版本不匹配。");
                    version = tagVersion;
                }
                else Require(version != null, "清单下载未提供可信的发布版本地址。");
            }).ConfigureAwait(false);
            Require(version != null, "清单下载未提供可信的发布版本地址。");
            using var document = await ReadJsonAsync(response, ManifestLimit, ct).ConfigureAwait(false);
            var release = ValidateManifest(document.RootElement, version!, null,
                $"版本说明：{Repository}/releases/tag/v{version}");
            try
            {
                using var sums = await SendAsync(new Uri($"{Repository}/releases/download/v{version}/SHA256SUMS"), true, ct).ConfigureAwait(false);
                var bytes = await ReadBytesAsync(sums, ManifestLimit, ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff').Trim();
                Require(text.Length >= 66 && text[..64].Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)
                    && text[64..] == $"  {release.InstallerFileName}", "SHA256SUMS 与版本清单不一致。");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            { throw new InvalidDataException("发布文件缺失：SHA256SUMS。", ex); }
            if (ParseVersion(version!)! <= currentVersion) return new(UpdateCheckStatus.UpToDate, "当前已是最新正式版本。");
            selected.TryAdd(release, 0);
            return new(UpdateCheckStatus.UpdateAvailable, $"发现新版本 {release.Version}。", release);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && version != null)
        { return new(UpdateCheckStatus.InvalidRelease, "发布版本清单已不存在，请稍后重试。"); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { return new(UpdateCheckStatus.InvalidRelease, "新版不兼容或发布文件异常：" + ex.Message); }
    }

    private async Task<UpdateCheckResult> CheckLegacyReleasesAsync(CancellationToken ct)
    {
        var candidates = new List<(Version Version, JsonElement Release)>();
        var next = new Uri(ApiRoot + "/releases?per_page=100&page=1");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; next != null; page++)
        {
            if (page >= maxPages || !seen.Add(next.AbsoluteUri))
                return new(UpdateCheckStatus.Incomplete, "更新检查未完成：发布分页超过限制，请稍后重试。");
            using var response = await SendAsync(next, false, ct).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, ListLimit, ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("发布列表格式异常。");
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                var tag = release.GetProperty("tag_name").GetString() ?? "";
                var version = tag.StartsWith('v') ? ParseVersion(tag[1..]) : null;
                if (version != null) candidates.Add((version, release.Clone()));
            }
            next = NextPage(response);
        }
        if (candidates.Count == 0) return new(UpdateCheckStatus.NoReleases, "暂无已发布的正式版本。");
        var warnings = new List<string>();
        foreach (var candidate in candidates.Where(c => c.Version > currentVersion).OrderByDescending(c => c.Version))
        {
            try
            {
                var release = await ValidateReleaseAsync(candidate.Release, candidate.Version.ToString(3), ct).ConfigureAwait(false);
                selected.TryAdd(release, 0);
                return new(UpdateCheckStatus.UpdateAvailable,
                    $"发现新版本 {release.Version}。" + (warnings.Count > 0 ? " 更高版本不可用：" + string.Join("；", warnings) : ""), release);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                warnings.Add($"{candidate.Version.ToString(3)}：{ex.Message}");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                warnings.Add($"{candidate.Version.ToString(3)}：发布文件或 tag 已不存在。");
            }
        }
        return warnings.Count > 0
            ? new(UpdateCheckStatus.InvalidRelease, "新版不兼容或发布文件异常：" + string.Join("；", warnings))
            : new(UpdateCheckStatus.UpToDate, "当前已是最新正式版本。");
    }

    private async Task<UpdateRelease> ValidateReleaseAsync(JsonElement release, string version, CancellationToken ct)
    {
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var manifestAsset = GetAsset(assets, "release-manifest.json", version);
        if (manifestAsset.Size > ManifestLimit) throw new InvalidDataException("清单超过大小限制。");
        GetAsset(assets, "SHA256SUMS", version);
        using var response = await SendAsync(manifestAsset.Url, true, ct).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, ManifestLimit, ct).ConfigureAwait(false);
        var installer = GetAsset(assets, $"UsbWebLauncher-{version}-win-x64-setup.exe", version);
        var notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        notes = Regex.Replace(notes, @"<!--\s*usb-web-launcher-release:.*?-->", "", RegexOptions.Singleline).Trim();
        var validated = ValidateManifest(document.RootElement, version, installer.Size, notes);
        Require(string.Equals(await ResolveTagAsync(version, ct).ConfigureAwait(false), validated.SourceCommit, StringComparison.OrdinalIgnoreCase), "tag 与源提交不匹配。");
        return validated;
    }

    private UpdateRelease ValidateManifest(JsonElement manifest, string version, long? expectedSize, string notes)
    {
        Require(manifest.GetProperty("schemaVersion").GetInt32() == 1, "不支持的清单版本。");
        Require(Text(manifest, "productId") == "usb-web-launcher", "产品身份不匹配。");
        Require(Text(manifest, "version") == version && Text(manifest, "channel") == "stable", "版本或通道不匹配。");
        Require(Text(manifest, "platform") == "windows" && Text(manifest, "architecture") == "x64", "平台或架构不兼容。");
        Require(Version.TryParse(Text(manifest, "minimumOsVersion"), out var minimum) && minimum <= osVersion, "Windows 版本不兼容。");
        Require(Text(manifest, "sourceRepository") == Repository && !manifest.GetProperty("sourceDirty").GetBoolean(), "发布来源不匹配或源码不干净。");
        Require(manifest.GetProperty("brokerProtocolVersion").GetInt32() == 1 && manifest.GetProperty("settingsSchemaVersion").GetInt32() == 1, "协议或设置版本不兼容。");
        var commit = Text(manifest, "sourceCommit");
        Require(Regex.IsMatch(commit, "\\A[0-9a-fA-F]{40}\\z"), "源提交无效。");
        var installerName = $"UsbWebLauncher-{version}-win-x64-setup.exe";
        Require(Text(manifest, "installerFileName") == installerName, "安装文件名不匹配。");
        var size = manifest.GetProperty("size").GetInt64();
        Require(size > 0 && size <= InstallerLimit && (expectedSize == null || size == expectedSize), "安装文件大小不匹配或超过限制。");
        var hash = Text(manifest, "sha256");
        Require(Regex.IsMatch(hash, "\\A[0-9a-fA-F]{64}\\z"), "SHA-256 格式无效。");
        var signature = Text(manifest, "signatureStatus");
        Require(signature is "signed" or "unsigned", "签名状态无效。");
        return new(version, notes, installerName, size, hash,
            new Uri($"{Repository}/releases/download/v{version}/{installerName}"), commit, signature);
    }

    private async Task<string> ResolveTagAsync(string version, CancellationToken ct)
    {
        var uri = new Uri(ApiRoot + "/git/ref/tags/v" + version);
        for (var depth = 0; depth < 5; depth++)
        {
            using var response = await SendAsync(uri, false, ct).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, ManifestLimit, ct).ConfigureAwait(false);
            var item = document.RootElement.GetProperty("object");
            var sha = Text(item, "sha");
            Require(Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}\\z"), "tag 提交无效。");
            if (Text(item, "type") == "commit") return sha;
            Require(Text(item, "type") == "tag", "tag 未指向提交。");
            uri = new Uri(ApiRoot + "/git/tags/" + sha);
        }
        throw new InvalidDataException("tag 嵌套超过限制。");
    }

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(selected.ContainsKey(release), "请重新检查更新后再下载。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var ct = deadline.Token;
        var entered = false;
        try
        {
            await downloadGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var directory = Path.Combine(cacheRoot, release.Version);
            EnsureSafePath(directory);
            Directory.CreateDirectory(directory);
            EnsureSafePath(directory);
            var finalPath = Path.Combine(directory, release.InstallerFileName);
            var partialPath = finalPath + ".partial";
            EnsureSafePath(finalPath);
            EnsureSafePath(partialPath);

            if (File.Exists(finalPath))
            {
                if (await HasExpectedHashAsync(finalPath, release, ct).ConfigureAwait(false))
                {
                    downloads[finalPath] = release;
                    progress?.Report(new(release.Size, release.Size));
                    return new(release, finalPath);
                }
                File.Delete(finalPath);
            }

            if (File.Exists(partialPath) && new FileInfo(partialPath).Length > release.Size)
                File.Delete(partialPath);

            Exception? lastTransientError = null;
            for (var attempt = 1; attempt <= DownloadMaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var received = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                progress?.Report(new(received, release.Size));
                if (received == release.Size) break;
                try
                {
                    await DownloadRangeAsync(release, partialPath, received, progress, ct).ConfigureAwait(false);
                    lastTransientError = null;
                    break;
                }
                catch (Exception ex) when (IsTransientDownloadFailure(ex, ct))
                {
                    lastTransientError = ex;
                    if (attempt == DownloadMaxAttempts) break;
                    await delayAsync(TimeSpan.FromSeconds(1 << (attempt - 1)), ct).ConfigureAwait(false);
                }
                catch
                {
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                    throw;
                }
            }

            var completed = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (lastTransientError != null)
                throw new IOException($"下载连接多次中断，已自动重试 {DownloadMaxAttempts} 次；已保留 {completed} / {release.Size} 字节，下次可继续下载。", lastTransientError);

            Require(completed == release.Size, "下载不完整。");
            if (!await HasExpectedHashAsync(partialPath, release, ct).ConfigureAwait(false))
            {
                File.Delete(partialPath);
                throw new InvalidDataException("安装文件 SHA-256 校验失败。");
            }

            ct.ThrowIfCancellationRequested();
            EnsureSafePath(partialPath);
            File.Move(partialPath, finalPath);
            downloads[finalPath] = release;
            return new(release, finalPath);
        }
        finally
        {
            if (entered) downloadGate.Release();
        }
    }

    private async Task DownloadRangeAsync(UpdateRelease release, string partialPath, long received,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var response = await SendAsync(release.DownloadUrl, true, ct, rangeStart: received > 0 ? received : null).ConfigureAwait(false);
        var append = received > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (received > 0 && !append)
        {
            Require(response.StatusCode == HttpStatusCode.OK, "下载服务器拒绝断点续传。");
            received = 0;
        }

        if (append)
        {
            var range = response.Content.Headers.ContentRange;
            Require(range?.Unit == "bytes" && range.From == received && range.Length == release.Size,
                "下载断点与发布文件不匹配。");
            if (response.Content.Headers.ContentLength is long declared)
                Require(declared > 0 && declared <= release.Size - received, "安装文件大小与清单不一致。");
        }
        else if (response.Content.Headers.ContentLength is long declared)
        {
            Require(declared == release.Size, "安装文件大小与清单不一致。");
        }

        var mode = append ? FileMode.Open : FileMode.Create;
        await using var output = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, DownloadBufferSize, true);
        if (append) output.Seek(received, SeekOrigin.Begin);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[DownloadBufferSize];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            received += count;
            Require(received <= release.Size && received <= InstallerLimit, "下载超出预期大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Report(new(received, release.Size));
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
        if (received < release.Size)
            throw new IOException($"下载连接提前结束，当前已接收 {received} / {release.Size} 字节。");
    }

    private static bool IsTransientDownloadFailure(Exception exception, CancellationToken ct) =>
        !ct.IsCancellationRequested && exception is HttpRequestException or IOException;

    private static async Task<bool> HasExpectedHashAsync(string path, UpdateRelease release, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length != release.Size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
        var hash = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task VerifyDownloadedAsync(DownloadedUpdate downloaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(downloads.TryGetValue(downloaded.FilePath, out var issued) && issued == downloaded.Release && selected.ContainsKey(downloaded.Release), "安装缓存身份无效，请重新下载。");
        EnsureSafePath(downloaded.FilePath);
        Require(Path.GetFileName(downloaded.FilePath) == downloaded.Release.InstallerFileName, "安装缓存文件名无效。");
        await using var input = new FileStream(downloaded.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
        Require(input.Length == downloaded.Release.Size, "安装缓存大小已改变，请重新下载。");
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        Require(Convert.ToHexString(hash).Equals(downloaded.Release.Sha256, StringComparison.OrdinalIgnoreCase), "安装缓存已改变，请重新下载。");
    }

    private void EnsureSafePath(string path)
    {
        var full = Path.GetFullPath(path);
        Require(full.StartsWith(cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "缓存路径不安全。");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "缓存路径包含重定向，请移除链接后重试。");
    }

    private static (Uri Url, long Size) GetAsset(JsonElement[] assets, string name, string version)
    {
        var matches = assets.Where(a => Text(a, "name") == name).ToArray();
        Require(matches.Length == 1, "缺失或重复发布资产：" + name);
        var asset = matches[0];
        Require(Text(asset, "state") == "uploaded", "发布资产尚未上传完成。");
        var expected = $"{Repository}/releases/download/v{version}/{name}";
        Require(Text(asset, "browser_download_url") == expected, "发布资产来源或版本不匹配。");
        var size = asset.GetProperty("size").GetInt64();
        Require(size > 0, "发布资产为空。");
        return (new Uri(expected), size);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool asset, CancellationToken ct,
        Action<Uri>? observeUri = null, long? rangeStart = null)
    {
        var initialUri = uri;
        for (var hop = 0; hop <= 5; hop++)
        {
            ValidateUri(uri, asset);
            if (asset && initialUri != LatestManifest && uri.Host == "github.com")
                Require(uri == initialUri, "发布文件重定向来源或版本不匹配。");
            observeUri?.Invoke(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("UsbWebLauncher-Updater/1.0");
            if (rangeStart is long start) request.Headers.Range = new RangeHeaderValue(start, null);
            if (!asset) { request.Headers.Accept.ParseAdd("application/vnd.github+json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri effective && effective != uri)
            {
                response.Dispose();
                throw new InvalidDataException("更新连接必须关闭自动重定向，以校验每个下载地址。");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                Require(location != null, "下载重定向无目标。");
                uri = new Uri(uri, location!);
                continue;
            }
            if ((int)response.StatusCode is 403 or 429)
            {
                try
                {
                    var limit = await GetRateLimitAsync(response, ct).ConfigureAwait(false);
                    if (limit != null) throw limit;
                    response.EnsureSuccessStatusCode();
                }
                finally { response.Dispose(); }
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("下载重定向超过限制。");
    }

    private static void ValidateUri(Uri uri, bool asset)
    {
        Require(uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0, "下载来源必须是可信 HTTPS 地址。");
        if (!asset)
        {
            Require(uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith("/repos/yxsimony-dot/CCB-USB-User/", StringComparison.Ordinal), "API 来源不匹配。");
            return;
        }
        Require((uri.Host == "github.com" && (uri.AbsolutePath.StartsWith("/yxsimony-dot/CCB-USB-User/releases/download/", StringComparison.Ordinal) || uri == LatestManifest))
            || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com", "拒绝不可信的下载重定向来源。");
    }

    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        foreach (var link in links.SelectMany(v => v.Split(',')))
        {
            if (!link.Contains("rel=\"next\"", StringComparison.Ordinal)) continue;
            var match = Regex.Match(link, "<([^>]+)>");
            Require(match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out _), "发布分页地址无效。");
            var uri = new Uri(match.Groups[1].Value);
            ValidateUri(uri, false);
            Require(uri.AbsolutePath == "/repos/yxsimony-dot/CCB-USB-User/releases", "发布分页地址不匹配。");
            return uri;
        }
        return null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(response, limit, ct).ConfigureAwait(false);
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset));
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        Require(response.Content.Headers.ContentLength is not long size || size <= limit, "响应超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            Require(output.Length + count <= limit, "响应超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static Version? ParseVersion(string value) => Regex.IsMatch(value, "\\A(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\z")
        && Version.TryParse(value, out var version) && version.Major <= 65535 && version.Minor <= 65535 && version.Build <= 65535 ? version : null;
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString() ?? "";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private async Task<RateLimitException?> GetRateLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var now = utcNow();
        var retry = response.Headers.RetryAfter;
        var isLimit = (int)response.StatusCode == 429 || retry != null
            || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(value => value.Trim() == "0"));
        if (!isLimit)
        {
            try
            {
                using var document = await ReadJsonAsync(response, ManifestLimit, ct).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString() ?? "";
                    isLimit = text.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException) { }
        }
        if (!isLimit) return null;
        var delta = retry?.Delta ?? TimeSpan.FromMinutes(1);
        var retryAt = retry?.Date ?? now.Add(delta > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delta);
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets)
            && long.TryParse(resets.FirstOrDefault(), out var seconds) && seconds >= 0 && seconds <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset > retryAt) retryAt = reset;
        }
        if (retryAt <= now) retryAt = now.AddMinutes(1);
        if (retryAt > now.AddDays(1)) retryAt = now.AddDays(1);
        return new RateLimitException(retryAt);
    }

    private sealed class RateLimitException(DateTimeOffset retryAt) : HttpRequestException("GitHub 暂时限制下载请求，请稍后重试。")
    {
        public DateTimeOffset RetryAt { get; } = retryAt;
    }
}
