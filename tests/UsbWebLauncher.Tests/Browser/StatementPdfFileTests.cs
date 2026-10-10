using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class StatementPdfFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "statement-policy-tests", Guid.NewGuid().ToString("N"));
    private static StatementExportIdentity Identity => StatementExportIdentity.Create("测试服务有限公司工会", "0507", 9);
    private const string Pdf = "%PDF-1.7\noriginal bank bytes\n%%EOF";

    public StatementPdfFileTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ReceiptNamesDoNotCollideWithStatementsAndPreserveExistingReceipts()
    {
        var statement = Path.Combine(directory, "09-测试公司-建行0001-对账.pdf");
        var receipt = Path.Combine(directory, "09-测试公司-建行0001-回单.pdf");
        var second = Path.Combine(directory, "09-测试公司-建行0001-回单 (2).pdf");
        await File.WriteAllTextAsync(statement, "existing statement");
        await File.WriteAllTextAsync(receipt, "existing receipt");
        await File.WriteAllTextAsync(second, "existing second receipt");
        using var file = StatementPdfFile.Create(directory, StatementExportIdentity.CreateReceipt("测试公司", "0001", 9));
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        var final = await file.CompleteAsync(CancellationToken.None);
        Assert.Equal(Path.Combine(directory, "09-测试公司-建行0001-回单 (3).pdf"), final);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(final));
        Assert.Equal("existing statement", await File.ReadAllTextAsync(statement));
        Assert.Equal("existing receipt", await File.ReadAllTextAsync(receipt));
        Assert.Equal("existing second receipt", await File.ReadAllTextAsync(second));
        Assert.False(File.Exists(file.TempPath));
    }

    [Fact]
    public void ReservesUniqueTemporaryFilesInRequestedDirectory()
    {
        using var first = StatementPdfFile.Create(directory, Identity);
        using var second = StatementPdfFile.Create(directory, Identity);
        Assert.NotEqual(first.TempPath, second.TempPath);
        Assert.Equal(directory, Path.GetDirectoryName(first.TempPath));
        Assert.True(File.Exists(first.TempPath));
        Assert.True(File.Exists(second.TempPath));
        Assert.NotEqual(".pdf", Path.GetExtension(first.TempPath));
    }

    [Fact]
    public async Task PublishesOriginalBytesAndKeepsCompletedFileAfterDisposal()
    {
        var file = StatementPdfFile.Create(directory, Identity);
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        var final = await file.CompleteAsync(CancellationToken.None);
        file.Dispose();
        Assert.Equal(Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账.pdf"), final);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(final));
        Assert.False(File.Exists(file.TempPath));
    }

    [Fact]
    public async Task UnsafeCompanyTextCannotCreateSubdirectoriesOrEscapeTargetDirectory()
    {
        using var file = StatementPdfFile.Create(directory, StatementExportIdentity.Create("../测试\\公司:财务", "0001", 9));
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        var final = await file.CompleteAsync(CancellationToken.None);
        Assert.Equal(directory, Path.GetDirectoryName(final));
        Assert.Equal("09-.._测试_公司_财务-建行0001-对账.pdf", Path.GetFileName(final));
        Assert.Equal(Pdf, await File.ReadAllTextAsync(final));
        Assert.Empty(Directory.GetDirectories(directory));
    }

    [Fact]
    public async Task DifferentAccountsRetainDifferentNames()
    {
        using var first = StatementPdfFile.Create(directory, StatementExportIdentity.Create("测试公司", "0001", 9));
        using var second = StatementPdfFile.Create(directory, StatementExportIdentity.Create("测试公司", "0002", 9));
        await File.WriteAllTextAsync(first.TempPath, Pdf);
        await File.WriteAllTextAsync(second.TempPath, Pdf);
        Assert.Equal("09-测试公司-建行0001-对账.pdf", Path.GetFileName(await first.CompleteAsync(CancellationToken.None)));
        Assert.Equal("09-测试公司-建行0002-对账.pdf", Path.GetFileName(await second.CompleteAsync(CancellationToken.None)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PD")]
    [InlineData("<html>session expired</html>")]
    [InlineData("prefix%PDF-1.7")]
    public async Task InvalidDownloadNeverPublishesAndCleansOnlyItsOwnTemporaryFile(string content)
    {
        using var file = StatementPdfFile.Create(directory, Identity);
        var unrelated = Path.Combine(directory, "other.download");
        await File.WriteAllTextAsync(unrelated, "keep");
        await File.WriteAllTextAsync(file.TempPath, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => file.CompleteAsync(CancellationToken.None));
        Assert.False(File.Exists(file.TempPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf"));
    }

    [Fact]
    public async Task ExistingNamesArePreservedAndNextSuffixIsUsed()
    {
        var first = Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账.pdf");
        var second = Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账 (2).pdf");
        await File.WriteAllTextAsync(first, "original one");
        await File.WriteAllTextAsync(second, "original two");
        using var file = StatementPdfFile.Create(directory, Identity);
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        Assert.Equal(Path.Combine(directory, "09-测试服务有限公司工会-建行0507-对账 (3).pdf"), await file.CompleteAsync(CancellationToken.None));
        Assert.Equal("original one", await File.ReadAllTextAsync(first));
        Assert.Equal("original two", await File.ReadAllTextAsync(second));
    }

    [Theory]
    [InlineData(ExportDocumentKind.Statement)]
    [InlineData(ExportDocumentKind.ElectronicReceipt)]
    public async Task ConcurrentExportsNeverOverwriteEachOther(ExportDocumentKind kind)
    {
        var identity = StatementExportIdentity.Create("测试公司", "0507", 9, kind);
        var files = Enumerable.Range(0, 12).Select(_ => StatementPdfFile.Create(directory, identity)).ToArray();
        try
        {
            for (var i = 0; i < files.Length; i++)
                await File.WriteAllTextAsync(files[i].TempPath, Pdf + i);
            var completed = await Task.WhenAll(files.Select(file => Task.Run(() => file.CompleteAsync(CancellationToken.None))));
            Assert.Equal(files.Length, completed.Distinct().Count());
            Assert.All(completed, path => Assert.StartsWith(identity.FileStem, Path.GetFileName(path)));
            for (var i = 0; i < completed.Length; i++)
                Assert.Equal(Pdf + i, await File.ReadAllTextAsync(completed[i]));
        }
        finally { foreach (var file in files) file.Dispose(); }
    }

    [Fact]
    public async Task CancelledExportRemovesTemporaryFileWithoutPublishing()
    {
        using var file = StatementPdfFile.Create(directory, Identity);
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.CompleteAsync(new CancellationToken(true)));
        Assert.False(File.Exists(file.TempPath));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf"));
    }

    [Fact]
    public async Task RepeatedCompletionReturnsSameFileWithoutPublishingAnotherCopy()
    {
        using var file = StatementPdfFile.Create(directory, Identity);
        await File.WriteAllTextAsync(file.TempPath, Pdf);
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => file.CompleteAsync(CancellationToken.None))));
        Assert.Single(paths.Distinct());
        Assert.Single(Directory.GetFiles(directory, "*.pdf"));
    }

    [Fact]
    public async Task MissingTemporaryFileCannotReportSuccess()
    {
        using var file = StatementPdfFile.Create(directory, Identity);
        File.Delete(file.TempPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => file.CompleteAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf"));
    }

    [Fact]
    public async Task DownloadStillBeingWrittenCannotBePublished()
    {
        using var file = StatementPdfFile.Create(directory, Identity);
        using (var writer = new FileStream(file.TempPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            writer.Write("%PDF-1.7"u8);
            writer.Flush();
            await Assert.ThrowsAsync<IOException>(() => file.CompleteAsync(CancellationToken.None));
            Assert.Empty(Directory.GetFiles(directory, "*.pdf"));
        }
        file.Dispose();
        Assert.False(File.Exists(file.TempPath));
    }

    [Fact]
    public async Task DisposedExportCannotCompleteAndDoesNotDeleteOtherFiles()
    {
        var file = StatementPdfFile.Create(directory, Identity);
        var other = Path.Combine(directory, "keep.pdf");
        await File.WriteAllTextAsync(other, Pdf);
        file.Dispose();
        file.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.CompleteAsync(CancellationToken.None));
        Assert.False(File.Exists(file.TempPath));
        Assert.Equal(Pdf, await File.ReadAllTextAsync(other));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}


