using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace EngineeringHub.Api.Services;

public sealed class DrawingStorageOptions
{
    public const string SectionName = "DrawingStorage";
    public string RootPath { get; set; } = string.Empty;
    public bool RequireUncPath { get; set; } = true;
}

public sealed record StoredRevisionFiles(
    string PdfRelativePath,
    string PdfHash,
    string? SourceRelativePath);

public sealed record StoredSupplementalFile(string RelativePath, string Hash);

public sealed record DrawingStorageStatus(
    bool Configured,
    bool IsNetworkPath,
    bool Available,
    string Message);

public interface IDrawingFileStore
{
    Task<StoredRevisionFiles> StoreRevisionAsync(
        int drawingId,
        string customer,
        string drawingNumber,
        string revisionNumber,
        IFormFile pdf,
        IFormFile? source,
        CancellationToken cancellationToken);

    Task<StoredSupplementalFile> StoreSupplementalAsync(
        int drawingId,
        string customer,
        string drawingNumber,
        IFormFile document,
        CancellationToken cancellationToken);

    string ResolvePath(string relativePath);
    Task<bool> VerifyHashAsync(string relativePath, string expectedHash, CancellationToken cancellationToken);
    Task<IStagedFileDeletion> StageDeletionAsync(string pdfRelativePath, CancellationToken cancellationToken);
    DrawingStorageStatus GetStatus();
}

public interface IStagedFileDeletion : IAsyncDisposable
{
    Task CompleteAsync(CancellationToken cancellationToken);
}

public sealed class DrawingFileStore(IOptions<DrawingStorageOptions> options) : IDrawingFileStore
{
    private readonly DrawingStorageOptions _options = options.Value;

    public async Task<StoredRevisionFiles> StoreRevisionAsync(
        int drawingId,
        string customer,
        string drawingNumber,
        string revisionNumber,
        IFormFile pdf,
        IFormFile? source,
        CancellationToken cancellationToken)
    {
        var root = GetValidatedRoot();
        Directory.CreateDirectory(root);

        // The immutable package ID makes every upload create a new location; files are never replaced in place.
        var packageId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var relativeFolder = Path.Combine(
            SafeSegment(customer),
            $"{SafeSegment(drawingNumber)}-{drawingId}",
            $"Rev-{SafeSegment(revisionNumber)}",
            packageId);
        var folder = ResolvePath(relativeFolder);
        Directory.CreateDirectory(folder);

        try
        {
            var pdfPath = Path.Combine(folder, "drawing" + SafeExtension(pdf.FileName));
            var hash = await WriteNewAndHashAsync(pdf, pdfPath, cancellationToken);
            string? sourcePath = null;
            if (source is { Length: > 0 })
            {
                sourcePath = Path.Combine(folder, "source" + SafeExtension(source.FileName));
                await WriteNewAndHashAsync(source, sourcePath, cancellationToken);
            }

            return new StoredRevisionFiles(
                Path.GetRelativePath(root, pdfPath),
                hash,
                sourcePath is null ? null : Path.GetRelativePath(root, sourcePath));
        }
        catch
        {
            // Cleanup is limited to the unique package directory created by this failed upload.
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    public async Task<StoredSupplementalFile> StoreSupplementalAsync(
        int drawingId,
        string customer,
        string drawingNumber,
        IFormFile document,
        CancellationToken cancellationToken)
    {
        var root = GetValidatedRoot();
        Directory.CreateDirectory(root);
        var packageId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var relativeFolder = Path.Combine(
            SafeSegment(customer),
            $"{SafeSegment(drawingNumber)}-{drawingId}",
            "Supplemental",
            packageId);
        var folder = ResolvePath(relativeFolder);
        Directory.CreateDirectory(folder);

        try
        {
            var path = Path.Combine(folder, "document" + SafeExtension(document.FileName));
            var hash = await WriteNewAndHashAsync(document, path, cancellationToken);
            return new StoredSupplementalFile(Path.GetRelativePath(root, path), hash);
        }
        catch
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    public string ResolvePath(string relativePath)
    {
        var root = GetValidatedRoot();
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stored drawing path is outside the configured drawing share.");
        return path;
    }

    public async Task<bool> VerifyHashAsync(string relativePath, string expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(expectedHash)) return false;
        var path = ResolvePath(relativePath);
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public Task<IStagedFileDeletion> StageDeletionAsync(string pdfRelativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pdfPath = ResolvePath(pdfRelativePath);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("The revision drawing file could not be found on the drawing share.", pdfPath);

        var packageFolder = Directory.GetParent(pdfPath)?.FullName
            ?? throw new InvalidOperationException("The revision package folder is invalid.");
        var root = GetValidatedRoot();
        var deletionRoot = Path.Combine(root, ".pending-deletions");
        Directory.CreateDirectory(deletionRoot);
        var stagedFolder = Path.Combine(deletionRoot, Guid.NewGuid().ToString("N"));
        Directory.Move(packageFolder, stagedFolder);
        return Task.FromResult<IStagedFileDeletion>(new StagedFileDeletion(packageFolder, stagedFolder));
    }

    public DrawingStorageStatus GetStatus()
    {
        try
        {
            var root = GetValidatedRoot();
            var isNetwork = IsUncPath(root);
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".drawing-storage-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(probe);
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            return new(true, isNetwork, true, isNetwork
                ? "The drawing network share is reachable and writable by the application identity."
                : "Development file storage is writable; production requires a UNC network share.");
        }
        catch (Exception exception)
        {
            return new(!string.IsNullOrWhiteSpace(_options.RootPath), false, false,
                $"The configured drawing share is unavailable or not writable: {exception.Message}");
        }
    }

    private string GetValidatedRoot()
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath))
            throw new InvalidOperationException("DrawingStorage:RootPath is not configured.");
        var root = Path.GetFullPath(_options.RootPath);
        if (_options.RequireUncPath && !IsUncPath(root))
            throw new InvalidOperationException("Production drawing storage must use a UNC path such as \\\\server\\share\\Engineering\\Drawings.");
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);
    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Unspecified" : cleaned;
    }

    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length is > 0 and <= 16 && extension.Skip(1).All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : ".bin";
    }

    private static async Task<string> WriteNewAndHashAsync(IFormFile file, string finalPath, CancellationToken cancellationToken)
    {
        var temporaryPath = finalPath + ".uploading";
        await using var input = file.OpenReadStream();
        await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.TransformBlock(buffer, 0, read, null, 0);
        }
        hash.TransformFinalBlock([], 0, 0);
        await output.FlushAsync(cancellationToken);
        output.Close();
        File.Move(temporaryPath, finalPath, overwrite: false);
        return Convert.ToHexString(hash.Hash!);
    }

    private sealed class StagedFileDeletion(string originalFolder, string stagedFolder) : IStagedFileDeletion
    {
        private bool _completed;

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Once the database commits, a cleanup failure must not restore a package whose record is gone.
            _completed = true;
            if (Directory.Exists(stagedFolder)) Directory.Delete(stagedFolder, recursive: true);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed && Directory.Exists(stagedFolder) && !Directory.Exists(originalFolder))
                Directory.Move(stagedFolder, originalFolder);
            return ValueTask.CompletedTask;
        }
    }
}
