using System.Globalization;
using System.IO;

namespace UsbWebLauncher.App.Browser;

public sealed class StatementPngFile : IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string fileStem;
    private string? completedPath;
    private bool disposed;
    private bool failed;
    private bool ownsTemporaryFile = true;

    private StatementPngFile(string directory, StatementExportIdentity identity, string tempPath)
    {
        this.directory = directory;
        fileStem = identity.FileStem;
        TempPath = tempPath;
    }

    public string TempPath { get; }

    public static StatementPngFile Create(string directory, StatementExportIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(identity);
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        while (true)
        {
            var path = Path.Combine(fullDirectory, $".statement-{Guid.NewGuid():N}.screenshot");
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new StatementPngFile(fullDirectory, identity, path);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }

    public Task<string> CompleteAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (completedPath is not null)
                    return Task.FromResult(completedPath);
                if (failed)
                    throw new InvalidOperationException("本次截图未完成，请重新导出。");

                cancellationToken.ThrowIfCancellationRequested();
                using var source = new FileStream(TempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                Span<byte> signature = stackalloc byte[8];
                if (source.Length < signature.Length)
                    throw new InvalidDataException("截图文件不是有效的 PNG。");
                source.ReadExactly(signature);
                ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
                if (!signature.SequenceEqual(pngSignature))
                    throw new InvalidDataException("截图文件不是有效的 PNG。");

                for (var suffix = 1; ; suffix++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = suffix == 1 ? $"{fileStem}.png" : string.Create(CultureInfo.InvariantCulture, $"{fileStem} ({suffix}).png");
                    var destination = Path.Combine(directory, name);
                    try
                    {
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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
