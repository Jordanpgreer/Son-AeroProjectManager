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
    public async Task MissingMetadataOnlyFileFailsIntegrityCheckWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "drawing-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));

            Assert.False(await store.VerifyHashAsync(string.Empty, string.Empty, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

    [Fact]
    public async Task ImageUploadPreservesItsSafeExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engineering-image-store-{Guid.NewGuid():N}");
        try
        {
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));
            var image = FormFile([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "drawing.png", "image/png");

            var stored = await store.StoreRevisionAsync(
                7, "ACME", "IMG-100", "A", image, null, CancellationToken.None);

            Assert.Equal(".png", Path.GetExtension(stored.PdfRelativePath));
            Assert.True(File.Exists(store.ResolvePath(stored.PdfRelativePath)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SupplementalUploadUsesASeparatePackageAndRetainsItsHash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engineering-supplemental-store-{Guid.NewGuid():N}");
        try
        {
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));
            var content = Encoding.UTF8.GetBytes("supplemental calculation data");
            var document = FormFile(content, "analysis.csv", "text/csv");

            var stored = await store.StoreSupplementalAsync(
                9, "ACME", "DOC-200", document, CancellationToken.None);

            Assert.Contains($"{Path.DirectorySeparatorChar}Supplemental{Path.DirectorySeparatorChar}", stored.RelativePath);
            Assert.Equal(".csv", Path.GetExtension(stored.RelativePath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), stored.Hash);
            Assert.True(File.Exists(store.ResolvePath(stored.RelativePath)));
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
