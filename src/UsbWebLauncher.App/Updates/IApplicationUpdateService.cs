namespace UsbWebLauncher.App.Updates;

public interface IApplicationUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
    Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken);
    Task VerifyDownloadedAsync(DownloadedUpdate downloaded, CancellationToken cancellationToken);
}

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, NoReleases, InvalidRelease, Incomplete, NetworkError, RateLimited, Cancelled }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateRelease? Release = null);
public sealed record UpdateRelease(string Version, string ReleaseNotes, string InstallerFileName, long Size,
    string Sha256, Uri DownloadUrl, string SourceCommit, string SignatureStatus);
public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes > 0 ? 100d * BytesReceived / TotalBytes : 0;
}
public sealed record DownloadedUpdate(UpdateRelease Release, string FilePath);
