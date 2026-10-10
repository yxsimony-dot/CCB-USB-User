using System.Globalization;
using System.IO;

namespace UsbWebLauncher.App.Browser;

public sealed class StatementPdfFile : IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string fileStem;
    private string? completedPath;
    private bool disposed;
    private bool failed;
    private bool ownsTemporaryFile = true;

    private StatementPdfFile(string directory, StatementExportIdentity identity, string tempPath)
    {
        this.directory = directory;
        fileStem = identity.FileStem;
        TempPath = tempPath;
    }

    public string TempPath { get; }

    public static StatementPdfFile Create(string directory, StatementExportIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(identity);
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        while (true)
        {
            var path = Path.Combine(fullDirectory, $".statement-{Guid.NewGuid():N}.download");
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new StatementPdfFile(fullDirectory, identity, path);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }

    public Task<string> CompleteAsync(CancellationToken cancellationToken)
    {
        // Only the five-byte signature and an atomic rename are synchronous. Serializing
        // this short operation also prevents Dispose from racing publication.
        lock (gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (completedPath is not null)
                    return Task.FromResult(completedPath);
                if (failed)
                    throw new InvalidOperationException("本次下载未完成，请重新导出。");

                cancellationToken.ThrowIfCancellationRequested();
                using var source = new FileStream(TempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                Span<byte> signature = stackalloc byte[5];
                if (source.Length < signature.Length)
                    throw new InvalidDataException("下载文件不是有效的 PDF。");
                source.ReadExactly(signature);
                if (!signature.SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("下载文件不是有效的 PDF。");

                for (var suffix = 1; ; suffix++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = suffix == 1 ? $"{fileStem}.pdf" : string.Create(CultureInfo.InvariantCulture, $"{fileStem} ({suffix}).pdf");
                    var destination = Path.Combine(directory, name);
                    try
                    {
                        // Same-directory, no-overwrite rename is the publication boundary.
                        File.Move(TempPath, destination, overwrite: false);
                        ownsTemporaryFile = false;
                        completedPath = destination;
                        return Task.FromResult(destination);
                    }
                    catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failed = true;
                DeleteTemporaryFile();
                return Task.FromCanceled<string>(cancellationToken);
            }
            catch (Exception error)
            {
                failed = true;
                DeleteTemporaryFile();
                return Task.FromException<string>(error);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            DeleteTemporaryFile();
        }
    }

    private void DeleteTemporaryFile()
    {
        if (!ownsTemporaryFile)
            return;
        try
        {
            File.Delete(TempPath);
            ownsTemporaryFile = false;
        }
        // A download may still own its handle. The caller stops it before disposing,
        // and disposal retries cleanup without masking the original failure.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
