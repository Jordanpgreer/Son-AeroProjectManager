using System.Security.Cryptography;
using System.Text;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class DrawingFileStoreTests
{
    [Fact]
    public async Task UploadCreatesPermanentPackageAndReturnsRelativeReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engineering-file-store-{Guid.NewGuid():N}");
        try
        {
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));
            var pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4 test drawing");
            var sourceBytes = Encoding.UTF8.GetBytes("source drawing data");
            var pdf = FormFile(pdfBytes, "drawing.pdf", "application/pdf");
            var source = FormFile(sourceBytes, "drawing.dwg", "application/octet-stream");

            var stored = await store.StoreRevisionAsync(
                42, "ACME", "DRW-100", "A", pdf, source, CancellationToken.None);

            Assert.False(Path.IsPathRooted(stored.PdfRelativePath));
            Assert.True(File.Exists(store.ResolvePath(stored.PdfRelativePath)));
            Assert.NotNull(stored.SourceRelativePath);
            Assert.True(File.Exists(store.ResolvePath(stored.SourceRelativePath!)));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(pdfBytes)), stored.PdfHash);
            Assert.True(await store.VerifyHashAsync(stored.PdfRelativePath, stored.PdfHash, CancellationToken.None));

            var second = await store.StoreRevisionAsync(
                42, "ACME", "DRW-100", "A", FormFile(pdfBytes, "drawing.pdf", "application/pdf"), null, CancellationToken.None);
            Assert.NotEqual(stored.PdfRelativePath, second.PdfRelativePath);

            await using (var staged = await store.StageDeletionAsync(second.PdfRelativePath, CancellationToken.None))
                await staged.CompleteAsync(CancellationToken.None);
            Assert.False(File.Exists(store.ResolvePath(second.PdfRelativePath)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static FormFile FormFile(byte[] content, string fileName, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
