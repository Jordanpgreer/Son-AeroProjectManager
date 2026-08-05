using System.Security.Cryptography;
using System.Text.Json;
using EngineeringHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Engineering;

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

    Task<string> ResolvePathAsync(string relativePath, CancellationToken cancellationToken);
    Task<bool> VerifyHashAsync(string relativePath, string expectedHash, CancellationToken cancellationToken);
    Task<IStagedFileDeletion> StageDeletionAsync(string pdfRelativePath, CancellationToken cancellationToken);
    Task<DrawingStorageStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDesignAuthoritiesAsync(CancellationToken cancellationToken);
    Task<string?> ResolveDesignAuthorityAsync(string authority, CancellationToken cancellationToken);
    Task EnsureDesignAuthoritiesAsync(IEnumerable<string> authorities, CancellationToken cancellationToken);
}

public interface IStagedFileDeletion : IAsyncDisposable
{
    Task CompleteAsync(CancellationToken cancellationToken);
}

public sealed class DrawingFileStore : IDrawingFileStore
{
    private readonly DrawingStorageOptions _options;
    private readonly EngineeringRoleDbContext? _settingsDb;

    public DrawingFileStore(
        IOptions<DrawingStorageOptions> options,
        EngineeringRoleDbContext? settingsDb = null)
    {
        _options = options.Value;
        _settingsDb = settingsDb;
    }

    public async Task<StoredRevisionFiles> StoreRevisionAsync(
        int drawingId,
        string customer,
        string drawingNumber,
        string revisionNumber,
        IFormFile pdf,
        IFormFile? source,
        CancellationToken cancellationToken)
    {
        var roots = await GetRootsAsync(cancellationToken);
        var root = roots.Active;
        var canonicalAuthority = ResolveDesignAuthority(root, customer)
            ?? throw new InvalidOperationException("The selected design authority is not an approved folder in Engineering storage.");
        Directory.CreateDirectory(root);

        // The immutable package ID makes every upload create a new location; files are never replaced in place.
        var packageId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var relativeFolder = Path.Combine(
            canonicalAuthority,
            $"{SafeSegment(drawingNumber)}-{drawingId}",
            $"Rev-{SafeSegment(revisionNumber)}",
            packageId);
        var folder = ResolvePathUnderRoot(root, relativeFolder);
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
        var roots = await GetRootsAsync(cancellationToken);
        var root = roots.Active;
        var canonicalAuthority = ResolveDesignAuthority(root, customer)
            ?? throw new InvalidOperationException("The selected design authority is not an approved folder in Engineering storage.");
        Directory.CreateDirectory(root);
        var packageId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var relativeFolder = Path.Combine(
            canonicalAuthority,
            $"{SafeSegment(drawingNumber)}-{drawingId}",
            "Supplemental",
            packageId);
        var folder = ResolvePathUnderRoot(root, relativeFolder);
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

    public async Task<string> ResolvePathAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = await GetRootsAsync(cancellationToken);
        foreach (var root in roots.All)
        {
            var candidate = ResolvePathUnderRoot(root, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return ResolvePathUnderRoot(roots.Active, relativePath);
    }

    public async Task<bool> VerifyHashAsync(string relativePath, string expectedHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(expectedHash)) return false;
        var path = await ResolvePathAsync(relativePath, cancellationToken);
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IStagedFileDeletion> StageDeletionAsync(string pdfRelativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pdfPath = await ResolvePathAsync(pdfRelativePath, cancellationToken);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("The revision drawing file could not be found on the drawing share.", pdfPath);

        var packageFolder = Directory.GetParent(pdfPath)?.FullName
            ?? throw new InvalidOperationException("The revision package folder is invalid.");
        var roots = await GetRootsAsync(cancellationToken);
        var root = roots.All.First(candidate => IsUnderRoot(candidate, pdfPath));
        var deletionRoot = Path.Combine(root, ".pending-deletions");
        Directory.CreateDirectory(deletionRoot);
        var stagedFolder = Path.Combine(deletionRoot, Guid.NewGuid().ToString("N"));
        Directory.Move(packageFolder, stagedFolder);
        return new StagedFileDeletion(packageFolder, stagedFolder);
    }

    public async Task<DrawingStorageStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configured = !string.IsNullOrWhiteSpace(_options.RootPath) ||
            (_settingsDb is not null && await _settingsDb.StorageSettings.AsNoTracking()
                .AnyAsync(setting => setting.Id == EngineeringStorageSchema.SettingsId, cancellationToken));
        try
        {
            var root = (await GetRootsAsync(cancellationToken)).Active;
            var isNetwork = EngineeringStoragePolicy.IsUncPath(root);
            EngineeringStoragePolicy.VerifyWritable(root);
            return new(true, isNetwork, true, isNetwork
                ? "The drawing network share is reachable and writable by the application identity."
                : "Development file storage is writable; production requires a UNC network share.");
        }
        catch (Exception exception)
        {
            return new(configured, false, false,
                $"The configured drawing share is unavailable or not writable: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> GetDesignAuthoritiesAsync(CancellationToken cancellationToken)
    {
        var root = (await GetRootsAsync(cancellationToken)).Active;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The configured drawing storage root is unavailable.");
        return EngineeringStoragePolicy.EnumerateAuthorities(root);
    }

    public async Task<string?> ResolveDesignAuthorityAsync(string authority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authority)) return null;
        var root = (await GetRootsAsync(cancellationToken)).Active;
        return ResolveDesignAuthority(root, authority);
    }

    public async Task EnsureDesignAuthoritiesAsync(
        IEnumerable<string> authorities,
        CancellationToken cancellationToken)
    {
        var root = (await GetRootsAsync(cancellationToken)).Active;
        Directory.CreateDirectory(root);
        foreach (var authority in authorities
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(EngineeringStoragePolicy.AuthorityPath(root, authority));
        }
    }

    private async Task<StorageRoots> GetRootsAsync(CancellationToken cancellationToken)
    {
        EngineeringStorageSettingRecord? setting = null;
        if (_settingsDb is not null)
        {
            setting = await _settingsDb.StorageSettings.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == EngineeringStorageSchema.SettingsId, cancellationToken);
        }

        var active = EngineeringStoragePolicy.NormalizeRoot(
            setting?.RootPath ?? _options.RootPath,
            _options.RequireUncPath);
        var previous = DeserializeRoots(setting?.PreviousRootPathsJson)
            .Select(path => EngineeringStoragePolicy.NormalizeRoot(path, requireUncPath: false))
            .Where(path => !string.Equals(path, active, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new StorageRoots(active, previous);
    }

    private static string? ResolveDesignAuthority(string root, string authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return null;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The configured drawing storage root is unavailable.");
        return EngineeringStoragePolicy.EnumerateAuthorities(root).SingleOrDefault(candidate =>
            string.Equals(candidate, authority.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DeserializeRoots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ResolvePathUnderRoot(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsUnderRoot(root, path))
            throw new InvalidOperationException("The stored drawing path is outside the configured drawing share.");
        return path;
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StorageRoots(string Active, IReadOnlyList<string> Previous)
    {
        public IEnumerable<string> All => new[] { Active }.Concat(Previous);
    }

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
