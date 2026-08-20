using System.IO.Compression;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Import;

public sealed class WorkCenterWorkbookImportService
{
    public const int MaxWorkbookBytes = 5 * 1024 * 1024;
    public const int MaxWorkCenterCount = 5_000;
    public const int MaxWorkCenterNameLength = 120;

    private const int MaxArchiveEntries = 2_000;
    private const long MaxExpandedWorkbookBytes = 25 * 1024 * 1024;
    private const int MaxScannedCells = 20_000;
    private const string ExpectedHeader = "Work Center Name";

    public async Task<WorkCenterWorkbookImportResult> ImportAsync(
        ProjectTrackerDbContext db,
        Stream input,
        CancellationToken cancellationToken = default)
    {
        var workbookBytes = await ReadBoundedAsync(input, cancellationToken);
        ValidatePackage(workbookBytes);
        var parsed = ParseNames(workbookBytes);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existingNames = (await db.WorkCenters
                .AsNoTracking()
                .Select(workCenter => workCenter.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedNames = parsed.UniqueNames
            .Where(name => !existingNames.Contains(name))
            .ToList();
        var skippedNames = parsed.SkippedNames
            .Concat(parsed.UniqueNames.Where(existingNames.Contains))
            .ToList();

        if (addedNames.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            db.WorkCenters.AddRange(addedNames.Select(name => new WorkCenter
            {
                Name = name,
                CreatedAt = now,
                UpdatedAt = now
            }));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new WorkCenterWorkbookImportException(
                    "Work centers changed while the workbook was being imported. Refresh and try the upload again.",
                    exception);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new WorkCenterWorkbookImportResult(
            addedNames.Count,
            skippedNames.Count,
            addedNames,
            skippedNames);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        if (!input.CanRead)
        {
            throw new WorkCenterWorkbookImportException("The selected workbook could not be read.");
        }

        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxWorkbookBytes)
            {
                throw new WorkCenterWorkbookImportException("The workbook is larger than the 5 MB import limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (output.Length == 0)
        {
            throw new WorkCenterWorkbookImportException("The selected workbook is empty.");
        }

        return output.ToArray();
    }

    private static void ValidatePackage(byte[] workbookBytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(workbookBytes), ZipArchiveMode.Read);
            if (archive.Entries.Count == 0
                || archive.Entries.Count > MaxArchiveEntries
                || archive.GetEntry("[Content_Types].xml") is null
                || archive.GetEntry("xl/workbook.xml") is null)
            {
                throw new WorkCenterWorkbookImportException("Upload a valid .xlsx workbook.");
            }

            long expandedSize = 0;
            foreach (var entry in archive.Entries)
            {
                expandedSize = checked(expandedSize + entry.Length);
                if (expandedSize > MaxExpandedWorkbookBytes)
                {
                    throw new WorkCenterWorkbookImportException("The workbook expands beyond the safe import limit.");
                }
            }
        }
        catch (WorkCenterWorkbookImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw new WorkCenterWorkbookImportException("Upload a valid .xlsx workbook.", exception);
        }
    }

    private static ParsedWorkCenters ParseNames(byte[] workbookBytes)
    {
        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(workbookBytes));
            var header = FindHeader(workbook);
            if (header is null)
            {
                throw new WorkCenterWorkbookImportException(
                    $"The workbook must include a column headed \"{ExpectedHeader}\".");
            }

            var cells = header.Worksheet
                .Column(header.Address.ColumnNumber)
                .CellsUsed(XLCellsUsedOptions.Contents)
                .Where(cell => cell.Address.RowNumber > header.Address.RowNumber)
                .OrderBy(cell => cell.Address.RowNumber)
                .Take(MaxWorkCenterCount + 1)
                .ToList();
            if (cells.Count > MaxWorkCenterCount)
            {
                throw new WorkCenterWorkbookImportException(
                    $"A workbook can contain at most {MaxWorkCenterCount:N0} work centers.");
            }

            var uniqueNames = new List<string>();
            var skippedNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in cells)
            {
                if (cell.HasFormula)
                {
                    throw new WorkCenterWorkbookImportException(
                        $"Work center on row {cell.Address.RowNumber} must be text, not a formula.");
                }
                var name = cell.GetFormattedString().Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                ValidateName(name, cell.Address.RowNumber);
                if (seen.Add(name))
                {
                    uniqueNames.Add(name);
                }
                else
                {
                    skippedNames.Add(name);
                }
            }

            if (uniqueNames.Count == 0)
            {
                throw new WorkCenterWorkbookImportException(
                    $"The \"{ExpectedHeader}\" column does not contain any work centers.");
            }

            return new ParsedWorkCenters(uniqueNames, skippedNames);
        }
        catch (WorkCenterWorkbookImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new WorkCenterWorkbookImportException("Project Tracker could not read this .xlsx workbook.", exception);
        }
    }

    private static IXLCell? FindHeader(XLWorkbook workbook)
    {
        var preferred = workbook.Worksheets
            .Where(sheet => string.Equals(sheet.Name.Trim(), "Work Centers", StringComparison.OrdinalIgnoreCase));
        var ordered = preferred.Concat(workbook.Worksheets.Where(sheet =>
            !string.Equals(sheet.Name.Trim(), "Work Centers", StringComparison.OrdinalIgnoreCase)));
        var scanned = 0;

        foreach (var sheet in ordered)
        {
            foreach (var cell in sheet.CellsUsed(XLCellsUsedOptions.Contents))
            {
                scanned += 1;
                if (scanned > MaxScannedCells)
                {
                    throw new WorkCenterWorkbookImportException(
                        "The workbook contains too much unrelated data to safely locate the work-center column.");
                }
                // Never evaluate formulas supplied by an uploaded workbook while
                // locating the administrator-controlled import column.
                if (cell.HasFormula) continue;
                if (string.Equals(cell.GetFormattedString().Trim(), ExpectedHeader, StringComparison.OrdinalIgnoreCase))
                {
                    return cell;
                }
            }
        }

        return null;
    }

    private static void ValidateName(string name, int rowNumber)
    {
        if (name.Length > MaxWorkCenterNameLength)
        {
            throw new WorkCenterWorkbookImportException(
                $"Work center on row {rowNumber} exceeds {MaxWorkCenterNameLength} characters.");
        }
        if (name.Any(char.IsControl))
        {
            throw new WorkCenterWorkbookImportException(
                $"Work center on row {rowNumber} contains an unsupported control character.");
        }
    }

    private sealed record ParsedWorkCenters(
        IReadOnlyList<string> UniqueNames,
        IReadOnlyList<string> SkippedNames);
}

public sealed record WorkCenterWorkbookImportResult(
    int AddedCount,
    int SkippedCount,
    IReadOnlyList<string> AddedNames,
    IReadOnlyList<string> SkippedNames);

public sealed class WorkCenterWorkbookImportException : Exception
{
    public WorkCenterWorkbookImportException(string message)
        : base(message)
    {
    }

    public WorkCenterWorkbookImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
