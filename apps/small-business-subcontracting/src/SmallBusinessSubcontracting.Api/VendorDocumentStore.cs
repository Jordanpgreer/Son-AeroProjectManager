using System.Security.Cryptography;

namespace SmallBusinessSubcontracting.Api;

public sealed record StoredVendorDocument(
    string OriginalFileName,
    string RelativePath,
    string ContentType,
    long FileSize,
    string FileHash);

public sealed class VendorDocumentStore(IConfiguration configuration, IHostEnvironment environment)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt"
    };

    public void EnsureConfigured() => ResolveRoot();

    public void VerifyAccessible()
    {
        var root = ResolveRoot(createDirectory: false);
        using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
        _ = entries.MoveNext();
    }

    public async Task VerifyWritableAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResolveRoot(), $".arda-write-probe-{Guid.NewGuid():N}.tmp");
        // Only our uniquely named probe is touched. DeleteOnClose exercises the same
        // share/NTFS delete permission used for document cleanup, including on errors.
        await using (var probe = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
        {
            await probe.WriteAsync(new byte[] { 0x41 }, cancellationToken);
            await probe.FlushAsync(cancellationToken);
            probe.Position = 0;
            var value = new byte[1];
            if (await probe.ReadAsync(value, cancellationToken) != 1 || value[0] != 0x41)
                throw new IOException("The vendor document storage read/write check failed.");
        }
        if (File.Exists(path)) throw new IOException("The vendor document storage cleanup check failed.");
    }

    public async Task<StoredVendorDocument> SaveAsync(
        int vendorId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0) throw new InvalidOperationException("Choose a non-empty document.");
        var maxBytes = configuration.GetValue<long>("VendorDocumentStorage:MaximumFileBytes", 20 * 1024 * 1024);
        if (file.Length > maxBytes)
            throw new InvalidOperationException($"Documents cannot exceed {maxBytes / 1024 / 1024:N0} MB.");

        var originalName = Path.GetFileName(file.FileName).Trim();
        if (originalName.Length == 0 || originalName.Length > 255)
            throw new InvalidOperationException("The document file name is invalid or too long.");
        var extension = Path.GetExtension(originalName);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException("Upload a PDF, Word, Excel, CSV, or text document.");

        var root = ResolveRoot();
        var relativePath = Path.Combine(vendorId.ToString(), DateTime.UtcNow.Year.ToString(), $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        var destination = ResolvePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".upload";
        try
        {
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            string hash;
            await using (var hashStream = File.OpenRead(temporary))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            File.Move(temporary, destination);
            return new StoredVendorDocument(
                originalName,
                relativePath.Replace('\\', '/'),
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.Length,
                hash);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public FileStream OpenRead(string relativePath)
    {
        var path = ResolvePath(ResolveRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException("The stored document file could not be found.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void DeleteIfPresent(string relativePath)
    {
        var path = ResolvePath(ResolveRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }

    private string ResolveRoot(bool createDirectory = true)
    {
        var configured = configuration["VendorDocumentStorage:RootPath"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "subcontracting-files")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured);
        root = Path.GetFullPath(root);
        if (!environment.IsDevelopment()
            && configuration.GetValue("VendorDocumentStorage:RequireUncPath", true)
            && !root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "VendorDocumentStorage:RootPath must be a UNC network path outside Development.");
        if (createDirectory) Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolvePath(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The document path is outside the configured storage root.");
        return full;
    }
}
