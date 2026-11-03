using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementPngFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "statement-png-tests", Guid.NewGuid().ToString("N"));
    private static StatementExportIdentity Identity => StatementExportIdentity.Create("测试服务有限公司工会", "0507", 9);
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02, 0x03];

    public StatementPngFileTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task PublishesScreenshotWithStatementNamingAndPreservesBytes()
    {
        var file = StatementPngFile.Create(directory, Identity);
        await File.WriteAllBytesAsync(file.TempPath, Png);

        var final = await file.CompleteAsync(CancellationToken.None);
        file.Dispose();

        Assert.Equal(Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账.png"), final);
        Assert.Equal(Png, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(file.TempPath));
    }

    [Fact]
    public async Task ExistingScreenshotsArePreservedAndNextSuffixIsUsed()
    {
        var first = Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账.png");
        var second = Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账 (2).png");
        await File.WriteAllTextAsync(first, "existing one");
        await File.WriteAllTextAsync(second, "existing two");
        using var file = StatementPngFile.Create(directory, Identity);
        await File.WriteAllBytesAsync(file.TempPath, Png);

        var final = await file.CompleteAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账 (3).png"), final);
        Assert.Equal("existing one", await File.ReadAllTextAsync(first));
        Assert.Equal("existing two", await File.ReadAllTextAsync(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a png")]
    public async Task InvalidCaptureNeverPublishesAndRemovesItsTemporaryFile(string content)
    {
        using var file = StatementPngFile.Create(directory, Identity);
        await File.WriteAllTextAsync(file.TempPath, content);

        await Assert.ThrowsAsync<InvalidDataException>(() => file.CompleteAsync(CancellationToken.None));

        Assert.False(File.Exists(file.TempPath));
        Assert.Empty(Directory.GetFiles(directory, "*.png"));
    }

    [Fact]
    public async Task CancelledCaptureIsNotPublished()
    {
        using var file = StatementPngFile.Create(directory, Identity);
        await File.WriteAllBytesAsync(file.TempPath, Png);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.CompleteAsync(new CancellationToken(true)));

        Assert.False(File.Exists(file.TempPath));
        Assert.Empty(Directory.GetFiles(directory, "*.png"));
    }

    [Fact]
    public async Task ConcurrentCapturesNeverOverwriteEachOther()
    {
        var files = Enumerable.Range(0, 8).Select(_ => StatementPngFile.Create(directory, Identity)).ToArray();
        try
        {
            for (var index = 0; index < files.Length; index++)
                await File.WriteAllBytesAsync(files[index].TempPath, [.. Png, (byte)index]);

            var completed = await Task.WhenAll(files.Select(file =>
                Task.Run(() => file.CompleteAsync(CancellationToken.None))));

            Assert.Equal(files.Length, completed.Distinct().Count());
            Assert.All(completed, path => Assert.EndsWith(".png", path));
            for (var index = 0; index < completed.Length; index++)
                Assert.Equal([.. Png, (byte)index], await File.ReadAllBytesAsync(completed[index]));
        }
        finally
        {
            foreach (var file in files)
                file.Dispose();
        }
    }

    [Fact]
    public async Task DisposedCaptureCannotComplete()
    {
        var file = StatementPngFile.Create(directory, Identity);
        await File.WriteAllBytesAsync(file.TempPath, Png);
        file.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.CompleteAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.png"));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
