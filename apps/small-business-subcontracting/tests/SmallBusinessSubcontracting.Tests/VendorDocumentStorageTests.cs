using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SmallBusinessSubcontracting.Api;

namespace SmallBusinessSubcontracting.Tests;

public sealed class VendorDocumentStorageTests
{
    [Fact]
    public async Task Document_upload_read_and_cleanup_work_with_Windows_file_sharing()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"subcontracting-document-{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VendorDocumentStorage:RootPath"] = temporary
        }).Build();
        var store = new VendorDocumentStore(config, new TestEnvironment { EnvironmentName = "Development" });
        var bytes = "%PDF-1.7 Test document"u8.ToArray();
        using var input = new MemoryStream(bytes);
        var file = new FormFile(input, 0, bytes.Length, "file", "certification.pdf")
        {
            Headers = new HeaderDictionary(), ContentType = "application/pdf"
        };
        try
        {
            await store.VerifyWritableAsync(CancellationToken.None);
            var document = await store.SaveAsync(123, file, CancellationToken.None);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), document.FileHash);
            Assert.StartsWith("123/", document.RelativePath);
            using (var output = new MemoryStream())
            {
                await using var saved = store.OpenRead(document.RelativePath);
                await saved.CopyToAsync(output);
                Assert.Equal(bytes, output.ToArray());
            }
            Assert.Throws<InvalidOperationException>(() => store.OpenRead("../../outside.pdf"));
            store.DeleteIfPresent(document.RelativePath);
            Assert.Throws<FileNotFoundException>(() => store.OpenRead(document.RelativePath));
            Assert.Empty(Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}
