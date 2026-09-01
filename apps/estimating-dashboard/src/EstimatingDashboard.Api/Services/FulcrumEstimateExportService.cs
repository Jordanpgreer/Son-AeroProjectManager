using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed partial class FulcrumEstimateExportService(FulcrumEstimateReviewStore reviews)
{
    public const string TemplateFileName = "Fulcrum-Rubber-Estimating-Template.xlsx";
    public const string TemplateResourceName =
        "EstimatingDashboard.Api.Assets.Templates.Fulcrum-Rubber-Estimating-Template.xlsx";
    public const string SnapshotSheet = "Rate Reference Snapshot";

    public FulcrumEstimateExportResult Export(
        Guid reviewId,
        FulcrumEstimateExportDto request,
        string actor)
    {
        var review = reviews.Get(reviewId, actor);
        if (review.Issues.Any(issue => issue.Severity == "error"))
            throw new FulcrumEstimateManualValidationException(
                "Resolve the upload errors and preview the Fulcrum workbook again before exporting.");
        var manualValues = ValidateManualValues(review, request.ManualValues);
        var overrides = ValidateOverrides(review, request.OperationOverrides);
        var rates = ValidateRateSnapshot(review, request.RateSnapshot);

        using var workbook = OpenTemplate();
        var target = workbook.Worksheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, FulcrumEstimateImportService.TargetSheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new FulcrumEstimateValidationException(
                $"The packaged estimate template is missing the {FulcrumEstimateImportService.TargetSheet} worksheet.");
        foreach (var sheet in workbook.Worksheets.Where(sheet => sheet != target).ToList())
            sheet.Delete();
        ValidateTemplateOperationList(target, review.Operations);

        ClearRange(target, 18, 39, [1, 2, 3, 5, 15]);
        ClearRange(target, 47, 58, [1, 2, 3, 4, 15]);
        target.Cell("B3").Value = review.PartNumber;
        target.Cell("B4").Value = review.Revision;
        target.Cell("B7").Value = review.EstimateDate.ToDateTime(TimeOnly.MinValue);
        target.Cell("B8").Value = review.EstimatorInitials;
        target.Cell("B9").Value = review.RateYear;

        foreach (var field in review.ManualFields)
            SetManualValue(target.Cell(field.Cell), field, manualValues[field.Id]);

        for (var index = 0; index < review.Operations.Count; index++)
        {
            var operation = review.Operations[index];
            var row = 18 + index;
            var timing = overrides.GetValueOrDefault(operation.Id);
            target.Cell(row, 1).Value = operation.TargetOperation;
            target.Cell(row, 2).Value = timing?.SetupMinutes ?? operation.SuggestedSetupMinutes;
            target.Cell(row, 3).Value = timing?.RunMinutes ?? operation.SuggestedRunMinutes;
            target.Cell(row, 5).Value = rates.ByKey[operation.RateReferenceKey].Value;
            target.Cell(row, 15).Value = $"OP {operation.OperationNumber}";
        }

        foreach (var material in review.Materials)
        {
            target.Cell(material.TargetRow, 1).Value = material.Description;
            target.Cell(material.TargetRow, 3).Value = material.UnitsRequired;
        }

        for (var row = 14; row <= 17; row++)
        {
            var operationName = target.Cell(row, 1).GetString().Trim();
            if (operationName.Length == 0) continue;
            if (!rates.ByOperation.TryGetValue(operationName, out var rate))
                throw new FulcrumEstimateManualValidationException(
                    $"Rate snapshot is missing the template operation '{operationName}'.");
            target.Cell(row, 5).Value = rate.Value;
        }
        target.Cell("D42").Value = rates.Snapshot.Assumptions.Burden;
        target.Cell("E69").Value = rates.Snapshot.Assumptions.LaborGa;
        target.Cell("E70").Value = rates.Snapshot.Assumptions.MaterialGa;
        target.Cell("E71").Value = rates.Snapshot.Assumptions.ProcessGa;
        target.Cell("E73").Value = rates.Snapshot.Assumptions.LaborProfit;
        target.Cell("E74").Value = rates.Snapshot.Assumptions.MaterialProfit;
        target.Cell("E75").Value = rates.Snapshot.Assumptions.ProcessProfit;
        NormalizeLaborSummaryFormulas(target);
        target.PageSetup.PrintAreas.Clear();
        target.PageSetup.PrintAreas.Add("A2:O89");
        EnsureNoExternalRateFormulas(target);
        AddRateSnapshot(workbook, rates.Snapshot);
        workbook.CalculateMode = XLCalculateMode.Auto;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        SanitizeWorkbookPackage(output);
        var date = review.EstimateDate.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        var filename = $"{SafeFilenamePart(review.PartNumber)} {SafeFilenamePart(review.Revision)} {date} {SafeFilenamePart(review.EstimatorInitials)}.xlsx";
        return new(output.ToArray(), filename);
    }

    private static XLWorkbook OpenTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", TemplateFileName);
        if (File.Exists(path)) return new XLWorkbook(path);
        using var stream = typeof(FulcrumEstimateExportService).Assembly
            .GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException($"The packaged estimate template '{TemplateFileName}' is missing.");
        return new XLWorkbook(stream);
    }

    private static IReadOnlyDictionary<string, ManualValue> ValidateManualValues(
        FulcrumEstimateReview review,
        IReadOnlyDictionary<string, JsonElement>? submitted)
    {
        if (submitted is null)
            throw new FulcrumEstimateManualValidationException("Complete every required manual field before exporting.");
        var allowed = review.ManualFields.ToDictionary(field => field.Id, StringComparer.Ordinal);
        var unknown = submitted.Keys.FirstOrDefault(key => !allowed.ContainsKey(key));
        if (unknown is not null)
            throw new FulcrumEstimateManualValidationException($"Manual field '{unknown}' is not part of this estimate review.");
        var result = new Dictionary<string, ManualValue>(StringComparer.Ordinal);
        foreach (var field in review.ManualFields)
        {
            if (!submitted.TryGetValue(field.Id, out var value))
                throw new FulcrumEstimateManualValidationException($"{field.Label} is required.");
            if (field.Kind == "number")
            {
                var number = Decimal(value, field.Label);
                if (number < (field.Min ?? decimal.MinValue) || number > 1_000_000_000m)
                    throw new FulcrumEstimateManualValidationException(
                        $"{field.Label} must be at least {field.Min?.ToString(CultureInfo.InvariantCulture) ?? "the allowed minimum"} and no greater than 1,000,000,000.");
                result[field.Id] = new ManualValue(null, number);
            }
            else
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new FulcrumEstimateManualValidationException($"{field.Label} must be text.");
                var text = value.GetString()?.Trim() ?? string.Empty;
                if (field.Required && text.Length == 0)
                    throw new FulcrumEstimateManualValidationException($"{field.Label} is required.");
                if (text.Length > 1000)
                    throw new FulcrumEstimateManualValidationException($"{field.Label} cannot exceed 1,000 characters.");
                result[field.Id] = new ManualValue(text, null);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, FulcrumOperationOverrideDto> ValidateOverrides(
        FulcrumEstimateReview review,
        IReadOnlyList<FulcrumOperationOverrideDto>? submitted)
    {
        var allowed = review.Operations.Select(operation => operation.Id).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, FulcrumOperationOverrideDto>(StringComparer.Ordinal);
        foreach (var item in submitted ?? [])
        {
            if (!allowed.Contains(item.OperationId))
                throw new FulcrumEstimateManualValidationException(
                    $"Operation override '{item.OperationId}' is not part of this estimate review.");
            if (!result.TryAdd(item.OperationId, item))
                throw new FulcrumEstimateManualValidationException(
                    $"Operation override '{item.OperationId}' was submitted more than once.");
            ValidateMinutes(item.SetupMinutes, "setup");
            ValidateMinutes(item.RunMinutes, "run");
        }
        return result;
    }

    private static RateSnapshotValidation ValidateRateSnapshot(
        FulcrumEstimateReview review,
        FulcrumRateSnapshotDto? snapshot)
    {
        if (snapshot is null)
            throw new FulcrumEstimateManualValidationException("The controlled rate snapshot is required.");
        if (snapshot.OperationRates is null || snapshot.Assumptions is null)
            throw new FulcrumEstimateManualValidationException("The controlled rate snapshot is incomplete.");
        if (snapshot.OperationRates.Count > EstimatingRateReferenceCatalog.References.Count)
            throw new FulcrumEstimateManualValidationException("The controlled rate snapshot contains too many operation rates.");
        if (snapshot.Year != review.RateYear)
            throw new FulcrumEstimateManualValidationException(
                $"Rate snapshot year {snapshot.Year} does not match estimate year {review.RateYear}.");
        var catalog = EstimatingRateReferenceCatalog.References.ToDictionary(
            reference => reference.Key,
            StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, FulcrumOperationRateDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var rate in snapshot.OperationRates)
        {
            if (!catalog.TryGetValue(rate.RateReferenceKey, out var reference)
                || !string.Equals(reference.Operation, rate.Operation.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FulcrumEstimateManualValidationException(
                    $"Rate snapshot entry '{rate.RateReferenceKey}' does not match the controlled Rates Reference.");
            if (rate.Value < 0 || rate.Value > 1_000_000m)
                throw new FulcrumEstimateManualValidationException(
                    $"Rate for '{rate.Operation}' must be non-negative and no greater than 1,000,000.");
            if (!EstimatingControlledRates.TryGetRate(reference.Key, snapshot.Year, out var controlledRate)
                || rate.Value != controlledRate)
                throw new FulcrumEstimateManualValidationException(
                    $"Rate for '{reference.Operation}' does not match the controlled {snapshot.Year} Rates Reference.");
            if (!byKey.TryAdd(reference.Key, rate with { Operation = reference.Operation }))
                throw new FulcrumEstimateManualValidationException(
                    $"Rate snapshot contains duplicate key '{reference.Key}'.");
        }
        foreach (var operation in review.Operations)
        {
            if (!byKey.ContainsKey(operation.RateReferenceKey))
                throw new FulcrumEstimateManualValidationException(
                    $"Rate snapshot is missing '{operation.TargetOperation}'.");
        }
        ValidateAssumption(snapshot.Assumptions.Burden, "burden");
        ValidateAssumption(snapshot.Assumptions.LaborGa, "labor G&A");
        ValidateAssumption(snapshot.Assumptions.MaterialGa, "material G&A");
        ValidateAssumption(snapshot.Assumptions.ProcessGa, "process G&A");
        ValidateAssumption(snapshot.Assumptions.LaborProfit, "labor profit");
        ValidateAssumption(snapshot.Assumptions.MaterialProfit, "material profit");
        ValidateAssumption(snapshot.Assumptions.ProcessProfit, "process profit");
        if (!EstimatingControlledRates.TryGetAssumptions(snapshot.Year, out var controlledAssumptions)
            || snapshot.Assumptions.Burden != controlledAssumptions.Burden
            || snapshot.Assumptions.LaborGa != controlledAssumptions.LaborGa
            || snapshot.Assumptions.MaterialGa != controlledAssumptions.MaterialGa
            || snapshot.Assumptions.ProcessGa != controlledAssumptions.ProcessGa
            || snapshot.Assumptions.LaborProfit != controlledAssumptions.LaborProfit
            || snapshot.Assumptions.MaterialProfit != controlledAssumptions.MaterialProfit
            || snapshot.Assumptions.ProcessProfit != controlledAssumptions.ProcessProfit)
            throw new FulcrumEstimateManualValidationException(
                $"Rate assumptions do not match the controlled {snapshot.Year} Rates Reference.");
        var byOperation = byKey.Values
            .GroupBy(rate => rate.Operation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return new(snapshot, byKey, byOperation);
    }

    private static void AddRateSnapshot(XLWorkbook workbook, FulcrumRateSnapshotDto snapshot)
    {
        var sheet = workbook.AddWorksheet(SnapshotSheet);
        sheet.Cell("A1").Value = "Estimate rate reference snapshot";
        sheet.Cell("A2").Value = "Year";
        sheet.Cell("B2").Value = snapshot.Year;
        sheet.Cell("A4").Value = "Rate reference key";
        sheet.Cell("B4").Value = "Operation";
        sheet.Cell("C4").Value = "Rate per minute";
        var row = 5;
        foreach (var rate in snapshot.OperationRates.OrderBy(rate => rate.RateReferenceKey))
        {
            sheet.Cell(row, 1).Value = rate.RateReferenceKey;
            sheet.Cell(row, 2).Value = rate.Operation;
            sheet.Cell(row, 3).Value = rate.Value;
            row++;
        }
        row++;
        foreach (var (label, value) in new[]
        {
            ("Burden", snapshot.Assumptions.Burden),
            ("Labor G&A", snapshot.Assumptions.LaborGa),
            ("Material G&A", snapshot.Assumptions.MaterialGa),
            ("Process G&A", snapshot.Assumptions.ProcessGa),
            ("Labor profit", snapshot.Assumptions.LaborProfit),
            ("Material profit", snapshot.Assumptions.MaterialProfit),
            ("Process profit", snapshot.Assumptions.ProcessProfit)
        })
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = value;
            row++;
        }
        sheet.Visibility = XLWorksheetVisibility.VeryHidden;
    }

    private static void EnsureNoExternalRateFormulas(IXLWorksheet sheet)
    {
        var external = sheet.CellsUsed(cell => cell.HasFormula)
            .FirstOrDefault(cell => cell.FormulaA1.Contains("[1]!Rates2020", StringComparison.OrdinalIgnoreCase));
        if (external is not null)
            throw new FulcrumEstimateValidationException(
                $"The estimate template still contains an external rate formula at {external.Address}.");
    }

    private static void SanitizeWorkbookPackage(MemoryStream package)
    {
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var entry in archive.Entries
                         .Where(entry =>
                             entry.FullName.StartsWith(
                                 "xl/externalLinks/",
                                 StringComparison.OrdinalIgnoreCase)
                             || entry.FullName.StartsWith(
                                 "customXml/",
                                 StringComparison.OrdinalIgnoreCase))
                         .ToList())
                entry.Delete();

            RewritePackageXml(archive, "xl/workbook.xml", document =>
            {
                XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                document.Descendants(spreadsheet + "externalReferences").Remove();
            });
            RewritePackageXml(archive, "xl/_rels/workbook.xml.rels", document =>
            {
                XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
                document.Descendants(relationships + "Relationship")
                    .Where(element =>
                    {
                        var type = (string?)element.Attribute("Type");
                        return type?.EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase) == true
                               || type?.EndsWith("/customXml", StringComparison.OrdinalIgnoreCase) == true;
                    })
                    .Remove();
            });
            RewritePackageXml(archive, "[Content_Types].xml", document =>
            {
                XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
                document.Descendants(contentTypes + "Override")
                    .Where(element =>
                    {
                        var partName = (string?)element.Attribute("PartName");
                        return partName?.StartsWith(
                                   "/xl/externalLinks/",
                                   StringComparison.OrdinalIgnoreCase) == true
                               || partName?.StartsWith(
                                   "/customXml/",
                                   StringComparison.OrdinalIgnoreCase) == true;
                    })
                    .Remove();
            });
        }
        package.Position = 0;
    }

    private static void RewritePackageXml(
        ZipArchive archive,
        string entryName,
        Action<XDocument> rewrite)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new FulcrumEstimateValidationException(
                $"The generated estimate is missing package part '{entryName}'.");
        XDocument document;
        using (var input = entry.Open())
            document = XDocument.Load(input, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        rewrite(document);
        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = replacement.Open();
        document.Save(output, System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    private static void NormalizeLaborSummaryFormulas(IXLWorksheet sheet)
    {
        // The inherited template used a union expression such as SUM((F44,F69,F73)).
        // Excel accepts it, but simpler addition is more portable across workbook readers.
        for (var column = 6; column <= 14; column++)
        {
            var letter = sheet.Cell(79, column).Address.ColumnLetter;
            sheet.Cell(79, column).FormulaA1 = $"={letter}44+{letter}69+{letter}73";
        }
    }

    private static void ValidateTemplateOperationList(
        IXLWorksheet sheet,
        IReadOnlyList<FulcrumOperationPreviewDto> operations)
    {
        var allowed = sheet.Range("A111:A148").Cells()
            .Select(cell => cell.GetString().Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (!allowed.Contains(operation.TargetOperation))
                throw new FulcrumEstimateValidationException(
                    $"The packaged template validation list does not contain '{operation.TargetOperation}'.");
        }
    }

    private static void SetManualValue(IXLCell cell, FulcrumManualFieldDto field, ManualValue value)
    {
        if (field.Kind == "number") cell.Value = value.Number!.Value;
        else cell.Value = value.Text ?? string.Empty;
    }

    private static decimal Decimal(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        throw new FulcrumEstimateManualValidationException($"{label} must be a valid number.");
    }

    private static void ValidateMinutes(decimal? value, string label)
    {
        if (value is < 0 or > FulcrumEstimateImportService.MaximumMinutes)
            throw new FulcrumEstimateManualValidationException(
                $"Operation {label} minutes must be between 0 and {FulcrumEstimateImportService.MaximumMinutes:N0}.");
    }

    private static void ValidateAssumption(decimal value, string label)
    {
        if (value < 0 || value > 10)
            throw new FulcrumEstimateManualValidationException(
                $"Rate snapshot {label} must be between 0 and 10.");
    }

    private static void ClearRange(IXLWorksheet sheet, int firstRow, int lastRow, int[] columns)
    {
        foreach (var row in Enumerable.Range(firstRow, lastRow - firstRow + 1))
        foreach (var column in columns)
            sheet.Cell(row, column).Clear(XLClearOptions.Contents);
    }

    private static string SafeFilenamePart(string value)
    {
        var cleaned = InvalidFilenameCharacters().Replace(value.Trim(), "-");
        cleaned = ControlCharacters().Replace(cleaned, "-");
        cleaned = Whitespace().Replace(cleaned, " ").Trim(' ', '.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd();
        if (cleaned.Length == 0)
            throw new FulcrumEstimateValidationException("Part number, revision, and estimator initials are required for the export filename.");
        return cleaned;
    }

    private sealed record ManualValue(string? Text, decimal? Number);
    private sealed record RateSnapshotValidation(
        FulcrumRateSnapshotDto Snapshot,
        IReadOnlyDictionary<string, FulcrumOperationRateDto> ByKey,
        IReadOnlyDictionary<string, FulcrumOperationRateDto> ByOperation);

    [GeneratedRegex(@"[<>:\""/\\|?*]+")]
    private static partial Regex InvalidFilenameCharacters();
    [GeneratedRegex(@"[\x00-\x1F]")]
    private static partial Regex ControlCharacters();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record FulcrumEstimateExportResult(byte[] Content, string FileName);
