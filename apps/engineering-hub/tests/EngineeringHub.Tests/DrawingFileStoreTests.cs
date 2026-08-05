using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Engineering;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class DrawingFileStoreTests
{
    [Fact]
    public void StoragePolicyPreservesDriveRootsAndRejectsUnsafeAuthorityNames()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Equal(driveRoot, EngineeringStoragePolicy.NormalizeRoot(driveRoot, requireUncPath: false));
        Assert.Throws<InvalidOperationException>(() => EngineeringStoragePolicy.NormalizeDesignAuthority(".."));
        Assert.Throws<InvalidOperationException>(() => EngineeringStoragePolicy.NormalizeDesignAuthority("CON"));
        Assert.Throws<InvalidOperationException>(() => EngineeringStoragePolicy.NormalizeDesignAuthority("Authority."));
    }

    [Fact]
    public async Task AuthorityCatalogComesOnlyFromVisibleRootFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engineering-authority-index-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Zulu Authority"));
            Directory.CreateDirectory(Path.Combine(root, "Alpha Authority"));
            Directory.CreateDirectory(Path.Combine(root, ".pending-deletions"));
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));

            var authorities = await store.GetDesignAuthoritiesAsync(CancellationToken.None);

            Assert.Equal(["Alpha Authority", "Zulu Authority"], authorities);
            Assert.Null(await store.ResolveDesignAuthorityAsync("not approved", CancellationToken.None));
            Assert.Equal("Alpha Authority", await store.ResolveDesignAuthorityAsync("alpha authority", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadRejectsAuthorityWithoutAnIndexedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"engineering-authority-rejection-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreRevisionAsync(
                42,
                "Unapproved Authority",
                "DRW-100",
                "A",
                FormFile(Encoding.UTF8.GetBytes("%PDF-1.4"), "drawing.pdf", "application/pdf"),
                null,
                CancellationToken.None));

            Assert.Contains("not an approved folder", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviousRootRemainsReadableAfterActiveRootChanges()
    {
        var oldRoot = Path.Combine(Path.GetTempPath(), $"engineering-old-root-{Guid.NewGuid():N}");
        var activeRoot = Path.Combine(Path.GetTempPath(), $"engineering-active-root-{Guid.NewGuid():N}");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            Directory.CreateDirectory(activeRoot);
            var relativePath = Path.Combine("Legacy Authority", "DRW-1-1", "Rev-A", "package", "drawing.pdf");
            var legacyPath = Path.Combine(oldRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await File.WriteAllTextAsync(legacyPath, "%PDF-1.4 legacy");
            var db = new EngineeringRoleDbContext(new DbContextOptionsBuilder<EngineeringRoleDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            db.StorageSettings.Add(new EngineeringStorageSettingRecord
            {
                Id = 1,
                RootPath = activeRoot,
                PreviousRootPathsJson = JsonSerializer.Serialize(new[] { oldRoot }),
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test"
            });
            await db.SaveChangesAsync();
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = activeRoot,
                RequireUncPath = false
            }), db);

            var resolved = await store.ResolvePathAsync(relativePath, CancellationToken.None);

            Assert.Equal(legacyPath, resolved);
        }
        finally
        {
            if (Directory.Exists(oldRoot)) Directory.Delete(oldRoot, recursive: true);
            if (Directory.Exists(activeRoot)) Directory.Delete(activeRoot, recursive: true);
        }
    }

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
            await store.EnsureDesignAuthoritiesAsync(["ACME"], CancellationToken.None);

            var stored = await store.StoreRevisionAsync(
                42, "ACME", "DRW-100", "A", pdf, source, CancellationToken.None);

            Assert.False(Path.IsPathRooted(stored.PdfRelativePath));
            Assert.True(File.Exists(await store.ResolvePathAsync(stored.PdfRelativePath, CancellationToken.None)));
            Assert.NotNull(stored.SourceRelativePath);
            Assert.True(File.Exists(await store.ResolvePathAsync(stored.SourceRelativePath!, CancellationToken.None)));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(pdfBytes)), stored.PdfHash);
            Assert.True(await store.VerifyHashAsync(stored.PdfRelativePath, stored.PdfHash, CancellationToken.None));

            var second = await store.StoreRevisionAsync(
                42, "ACME", "DRW-100", "A", FormFile(pdfBytes, "drawing.pdf", "application/pdf"), null, CancellationToken.None);
            Assert.NotEqual(stored.PdfRelativePath, second.PdfRelativePath);

            await using (var staged = await store.StageDeletionAsync(second.PdfRelativePath, CancellationToken.None))
                await staged.CompleteAsync(CancellationToken.None);
            Assert.False(File.Exists(await store.ResolvePathAsync(second.PdfRelativePath, CancellationToken.None)));
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
            await store.EnsureDesignAuthoritiesAsync(["ACME"], CancellationToken.None);

            var stored = await store.StoreRevisionAsync(
                7, "ACME", "IMG-100", "A", image, null, CancellationToken.None);

            Assert.Equal(".png", Path.GetExtension(stored.PdfRelativePath));
            Assert.True(File.Exists(await store.ResolvePathAsync(stored.PdfRelativePath, CancellationToken.None)));
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
            await store.EnsureDesignAuthoritiesAsync(["ACME"], CancellationToken.None);

            var stored = await store.StoreSupplementalAsync(
                9, "ACME", "DOC-200", document, CancellationToken.None);

            Assert.Contains($"{Path.DirectorySeparatorChar}Supplemental{Path.DirectorySeparatorChar}", stored.RelativePath);
            Assert.Equal(".csv", Path.GetExtension(stored.RelativePath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), stored.Hash);
            Assert.True(File.Exists(await store.ResolvePathAsync(stored.RelativePath, CancellationToken.None)));
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
