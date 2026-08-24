using System.Globalization;
using System.Security.Cryptography;
using ClosedXML.Excel;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistoryImportService(
    EstimatingAccessDbContext db,
    EstimatingHistoryReviewStore reviews)
{
    private static readonly string[] RequiredHeaders =
    [
        "Id",
        "Number",
        "Customer",
        "SalesPerson",
        "TotalInPrimaryCurrency",
        "Status",
        "RFQ Due Date",
        "Date to Estimating",
        "Issues?",
        "Quote Complexity",
        "Number of Parts in Quote",
        "Estimating Status",
        "Estimating Rep",
        "Estimating Completion Date"
    ];

    private static readonly string[] AdditionalHeaders =
    [
        "CustomerContact",
        "RFQ/REF No",
        "Quote On Track?"
    ];

    private static readonly string[] ImportHeaders = [.. RequiredHeaders, .. AdditionalHeaders];

    public async Task<EstimatingHistoryImportValidationDto> ValidateAsync(
        Stream stream,
        string fileName,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        var errors = new List<EstimatingHistoryImportIssueDto>();
        var rows = Parse(bytes, errors);

        var duplicates = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceId))
            .GroupBy(row => row.SourceId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();
        foreach (var duplicate in duplicates)
        {
            foreach (var row in duplicate)
                errors.Add(new EstimatingHistoryImportIssueDto(row.RowNumber, "Id", $"Source Id '{duplicate.Key}' appears more than once in this workbook."));
        }

        var invalidRows = errors.Select(error => error.Row).ToHashSet();
        var validRows = rows.Where(row => !invalidRows.Contains(row.RowNumber)).ToList();
        var sourceIds = validRows.Select(row => row.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await db.QuoteHistory
            .AsNoTracking()
            .Where(record => sourceIds.Contains(record.SourceId))
            .ToDictionaryAsync(record => record.SourceId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var changes = new List<EstimatingHistoryImportChangeDto>();
        var newRecords = 0;
        var updatedRecords = 0;
        var unchangedRecords = 0;
        foreach (var row in validRows)
        {
            if (!existing.TryGetValue(row.SourceId, out var current))
            {
                newRecords++;
                if (changes.Count < 250)
                    changes.Add(Change(row, "New"));
            }
            else if (Equivalent(current, row))
            {
                unchangedRecords++;
            }
            else
            {
                updatedRecords++;
                if (changes.Count < 250)
                    changes.Add(Change(row, "Updated"));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var review = reviews.Add(new EstimatingHistoryImportReview(
            Guid.NewGuid(),
            actor,
            now.AddMinutes(45),
            Path.GetFileName(fileName),
            Convert.ToHexString(SHA256.HashData(bytes)),
            rows.Count,
            newRecords,
            updatedRecords,
            unchangedRecords,
            errors.OrderBy(error => error.Row).ThenBy(error => error.Column).ToList(),
            changes,
            rows,
            invalidRows,
            existing.ToDictionary(pair => pair.Key, pair => pair.Value.Version, StringComparer.OrdinalIgnoreCase)));
        return ToDto(review);
    }

    public async Task<EstimatingHistoryImportApplyResultDto> ApplyAsync(
        Guid reviewId,
        string actor,
        bool continueWithErrors,
        CancellationToken cancellationToken)
    {
        var review = reviews.Get(reviewId, actor);
        if (review.Errors.Count > 0 && !continueWithErrors)
            throw new EstimatingHistoryImportValidationException("The workbook contains errors. Correct them or explicitly continue with valid rows only.");
        if (review.NewRecords + review.UpdatedRecords == 0)
            throw new EstimatingHistoryImportValidationException("The workbook does not contain any new or changed quote records.");

        var validRows = review.Rows.Where(row => !review.InvalidRows.Contains(row.RowNumber)).ToList();
        var sourceIds = validRows.Select(row => row.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await db.QuoteHistory
            .Where(record => sourceIds.Contains(record.SourceId))
            .ToDictionaryAsync(record => record.SourceId, StringComparer.OrdinalIgnoreCase, cancellationToken);
        foreach (var current in existing.Values)
        {
            if (!review.ExpectedVersions.TryGetValue(current.SourceId, out var expected)
                || current.Version != expected)
                throw new EstimatingHistoryImportConflictException($"Quote {current.QuoteNumber} changed after validation. Validate the workbook again before applying it.");
        }

        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.NewGuid();
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var row in validRows)
        {
            if (!existing.TryGetValue(row.SourceId, out var record))
            {
                record = new EstimatingQuoteHistoryRecord
                {
                    SourceId = row.SourceId,
                    FirstImportedAt = now,
                    Version = 0
                };
                Apply(record, row, batchId, actor, now);
                db.QuoteHistory.Add(record);
                existing[row.SourceId] = record;
                added++;
            }
            else if (Equivalent(record, row))
            {
                unchanged++;
            }
            else
            {
                Apply(record, row, batchId, actor, now);
                record.Version++;
                updated++;
            }
        }

        db.QuoteHistoryImportBatches.Add(new EstimatingHistoryImportBatch
        {
            Id = batchId,
            FileName = review.FileName,
            FileHash = review.FileHash,
            ImportedBy = actor,
            ImportedAt = now,
            TotalRows = review.TotalRows,
            NewRecords = added,
            UpdatedRecords = updated,
            UnchangedRecords = unchanged,
            SkippedRows = review.InvalidRows.Count(row => row > 1),
            ErrorRows = review.Errors.Select(error => error.Row).Distinct().Count()
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        reviews.Remove(reviewId);
        return new EstimatingHistoryImportApplyResultDto(
            batchId,
            added,
            updated,
            unchanged,
            review.InvalidRows.Count(row => row > 1));
    }

    private static IReadOnlyList<EstimatingHistoryImportRow> Parse(
        byte[] bytes,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.FirstOrDefault(worksheet =>
                    string.Equals(worksheet.Name, "Grid Results", StringComparison.OrdinalIgnoreCase)
                    && FindHeaderRow(worksheet) > 0)
                ?? workbook.Worksheets.FirstOrDefault(worksheet => FindHeaderRow(worksheet) > 0);
            if (sheet is null)
            {
                errors.Add(new EstimatingHistoryImportIssueDto(1, null, "The workbook does not contain a worksheet."));
                return [];
            }

            var headerRow = FindHeaderRow(sheet);
            if (headerRow == 0)
            {
                errors.Add(new EstimatingHistoryImportIssueDto(1, null, "Could not find the Fulcrum Grid Results header row."));
                return [];
            }

            var columns = Columns(sheet, headerRow);
            if (RequiredHeaders.Any(required => !columns.ContainsKey(NormalizeHeader(required))))
            {
                var supplementalSheet = workbook.Worksheets.FirstOrDefault(worksheet =>
                    worksheet != sheet
                    && FindHeaderRow(worksheet, "Index", "Estimating Rep") > 0);
                if (supplementalSheet is not null)
                {
                    var supplementalHeaderRow = FindHeaderRow(supplementalSheet, "Index", "Estimating Rep");
                    var supplementalColumns = Columns(supplementalSheet, supplementalHeaderRow);
                    var available = columns.Keys.Concat(supplementalColumns.Keys).ToHashSet();
                    foreach (var required in RequiredHeaders.Where(required => !available.Contains(NormalizeHeader(required))))
                        errors.Add(new EstimatingHistoryImportIssueDto(headerRow, required, $"Required column '{required}' is missing."));
                    if (errors.Count > 0) return [];

                    sheet = BuildMergedSheet(
                        workbook,
                        sheet,
                        headerRow,
                        columns,
                        supplementalSheet,
                        supplementalHeaderRow,
                        supplementalColumns,
                        errors);
                    headerRow = 1;
                    columns = Columns(sheet, headerRow);
                }
            }
            foreach (var required in RequiredHeaders.Where(required => !columns.ContainsKey(NormalizeHeader(required))))
                errors.Add(new EstimatingHistoryImportIssueDto(headerRow, required, $"Required column '{required}' is missing."));
            if (errors.Count > 0) return [];

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
            if (lastRow - headerRow > 10000)
            {
                errors.Add(new EstimatingHistoryImportIssueDto(headerRow, null, "The workbook exceeds the 10,000-row import limit."));
                return [];
            }

            var rows = new List<EstimatingHistoryImportRow>();
            for (var rowNumber = headerRow + 1; rowNumber <= lastRow; rowNumber++)
            {
                if (IsBlank(sheet, rowNumber, columns)) continue;
                var rowErrors = new List<EstimatingHistoryImportIssueDto>();
                foreach (var cell in columns.Values.Select(column => sheet.Cell(rowNumber, column)).Where(cell => cell.HasFormula))
                    rowErrors.Add(new EstimatingHistoryImportIssueDto(rowNumber, HeaderAt(columns, cell.Address.ColumnNumber), "Formulas are not accepted. Upload the Fulcrum export values directly."));

                var sourceId = RequiredText(sheet, rowNumber, columns, "Id", 80, rowErrors);
                var quoteNumber = RequiredInt(sheet, rowNumber, columns, "Number", rowErrors);
                var customer = RequiredText(sheet, rowNumber, columns, "Customer", 240, rowErrors);
                var customerContact = OptionalText(sheet, rowNumber, columns, "CustomerContact", 240, rowErrors);
                var salesPerson = Text(sheet, rowNumber, columns, "SalesPerson", 160, rowErrors) ?? "Unassigned";
                var quoteStatus = Text(sheet, rowNumber, columns, "Status", 80, rowErrors) ?? "Unknown";
                var rfqReferenceNumber = OptionalText(sheet, rowNumber, columns, "RFQ/REF No", 500, rowErrors);
                var estimator = Text(sheet, rowNumber, columns, "Estimating Rep", 160, rowErrors) ?? "Unassigned";
                var totalValue = Decimal(sheet, rowNumber, columns, "TotalInPrimaryCurrency", rowErrors) ?? 0m;
                if (totalValue < 0)
                    rowErrors.Add(new EstimatingHistoryImportIssueDto(rowNumber, "TotalInPrimaryCurrency", "Quote value cannot be negative."));
                var numberOfParts = Int(sheet, rowNumber, columns, "Number of Parts in Quote", rowErrors) ?? 0;
                if (numberOfParts < 0)
                    rowErrors.Add(new EstimatingHistoryImportIssueDto(rowNumber, "Number of Parts in Quote", "Number of parts cannot be negative."));
                var dueDate = Date(sheet, rowNumber, columns, "RFQ Due Date", rowErrors);
                var assignedDate = Date(sheet, rowNumber, columns, "Date to Estimating", rowErrors);
                var completionDate = Date(sheet, rowNumber, columns, "Estimating Completion Date", rowErrors);
                var issues = Text(sheet, rowNumber, columns, "Issues?", 240, rowErrors);
                var quoteOnTrack = OptionalText(sheet, rowNumber, columns, "Quote On Track?", 40, rowErrors);
                var complexity = Text(sheet, rowNumber, columns, "Quote Complexity", 80, rowErrors);
                var estimatingStatus = Text(sheet, rowNumber, columns, "Estimating Status", 160, rowErrors);
                var metrics = Metrics(dueDate, assignedDate, completionDate);
                rows.Add(new EstimatingHistoryImportRow(
                    rowNumber,
                    sourceId ?? string.Empty,
                    quoteNumber ?? 0,
                    customer ?? string.Empty,
                    customerContact,
                    salesPerson,
                    quoteStatus,
                    rfqReferenceNumber,
                    estimator,
                    totalValue,
                    dueDate,
                    assignedDate,
                    issues,
                    quoteOnTrack,
                    complexity,
                    numberOfParts,
                    estimatingStatus,
                    completionDate,
                    metrics.OnTimeStatus,
                    metrics.DaysLate,
                    metrics.Workdays,
                    metrics.CompletedMonth,
                    metrics.CompletedYear,
                    metrics.CompletedWeekOfMonth,
                    metrics.CompletedMonthAndWeek,
                    metrics.IsCompleted,
                    metrics.CompletedWeekOfYear,
                    metrics.IsOnTime,
                    metrics.OnTimeRatio));
                errors.AddRange(rowErrors);
            }
            return rows;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(new EstimatingHistoryImportIssueDto(1, null, $"The workbook could not be read: {exception.Message}"));
            return [];
        }
    }

    private static int FindHeaderRow(IXLWorksheet sheet)
        => FindHeaderRow(sheet, "Id", "Number");

    private static int FindHeaderRow(IXLWorksheet sheet, params string[] requiredHeaders)
    {
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var row = 1; row <= Math.Min(10, sheet.LastRowUsed()?.RowNumber() ?? 1); row++)
        {
            var headers = sheet.Row(row).Cells(1, lastColumn).Select(cell => NormalizeHeader(cell.GetString())).ToHashSet();
            if (requiredHeaders.All(header => headers.Contains(NormalizeHeader(header)))) return row;
        }
        return 0;
    }

    private static IReadOnlyDictionary<string, int> Columns(IXLWorksheet sheet, int headerRow)
    {
        var lastColumn = sheet.Row(headerRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
        return sheet.Row(headerRow)
            .Cells(1, lastColumn)
            .Where(cell => !string.IsNullOrWhiteSpace(cell.GetString()))
            .GroupBy(cell => NormalizeHeader(cell.GetString()))
            .ToDictionary(group => group.Key, group => group.First().Address.ColumnNumber);
    }

    private static IXLWorksheet BuildMergedSheet(
        XLWorkbook workbook,
        IXLWorksheet sourceSheet,
        int sourceHeaderRow,
        IReadOnlyDictionary<string, int> sourceColumns,
        IXLWorksheet supplementalSheet,
        int supplementalHeaderRow,
        IReadOnlyDictionary<string, int> supplementalColumns,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var merged = workbook.Worksheets.Add("__EstimatingImport");
        for (var column = 0; column < ImportHeaders.Length; column++)
            merged.Cell(1, column + 1).Value = ImportHeaders[column];

        if (!sourceColumns.TryGetValue(NormalizeHeader("Index"), out var sourceIndexColumn))
        {
            errors.Add(new EstimatingHistoryImportIssueDto(sourceHeaderRow, "Index", "The legacy workbook needs an Index column to join its quote tables."));
            return merged;
        }

        var supplementalIndexColumn = supplementalColumns[NormalizeHeader("Index")];
        var supplementalRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var supplementalLastRow = supplementalSheet.LastRowUsed()?.RowNumber() ?? supplementalHeaderRow;
        for (var row = supplementalHeaderRow + 1; row <= supplementalLastRow; row++)
        {
            var index = Clean(supplementalSheet.Cell(row, supplementalIndexColumn).GetFormattedString());
            if (index is null) continue;
            if (!supplementalRows.TryAdd(index, row))
                errors.Add(new EstimatingHistoryImportIssueDto(row, "Index", $"Index '{index}' appears more than once in the estimating table."));
        }

        var sourceLastRow = sourceSheet.LastRowUsed()?.RowNumber() ?? sourceHeaderRow;
        var targetRow = 2;
        for (var sourceRow = sourceHeaderRow + 1; sourceRow <= sourceLastRow; sourceRow++)
        {
            if (IsBlank(sourceSheet, sourceRow, sourceColumns)) continue;
            var index = Clean(sourceSheet.Cell(sourceRow, sourceIndexColumn).GetFormattedString());
            if (index is null || !supplementalRows.TryGetValue(index, out var supplementalRow))
            {
                errors.Add(new EstimatingHistoryImportIssueDto(sourceRow, "Index", $"No estimating data row matches Index '{index ?? "(blank)"}'."));
                targetRow++;
                continue;
            }

            for (var column = 0; column < ImportHeaders.Length; column++)
            {
                var header = NormalizeHeader(ImportHeaders[column]);
                IXLCell? source = null;
                if (sourceColumns.TryGetValue(header, out var sourceColumn))
                    source = sourceSheet.Cell(sourceRow, sourceColumn);
                else if (supplementalColumns.TryGetValue(header, out var supplementalColumn))
                    source = supplementalSheet.Cell(supplementalRow, supplementalColumn);
                if (source is not null)
                    CopyCell(source, merged.Cell(targetRow, column + 1));
            }
            targetRow++;
        }
        return merged;
    }

    private static void CopyCell(IXLCell source, IXLCell target)
    {
        if (source.HasFormula)
            target.FormulaA1 = source.FormulaA1;
        else
            target.Value = source.Value;
    }

    private static bool IsBlank(IXLWorksheet sheet, int row, IReadOnlyDictionary<string, int> columns) =>
        columns.Values.All(column => sheet.Cell(row, column).IsEmpty());

    private static string? RequiredText(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        int maxLength,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var value = Text(sheet, row, columns, header, maxLength, errors);
        if (value is null)
            errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"{header} is required."));
        return value;
    }

    private static string? Text(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        int maxLength,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var value = Clean(Cell(sheet, row, columns, header).GetFormattedString());
        if (value?.Length > maxLength)
            errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"{header} cannot exceed {maxLength} characters."));
        return value;
    }

    private static string? OptionalText(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        int maxLength,
        List<EstimatingHistoryImportIssueDto> errors) =>
        columns.ContainsKey(NormalizeHeader(header))
            ? Text(sheet, row, columns, header, maxLength, errors)
            : null;

    private static int? RequiredInt(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var value = Int(sheet, row, columns, header, errors);
        if (!value.HasValue)
            errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"{header} is required."));
        return value;
    }

    private static int? Int(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var cell = Cell(sheet, row, columns, header);
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<int>(out var number)) return number;
        var value = Clean(cell.GetFormattedString());
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"'{value}' is not a valid whole number."));
        return null;
    }

    private static decimal? Decimal(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var cell = Cell(sheet, row, columns, header);
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<decimal>(out var number)) return number;
        var value = Clean(cell.GetFormattedString());
        if (decimal.TryParse(value, NumberStyles.Currency, CultureInfo.InvariantCulture, out number)
            || decimal.TryParse(value, NumberStyles.Currency, CultureInfo.CurrentCulture, out number)) return number;
        errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"'{value}' is not a valid currency value."));
        return null;
    }

    private static DateTime? Date(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<EstimatingHistoryImportIssueDto> errors)
    {
        var cell = Cell(sheet, row, columns, header);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().Date;
        if (cell.DataType == XLDataType.Number)
        {
            try { return DateTime.FromOADate(cell.GetDouble()).Date; }
            catch (ArgumentException) { }
        }
        var value = Clean(cell.GetFormattedString());
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)) return parsed.Date;
        errors.Add(new EstimatingHistoryImportIssueDto(row, header, $"'{value}' is not a valid date."));
        return null;
    }

    private static IXLCell Cell(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header) => sheet.Cell(row, columns[NormalizeHeader(header)]);

    private static string HeaderAt(IReadOnlyDictionary<string, int> columns, int column) =>
        ImportHeaders.FirstOrDefault(header => columns.TryGetValue(NormalizeHeader(header), out var number) && number == column)
        ?? $"Column {XLHelper.GetColumnLetterFromNumber(column)}";

    private static QuoteMetrics Metrics(DateTime? dueDate, DateTime? assignedDate, DateTime? completionDate)
    {
        var completed = completionDate.HasValue;
        var onTime = completed && dueDate.HasValue && completionDate!.Value.Date <= dueDate.Value.Date;
        var late = completed && dueDate.HasValue && completionDate!.Value.Date > dueDate.Value.Date;
        var status = onTime ? EstimatingOnTimeStatuses.OnTime : late ? EstimatingOnTimeStatuses.Late : EstimatingOnTimeStatuses.NoData;
        var daysLate = late ? BusinessDays(dueDate!.Value.Date.AddDays(1), completionDate!.Value.Date) : 0;
        int? workdays = completed && assignedDate.HasValue && completionDate!.Value.Date >= assignedDate.Value.Date
            ? BusinessDays(assignedDate.Value.Date, completionDate.Value.Date)
            : null;
        var completion = completionDate?.Date;
        int? weekOfMonth = completion.HasValue ? ((completion.Value.Day - 1) / 7) + 1 : null;
        return new QuoteMetrics(
            status,
            daysLate,
            workdays,
            completion?.ToString("MMMM", CultureInfo.InvariantCulture),
            completion?.Year,
            weekOfMonth,
            completion.HasValue ? $"{completion.Value:MMMM} Week # {weekOfMonth:00}" : null,
            completed,
            completion.HasValue ? WeekOfYear(completion.Value) : null,
            onTime,
            completed && dueDate.HasValue ? (onTime ? 1m : 0m) : null);
    }

    private static int BusinessDays(DateTime start, DateTime end)
    {
        var days = 0;
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) days++;
        return days;
    }

    private static int WeekOfYear(DateTime date)
    {
        var januaryFirst = new DateTime(date.Year, 1, 1);
        var mondayOffset = ((int)januaryFirst.DayOfWeek + 6) % 7;
        return ((date.DayOfYear + mondayOffset - 1) / 7) + 1;
    }

    private static void Apply(
        EstimatingQuoteHistoryRecord record,
        EstimatingHistoryImportRow row,
        Guid batchId,
        string actor,
        DateTimeOffset now)
    {
        record.QuoteNumber = row.QuoteNumber;
        record.Customer = row.Customer;
        record.CustomerContact = row.CustomerContact;
        record.SalesPerson = row.SalesPerson;
        record.QuoteStatus = row.QuoteStatus;
        record.RfqReferenceNumber = row.RfqReferenceNumber;
        record.EstimatingRep = row.EstimatingRep;
        record.TotalValue = row.TotalValue;
        record.RfqDueDate = row.RfqDueDate;
        record.DateToEstimating = row.DateToEstimating;
        record.Issues = row.Issues;
        record.QuoteOnTrack = row.QuoteOnTrack;
        record.QuoteComplexity = row.QuoteComplexity;
        record.NumberOfParts = row.NumberOfParts;
        record.EstimatingStatus = row.EstimatingStatus;
        record.EstimatingCompletionDate = row.EstimatingCompletionDate;
        record.OnTimeStatus = row.OnTimeStatus;
        record.DaysLate = row.DaysLate;
        record.Workdays = row.Workdays;
        record.CompletedMonth = row.CompletedMonth;
        record.CompletedYear = row.CompletedYear;
        record.CompletedWeekOfMonth = row.CompletedWeekOfMonth;
        record.CompletedMonthAndWeek = row.CompletedMonthAndWeek;
        record.IsCompleted = row.IsCompleted;
        record.CompletedWeekOfYear = row.CompletedWeekOfYear;
        record.IsOnTime = row.IsOnTime;
        record.OnTimeRatio = row.OnTimeRatio;
        record.LastImportBatchId = batchId;
        record.UpdatedAt = now;
        record.UpdatedBy = actor;
    }

    private static bool Equivalent(EstimatingQuoteHistoryRecord record, EstimatingHistoryImportRow row) =>
        record.QuoteNumber == row.QuoteNumber
        && Same(record.Customer, row.Customer)
        && Same(record.CustomerContact, row.CustomerContact)
        && Same(record.SalesPerson, row.SalesPerson)
        && Same(record.QuoteStatus, row.QuoteStatus)
        && Same(record.RfqReferenceNumber, row.RfqReferenceNumber)
        && Same(record.EstimatingRep, row.EstimatingRep)
        && record.TotalValue == row.TotalValue
        && record.RfqDueDate?.Date == row.RfqDueDate?.Date
        && record.DateToEstimating?.Date == row.DateToEstimating?.Date
        && Same(record.Issues, row.Issues)
        && Same(record.QuoteOnTrack, row.QuoteOnTrack)
        && Same(record.QuoteComplexity, row.QuoteComplexity)
        && record.NumberOfParts == row.NumberOfParts
        && Same(record.EstimatingStatus, row.EstimatingStatus)
        && record.EstimatingCompletionDate?.Date == row.EstimatingCompletionDate?.Date;

    private static EstimatingHistoryImportChangeDto Change(EstimatingHistoryImportRow row, string type) =>
        new(row.RowNumber, row.SourceId, row.QuoteNumber, row.Customer, type);

    private static EstimatingHistoryImportValidationDto ToDto(EstimatingHistoryImportReview review) => new(
        review.Id,
        review.ExpiresAt,
        review.FileName,
        review.TotalRows,
        review.NewRecords,
        review.UpdatedRecords,
        review.UnchangedRecords,
        review.Errors.Select(error => error.Row).Distinct().Count(),
        review.Errors,
        review.Changes,
        review.Errors.Count == 0 && review.NewRecords + review.UpdatedRecords > 0);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeHeader(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static bool Same(string? left, string? right) => string.Equals(Clean(left), Clean(right), StringComparison.Ordinal);

    private sealed record QuoteMetrics(
        string OnTimeStatus,
        int DaysLate,
        int? Workdays,
        string? CompletedMonth,
        int? CompletedYear,
        int? CompletedWeekOfMonth,
        string? CompletedMonthAndWeek,
        bool IsCompleted,
        int? CompletedWeekOfYear,
        bool IsOnTime,
        decimal? OnTimeRatio);
}
