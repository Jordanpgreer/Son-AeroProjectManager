using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.AspNetCore.Http;

namespace EstimatingDashboard.Api.Services;

public sealed partial class QuoteStatusService
{
    private const long MaxQuoteFileBytes = 50L * 1024 * 1024;
    private static readonly HashSet<string> AllowedQuoteFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".msg", ".eml", ".xls", ".xlsx", ".xlsm", ".doc", ".docx"
    };

    public async Task<QuoteStatusDetailDto> UpsertFileLocationAsync(
        int id,
        UpdateQuoteFileLocationDto dto,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var quote = await vendors.QuoteAsync(id, access, true, cancellationToken);
        VendorQuoteService.Version(quote.Version, dto.ExpectedVersion);
        var next = ValidPath(dto.Path);
        var current = FileLocations(quote);
        var previous = string.IsNullOrWhiteSpace(dto.PreviousPath) ? null : ValidPath(dto.PreviousPath);
        if (previous is not null && !current.Any(location => SamePath(location.Path, previous)))
            throw new VendorQuoteException(409, "This file location changed in another session. Refresh and try again.");

        var sourcePaths = SourcePaths(quote);
        var overrides = JsonStrings(quote.ArdaFilePathOverridesJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressed = JsonStrings(quote.ArdaSuppressedFilePathsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (previous is not null)
        {
            overrides.RemoveWhere(path => SamePath(path, previous));
            if (sourcePaths.Any(path => SamePath(path, previous)) && !SamePath(previous, next))
                suppressed.Add(previous);
        }
        suppressed.RemoveWhere(path => SamePath(path, next));
        if (!sourcePaths.Any(path => SamePath(path, next))) overrides.Add(next);

        if (previous is null && current.Any(location => SamePath(location.Path, next)))
            return await DetailAsync(id, access, cancellationToken);

        quote.ArdaFilePathOverridesJson = JsonSerializer.Serialize(overrides.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        quote.ArdaSuppressedFilePathsJson = JsonSerializer.Serialize(suppressed.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        quote.QuoteFolderPath = FileLocations(quote).FirstOrDefault()?.Path;
        Activity(quote, "file-location", previous is null ? "File location added" : "File location updated", access, previous, next);
        Touch(quote, access);
        await vendors.SaveAsync(cancellationToken);
        return await DetailAsync(id, access, cancellationToken);
    }

    public async Task<QuoteStatusDetailDto> RemoveFileLocationAsync(
        int id,
        RemoveQuoteFileLocationDto dto,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var quote = await vendors.QuoteAsync(id, access, true, cancellationToken);
        VendorQuoteService.Version(quote.Version, dto.ExpectedVersion);
        var path = ValidPath(dto.Path);
        if (!FileLocations(quote).Any(location => SamePath(location.Path, path)))
            throw new VendorQuoteException(409, "This file location changed in another session. Refresh and try again.");
        var overrides = JsonStrings(quote.ArdaFilePathOverridesJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        overrides.RemoveWhere(candidate => SamePath(candidate, path));
        var suppressed = JsonStrings(quote.ArdaSuppressedFilePathsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (SourcePaths(quote).Any(candidate => SamePath(candidate, path))) suppressed.Add(path);
        quote.ArdaFilePathOverridesJson = JsonSerializer.Serialize(overrides.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        quote.ArdaSuppressedFilePathsJson = JsonSerializer.Serialize(suppressed.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        quote.QuoteFolderPath = FileLocations(quote).FirstOrDefault()?.Path;
        Activity(quote, "file-location", "File location removed", access, path, null);
        Touch(quote, access);
        await vendors.SaveAsync(cancellationToken);
        return await DetailAsync(id, access, cancellationToken);
    }

    public async Task<QuoteStatusDetailDto> CopyFileToLocationAsync(
        int id,
        int expectedVersion,
        string path,
        string collision,
        IFormFile file,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var quote = await vendors.QuoteAsync(id, access, true, cancellationToken);
        VendorQuoteService.Version(quote.Version, expectedVersion);
        path = ValidPath(path);
        if (!FileLocations(quote).Any(location => SamePath(location.Path, path)))
            throw new VendorQuoteException(400, "Choose a file location that is currently attached to this quote.");
        if (!Directory.Exists(path))
            throw new VendorQuoteException(409, "The file location is unavailable from the Arda server. Confirm the S-drive mapping and server permissions.", "FileLocationUnavailable");
        if (file.Length <= 0 || file.Length > MaxQuoteFileBytes)
            throw new VendorQuoteException(400, "Choose a non-empty file no larger than 50 MB.");
        var fileName = Path.GetFileName(file.FileName).Trim();
        if (fileName.Length == 0 || fileName.Length > 180 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new VendorQuoteException(400, "The file name is invalid or longer than 180 characters.");
        if (!AllowedQuoteFileExtensions.Contains(Path.GetExtension(fileName)))
            throw new VendorQuoteException(400, "Only PDF, Outlook email, Excel, and Word files can be copied to a quote location.");

        var target = SafeChildPath(path, fileName);
        if (File.Exists(target))
        {
            if (collision.Equals("rename", StringComparison.OrdinalIgnoreCase))
                target = AvailableName(path, fileName);
            else if (!collision.Equals("overwrite", StringComparison.OrdinalIgnoreCase))
                throw new VendorQuoteException(409, $"{fileName} already exists in this location.", "FileAlreadyExists");
        }

        var temporary = SafeChildPath(path, $".arda-{Guid.NewGuid():N}.upload");
        try
        {
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await file.CopyToAsync(destination, cancellationToken);
            File.Move(temporary, target, collision.Equals("overwrite", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException exception)
        {
            throw new VendorQuoteException(409, $"The file could not be copied: {exception.Message}", "FileCopyConflict");
        }
        catch (UnauthorizedAccessException)
        {
            throw new VendorQuoteException(
                403,
                "The Arda server does not have permission to write to this file location. Contact IT to grant the application service account access.",
                "FileLocationUnavailable");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // A failed best-effort cleanup should not replace the original upload result.
            }
            catch (UnauthorizedAccessException)
            {
                // The service account may lose access while a network location is unavailable.
            }
        }

        Activity(quote, "file-copy", "File copied to quote location", access, null, Path.GetFileName(target));
        Touch(quote, access);
        await vendors.SaveAsync(cancellationToken);
        return await DetailAsync(id, access, cancellationToken);
    }

    internal static IReadOnlyList<QuoteFileLocationDto> FileLocations(EstimatingQuoteHistoryRecord quote)
    {
        var source = SourcePaths(quote);
        var suppressed = JsonStrings(quote.ArdaSuppressedFilePathsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<QuoteFileLocationDto>();
        foreach (var path in source)
            if (!suppressed.Contains(path) && result.All(location => !SamePath(location.Path, path)))
                result.Add(new(path, "Fulcrum"));
        foreach (var path in JsonStrings(quote.ArdaFilePathOverridesJson))
            if (!suppressed.Contains(path) && result.All(location => !SamePath(location.Path, path)))
                result.Add(new(path, "Arda"));
        return result;
    }

    internal static QuoteProductionWarningsDto ProductionWarnings(EstimatingQuoteHistoryRecord quote) => new(
        JsonObjects<FulcrumQuoteItemReference>(quote.FulcrumQuoteItemsJson)
            .Select(item => new QuoteItemReferenceDto(item.ItemId, item.PartNumber, item.Revision)).ToList(),
        JsonStrings(quote.FulcrumOpWarningsJson),
        quote.FulcrumBuyItemCount,
        quote.FulcrumMakeItemCount,
        quote.FulcrumInspectionUpdatedAt);

    internal static int JsonListCount(string? json) => JsonStrings(json).Count;

    private static bool HasFulcrumWarnings(EstimatingQuoteHistoryRecord quote) =>
        quote.FulcrumBuyItemCount > 0 || quote.FulcrumMakeItemCount > 0 || JsonListCount(quote.FulcrumOpWarningsJson) > 0;

    private static IReadOnlyList<string> SourcePaths(EstimatingQuoteHistoryRecord quote)
    {
        var paths = JsonStrings(quote.FulcrumFilePathsJson).ToList();
        if (paths.Count == 0 && FulcrumQuoteFolderPath.Normalize(quote.QuoteFolderPath) is { } legacy)
            paths.Add(legacy);
        return paths;
    }

    private static IReadOnlyList<string> JsonStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<T> JsonObjects<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ValidPath(string? path) => FulcrumQuoteFolderPath.Normalize(path)
        ?? throw new VendorQuoteException(400, "Enter a valid S-drive folder path without traversal or invalid characters.");

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string SafeChildPath(string folder, string fileName)
    {
        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(fullFolder, fileName));
        if (!target.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new VendorQuoteException(400, "The file name does not resolve inside the selected quote folder.");
        return target;
    }

    private static string AvailableName(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            var candidate = SafeChildPath(folder, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new VendorQuoteException(409, "A renamed copy could not be created because too many files share this name.", "FileCopyConflict");
    }
}
