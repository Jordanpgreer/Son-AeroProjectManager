using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class FulcrumEstimateImportService(
    EstimatingAccessDbContext db,
    FulcrumEstimateReviewStore reviews,
    TimeProvider timeProvider)
{
    public const string RoutingSheet = "Routing";
    public const string BillOfMaterialsSheet = "Bill of Materials";
    public const string TargetSheet = "Rubber Breakdown";
    public const int MaximumWorkbookRows = 1000;
    public const int MaximumOperations = 22;
    public const int MaximumMaterials = 12;
    public const decimal MaximumMinutes = 1_000_000m;
    public const long MaximumUploadBytes = 25L * 1024 * 1024;
    public const long MaximumUncompressedWorkbookBytes = 100L * 1024 * 1024;
    public const int MaximumWorkbookEntries = 2_048;

    public async Task<FulcrumEstimatePreviewDto> PreviewAsync(
        Stream stream,
        string fileName,
        string actor,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var copy = await ReadBoundedAsync(stream, cancellationToken);
        if (copy.Length == 0)
            throw new FulcrumEstimateValidationException("Choose a non-empty Fulcrum workbook.");

        using var workbook = OpenWorkbook(copy.ToArray());
        var routing = workbook.Worksheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, RoutingSheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new FulcrumEstimateValidationException("The workbook is missing the Routing worksheet.");
        var billOfMaterials = workbook.Worksheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, BillOfMaterialsSheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new FulcrumEstimateValidationException("The workbook is missing the Bill of Materials worksheet.");
        if ((routing.LastRowUsed()?.RowNumber() ?? 0) > MaximumWorkbookRows
            || (billOfMaterials.LastRowUsed()?.RowNumber() ?? 0) > MaximumWorkbookRows)
            throw new FulcrumEstimateValidationException(
                $"Routing and Bill of Materials worksheets cannot exceed {MaximumWorkbookRows:N0} rows.");

        var issues = new List<FulcrumEstimateIssueDto>();
        var partNumber = RequiredIdentifier(routing.Cell("D3"), RoutingSheet, "D", "part number", issues);
        var revision = RequiredIdentifier(routing.Cell("E3"), RoutingSheet, "E", "revision", issues);
        ValidateBomIdentity(billOfMaterials, partNumber, revision, issues);

        var mappings = await db.EstimatingOperationMappings.AsNoTracking()
            .Include(mapping => mapping.RateReference)
            .Where(mapping => mapping.IsActive && mapping.RateReference.IsActive)
            .ToDictionaryAsync(
                mapping => mapping.FulcrumOperationKey,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);
        var operations = ParseOperations(routing, mappings, issues);
        var materials = ParseMaterials(billOfMaterials, issues);
        var manualFields = ManualFields(materials);
        var now = timeProvider.GetUtcNow();
        var centralNow = TimeZoneInfo.ConvertTime(now, CentralTimeZone());
        var estimateDate = DateOnly.FromDateTime(centralNow.DateTime);
        var initials = Initials(displayName);
        if (initials.Length == 0)
            issues.Add(Error("Identity", null, null, "Your estimator initials could not be derived from the signed-in display name."));

        var expiresAt = now.AddMinutes(45);
        var review = reviews.Add(new FulcrumEstimateReview(
            Guid.NewGuid(),
            actor,
            expiresAt,
            Path.GetFileName(fileName),
            partNumber,
            revision,
            estimateDate,
            initials,
            estimateDate.Year,
            operations,
            materials,
            manualFields,
            issues));
        return ToDto(review);
    }

    private static XLWorkbook OpenWorkbook(byte[] bytes)
    {
        ValidateWorkbookPackage(bytes);
        try
        {
            using var stream = new MemoryStream(bytes);
            return new XLWorkbook(stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FulcrumEstimateValidationException(
                "The workbook could not be read as an .xlsx Fulcrum export.");
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var copy = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return copy;
            if (copy.Length + read > MaximumUploadBytes)
            {
                await copy.DisposeAsync();
                throw new FulcrumEstimateValidationException("The workbook cannot exceed 25 MB.");
            }
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateWorkbookPackage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumWorkbookEntries)
                throw new FulcrumEstimateValidationException(
                    $"The workbook package cannot contain more than {MaximumWorkbookEntries:N0} files.");

            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (IsUnsafePackagePath(entry.FullName))
                    throw new FulcrumEstimateValidationException(
                        "The workbook package contains an unsafe file path.");
                uncompressedBytes = checked(uncompressedBytes + entry.Length);
                if (uncompressedBytes > MaximumUncompressedWorkbookBytes)
                    throw new FulcrumEstimateValidationException(
                        "The expanded workbook cannot exceed 100 MB.");
            }

            var names = archive.Entries
                .Select(entry => entry.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains("[Content_Types].xml") || !names.Contains("xl/workbook.xml"))
                throw new FulcrumEstimateValidationException(
                    "The upload is not a valid Excel workbook package.");
        }
        catch (FulcrumEstimateValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            throw new FulcrumEstimateValidationException(
                "The upload is not a valid Excel workbook package.");
        }
    }

    private static bool IsUnsafePackagePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith('/')
            || value.StartsWith('\\'))
            return true;
        return value.Split('/', '\\').Any(segment => segment == "..");
    }

    private static string RequiredIdentifier(
        IXLCell cell,
        string sheet,
        string column,
        string label,
        List<FulcrumEstimateIssueDto> issues)
    {
        RejectFormula(cell, sheet, cell.Address.RowNumber, column, issues);
        var value = Clean(cell.GetFormattedString());
        if (value.Length == 0)
            issues.Add(Error(sheet, cell.Address.RowNumber, column, $"Routing {label} is required in {cell.Address}."));
        else if (value.Length > 100)
            issues.Add(Error(sheet, cell.Address.RowNumber, column, $"Routing {label} cannot exceed 100 characters."));
        return value;
    }

    private static void ValidateBomIdentity(
        IXLWorksheet sheet,
        string partNumber,
        string revision,
        List<FulcrumEstimateIssueDto> issues)
    {
        foreach (var (address, expected, label) in new[]
        {
            ("D3", partNumber, "part number"),
            ("E3", revision, "revision")
        })
        {
            var cell = sheet.Cell(address);
            RejectFormula(cell, BillOfMaterialsSheet, 3, address[..1], issues);
            var value = Clean(cell.GetFormattedString());
            if (value.Length > 0 && expected.Length > 0
                && !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                issues.Add(Error(
                    BillOfMaterialsSheet,
                    3,
                    address[..1],
                    $"Bill of Materials {label} '{value}' does not match Routing '{expected}'."));
        }
    }

    private static IReadOnlyList<FulcrumOperationPreviewDto> ParseOperations(
        IXLWorksheet sheet,
        IReadOnlyDictionary<string, Models.EstimatingOperationMappingRecord> mappings,
        List<FulcrumEstimateIssueDto> issues)
    {
        var operations = new List<FulcrumOperationPreviewDto>();
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 2, MaximumWorkbookRows);
        for (var row = 3; row <= lastRow; row++)
        {
            var operationCell = sheet.Cell(row, 7);
            var orderCell = sheet.Cell(row, 8);
            var setupCell = sheet.Cell(row, 12);
            var typeCell = sheet.Cell(row, 14);
            var laborCell = sheet.Cell(row, 15);
            if (operationCell.IsEmpty() && orderCell.IsEmpty() && setupCell.IsEmpty()
                && typeCell.IsEmpty() && laborCell.IsEmpty()) continue;
            foreach (var (cell, column) in new[]
            {
                (operationCell, "G"), (orderCell, "H"), (setupCell, "L"),
                (typeCell, "N"), (laborCell, "O")
            }) RejectFormula(cell, RoutingSheet, row, column, issues);

            var sourceOperation = Clean(operationCell.GetFormattedString());
            if (sourceOperation.Length == 0)
            {
                issues.Add(Error(RoutingSheet, row, "G", "Operation is required when a routing row contains data."));
                continue;
            }
            if (sourceOperation.Length > 160)
            {
                issues.Add(Error(RoutingSheet, row, "G", "Operation cannot exceed 160 characters."));
                continue;
            }
            var operationNumber = PositiveWholeNumber(orderCell, RoutingSheet, row, "H", "operation number", issues);
            var setup = OptionalNonNegativeDecimal(setupCell, RoutingSheet, row, "L", "setup time", issues);
            var labor = OptionalNonNegativeDecimal(laborCell, RoutingSheet, row, "O", "labor time", issues);
            var timeType = Clean(typeCell.GetFormattedString());
            var (suggestedSetup, suggestedRun) = SuggestedTimes(setup, labor, timeType, row, issues);

            if (!mappings.TryGetValue(EstimatingOperationNames.Normalize(sourceOperation), out var mapping))
            {
                issues.Add(Error(
                    RoutingSheet,
                    row,
                    "G",
                    $"No active estimating operation rule matches '{sourceOperation}'. Add a rule before exporting."));
                continue;
            }
            operations.Add(new FulcrumOperationPreviewDto(
                $"routing-{row}",
                row,
                sourceOperation,
                operationNumber ?? 0,
                mapping.RateReferenceKey,
                mapping.RateReference.OperationName,
                suggestedSetup,
                suggestedRun,
                timeType.Length == 0 ? null : timeType));
        }
        if (operations.Count == 0)
            issues.Add(Error(RoutingSheet, null, "G", "No routing operations were found in column G from row 3 onward."));
        if (operations.Count > MaximumOperations)
            issues.Add(Error(
                RoutingSheet,
                null,
                "G",
                $"The workbook contains {operations.Count} mapped operations; the estimate supports {MaximumOperations}."));
        return operations.Take(MaximumOperations).ToList();
    }

    private static (decimal Setup, decimal Run) SuggestedTimes(
        decimal? setup,
        decimal? labor,
        string timeType,
        int row,
        List<FulcrumEstimateIssueDto> issues)
    {
        var suggestedSetup = setup ?? 0m;
        var suggestedRun = 0m;
        var normalized = NonLetters().Replace(timeType, string.Empty).ToUpperInvariant();
        if (labor is null) return (suggestedSetup, suggestedRun);
        switch (normalized)
        {
            case "PERUNIT":
                suggestedRun = labor.Value;
                break;
            case "UNITSPERHOUR":
                if (labor.Value <= 0)
                    issues.Add(Error(RoutingSheet, row, "O", "UnitsPerHour must be greater than zero."));
                else if (labor.Value < 60m / MaximumMinutes)
                    issues.Add(Error(
                        RoutingSheet,
                        row,
                        "O",
                        $"UnitsPerHour converts to more than {MaximumMinutes:N0} run minutes."));
                else
                    suggestedRun = 60m / labor.Value;
                break;
            case "FIXED":
                if (setup is null) suggestedSetup = labor.Value;
                break;
            default:
                issues.Add(Error(
                    RoutingSheet,
                    row,
                    "N",
                    "Labor time type must be PerUnit, UnitsPerHour, or Fixed when labor time is provided."));
                break;
        }
        return (suggestedSetup, suggestedRun);
    }

    private static IReadOnlyList<FulcrumMaterialPreviewDto> ParseMaterials(
        IXLWorksheet sheet,
        List<FulcrumEstimateIssueDto> issues)
    {
        var materials = new List<FulcrumMaterialPreviewDto>();
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 2, MaximumWorkbookRows);
        for (var row = 3; row <= lastRow; row++)
        {
            var materialCell = sheet.Cell(row, 8);
            var unitsCell = sheet.Cell(row, 12);
            if (materialCell.IsEmpty() && unitsCell.IsEmpty()) continue;
            RejectFormula(materialCell, BillOfMaterialsSheet, row, "H", issues);
            RejectFormula(unitsCell, BillOfMaterialsSheet, row, "L", issues);
            var description = Clean(materialCell.GetFormattedString());
            if (description.Length == 0)
            {
                issues.Add(Error(BillOfMaterialsSheet, row, "H", "Material is required when the BOM row contains data."));
                continue;
            }
            if (description.Length > 240)
            {
                issues.Add(Error(BillOfMaterialsSheet, row, "H", "Material cannot exceed 240 characters."));
                continue;
            }
            var units = RequiredNonNegativeDecimal(
                unitsCell,
                BillOfMaterialsSheet,
                row,
                "L",
                "units required",
                issues);
            materials.Add(new FulcrumMaterialPreviewDto(
                $"bom-{row}",
                row,
                47 + materials.Count,
                description,
                units ?? 0m));
        }
        if (materials.Count > MaximumMaterials)
            issues.Add(Error(
                BillOfMaterialsSheet,
                null,
                "H",
                $"The workbook contains {materials.Count} materials; the estimate supports {MaximumMaterials}."));
        return materials.Take(MaximumMaterials).ToList();
    }

    private static IReadOnlyList<FulcrumManualFieldDto> ManualFields(
        IReadOnlyList<FulcrumMaterialPreviewDto> materials)
    {
        var fields = new List<FulcrumManualFieldDto>
        {
            new("customer", "Customer", "Enter the estimate customer.", TargetSheet, "B2", "text", true),
            new("quoteLogNumber", "Quote log number", "Enter the controlled quote log number.", TargetSheet, "B5", "text", true)
        };
        for (var index = 0; index < 8; index++)
            fields.Add(new FulcrumManualFieldDto(
                $"quantity{index + 1}",
                $"Quantity {index + 1}",
                "Enter a positive estimate quantity.",
                $"{TargetSheet}",
                $"{(char)('F' + index)}13",
                "number",
                true,
                0.0000001m));
        foreach (var material in materials)
        {
            fields.Add(new FulcrumManualFieldDto(
                $"{material.Id}.unitOfMeasure",
                $"{material.Description}: unit of measure",
                "Enter the material unit of measure.",
                TargetSheet,
                $"B{material.TargetRow}",
                "text",
                true));
            fields.Add(new FulcrumManualFieldDto(
                $"{material.Id}.unitPrice",
                $"{material.Description}: unit price",
                "Enter the non-negative unit price.",
                TargetSheet,
                $"D{material.TargetRow}",
                "number",
                true,
                0));
            fields.Add(new FulcrumManualFieldDto(
                $"{material.Id}.notes",
                $"{material.Description}: notes",
                "Enter the required material note or source context.",
                TargetSheet,
                $"O{material.TargetRow}",
                "text",
                true));
        }
        return fields;
    }

    private static int? PositiveWholeNumber(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        string label,
        List<FulcrumEstimateIssueDto> issues)
    {
        if (cell.TryGetValue<int>(out var number) && number > 0 && number <= 9999) return number;
        var text = Clean(cell.GetFormattedString());
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            && number > 0 && number <= 9999) return number;
        issues.Add(Error(sheet, row, column, $"{label} must be a positive whole number no greater than 9,999."));
        return null;
    }

    private static decimal? OptionalNonNegativeDecimal(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        string label,
        List<FulcrumEstimateIssueDto> issues)
    {
        if (cell.IsEmpty()) return null;
        return RequiredNonNegativeDecimal(cell, sheet, row, column, label, issues);
    }

    private static decimal? RequiredNonNegativeDecimal(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        string label,
        List<FulcrumEstimateIssueDto> issues)
    {
        if (cell.TryGetValue<decimal>(out var number)
            && number >= 0 && number <= MaximumMinutes) return number;
        var text = Clean(cell.GetFormattedString());
        if ((decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number)
                || decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out number))
            && number >= 0 && number <= MaximumMinutes) return number;
        issues.Add(Error(
            sheet,
            row,
            column,
            $"{label} must be a non-negative number no greater than {MaximumMinutes:N0}."));
        return null;
    }

    private static void RejectFormula(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<FulcrumEstimateIssueDto> issues)
    {
        if (cell.HasFormula)
            issues.Add(Error(sheet, row, column, "Formulas are not accepted in Fulcrum source fields; upload exported values."));
    }

    private static FulcrumEstimateIssueDto Error(
        string sheet,
        int? row,
        string? column,
        string message) => new("error", sheet, row, column, message);

    private static string Clean(string? value) => Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    private static string Initials(string displayName)
    {
        var normalized = Parenthetical().Replace(displayName ?? string.Empty, " ");
        var words = Words().Matches(normalized)
            .Select(match => match.Value)
            .Where(word => word.Length > 0)
            .ToList();
        if (words.Count == 0) return string.Empty;
        var first = char.ToUpperInvariant(words[0][0]).ToString();
        return words.Count == 1
            ? first
            : first + char.ToUpperInvariant(words[^1][0]);
    }

    private static TimeZoneInfo CentralTimeZone()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    private static FulcrumEstimatePreviewDto ToDto(FulcrumEstimateReview review) => new(
        review.Id,
        review.ExpiresAt,
        review.SourceFileName,
        TargetSheet,
        review.PartNumber,
        review.Revision,
        review.EstimateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        review.EstimatorInitials,
        review.RateYear,
        review.Operations,
        review.Materials,
        review.ManualFields,
        review.Issues,
        review.Issues.All(issue => issue.Severity != "error"));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^A-Za-z]")]
    private static partial Regex NonLetters();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthetical();
}
