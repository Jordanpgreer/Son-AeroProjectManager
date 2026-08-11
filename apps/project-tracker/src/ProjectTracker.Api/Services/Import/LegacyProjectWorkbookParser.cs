using System.Globalization;
using ClosedXML.Excel;
using ProjectTracker.Api.Dtos;

namespace ProjectTracker.Api.Services.Import;

internal static class LegacyProjectWorkbookParser
{
    private const string MultiProjectFormat = "Legacy multi-project tracker workbook";
    private const string SingleProjectFormat = "Legacy single-project Gantt schedule";

    internal sealed record Result(
        ControlledImportPayload Payload,
        byte[] NormalizedWorkbook);

    public static bool TryParse(
        XLWorkbook workbook,
        string fileName,
        List<ImportIssueDto> errors,
        out Result result)
    {
        var singleSheet = workbook.Worksheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, "Project Gantt Chart", StringComparison.OrdinalIgnoreCase)
            && HasHeaders(sheet, 5, (1, "ID"), (2, "Task Title"), (3, "Phase"), (4, "Start Date")));
        if (singleSheet is not null)
        {
            var payload = ParseSingleProject(singleSheet, fileName, errors);
            result = new Result(payload, BuildNormalizedWorkbook(payload));
            return true;
        }

        var projectSheets = workbook.Worksheets
            .Where(sheet => !string.Equals(sheet.Name, "Holiday Schedule", StringComparison.OrdinalIgnoreCase))
            .Where(sheet => HasHeaders(sheet, 8, (2, "ID"), (3, "Task Title"), (4, "Phase"), (5, "Start Date")))
            .ToList();
        if (projectSheets.Count > 0)
        {
            var payload = ParseMultiProject(projectSheets, errors);
            result = new Result(payload, BuildNormalizedWorkbook(payload));
            return true;
        }

        result = null!;
        return false;
    }

    public static byte[] BuildNormalizedWorkbook(ControlledImportPayload payload)
    {
        using var workbook = new XLWorkbook();
        var projects = workbook.AddWorksheet(ControlledWorkbookImportService.ProjectsSheet);
        var operations = workbook.AddWorksheet(ControlledWorkbookImportService.OperationsSheet);
        WriteHeaders(projects, ControlledWorkbookImportService.ProjectHeaders);
        WriteHeaders(operations, ControlledWorkbookImportService.OperationHeaders);

        var rowNumber = 2;
        foreach (var row in payload.Projects)
        {
            projects.Cell(rowNumber, 1).Value = row.Key;
            projects.Cell(rowNumber, 2).Value = row.ProgramName;
            projects.Cell(rowNumber, 3).Value = row.CustomerName;
            projects.Cell(rowNumber, 4).Value = row.ProgramManager ?? string.Empty;
            projects.Cell(rowNumber, 5).Value = row.Engineer ?? string.Empty;
            projects.Cell(rowNumber, 6).Value = row.SalesOrderNumber ?? string.Empty;
            projects.Cell(rowNumber, 7).Value = row.JobNumber ?? string.Empty;
            projects.Cell(rowNumber, 8).Value = row.PriorityRank;
            SetDate(projects.Cell(rowNumber, 9), row.CompletedOn);
            rowNumber++;
        }

        rowNumber = 2;
        foreach (var row in payload.Operations)
        {
            operations.Cell(rowNumber, 1).Value = row.ProjectKey;
            operations.Cell(rowNumber, 2).Value = row.Key;
            operations.Cell(rowNumber, 3).Value = row.Sequence;
            operations.Cell(rowNumber, 4).Value = row.Title;
            operations.Cell(rowNumber, 5).Value = row.Phase ?? string.Empty;
            operations.Cell(rowNumber, 6).Value = row.WorkStation ?? string.Empty;
            operations.Cell(rowNumber, 7).Value = row.DependencyKey ?? string.Empty;
            operations.Cell(rowNumber, 8).Value = row.StartDateLocked ? "Yes" : "No";
            SetDate(operations.Cell(rowNumber, 9), row.StartDate);
            SetDate(operations.Cell(rowNumber, 10), row.OriginalStartDate);
            SetDate(operations.Cell(rowNumber, 11), row.EndDate);
            SetDate(operations.Cell(rowNumber, 12), row.OriginalEndDate);
            operations.Cell(rowNumber, 13).Value = row.EstimatedDuration;
            operations.Cell(rowNumber, 14).Value = row.ActualDuration;
            operations.Cell(rowNumber, 15).Value = row.PercentComplete;
            operations.Cell(rowNumber, 15).Style.NumberFormat.Format = "0%";
            operations.Cell(rowNumber, 16).Value = row.Notes ?? string.Empty;
            operations.Cell(rowNumber, 18).Value = row.ExternalTaskId ?? string.Empty;
            rowNumber++;
        }

        FinishSheet(projects, ControlledWorkbookImportService.ProjectHeaders.Length);
        FinishSheet(operations, ControlledWorkbookImportService.OperationHeaders.Length);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static ControlledImportPayload ParseSingleProject(
        IXLWorksheet sheet,
        string fileName,
        List<ImportIssueDto> errors)
    {
        var rawName = CellText(sheet.Cell(1, 1));
        var fallback = Path.GetFileNameWithoutExtension(fileName);
        var programName = CleanProjectName(rawName, fallback);
        var key = "NEW-LEGACY-PROJECT-1";
        var tasks = new List<LegacyTask>();
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 5, 500);
        for (var row = 6; row <= lastRow; row++)
        {
            var title = CellText(sheet.Cell(row, 2));
            if (string.IsNullOrWhiteSpace(title) || IsAddRow(title)) continue;
            tasks.Add(new LegacyTask(
                row,
                CellText(sheet.Cell(row, 1)) ?? tasks.Count.ToString(CultureInfo.InvariantCulture),
                title,
                CellText(sheet.Cell(row, 3)),
                ReadDate(sheet.Cell(row, 4)),
                ReadDate(sheet.Cell(row, 6)),
                ReadDate(sheet.Cell(row, 5)),
                ReadDate(sheet.Cell(row, 7)),
                ReadInteger(sheet.Cell(row, 8)),
                null,
                ReadPercent(sheet.Cell(row, 9), sheet.Name, row, errors),
                CellText(sheet.Cell(row, 12)),
                CellText(sheet.Cell(row, 10))));
        }

        if (tasks.Count == 0)
            errors.Add(new ImportIssueDto(sheet.Name, 6, "Task Title", "No project operations were found."));

        return BuildPayload(programName, key, tasks, SingleProjectFormat);
    }

    private static ControlledImportPayload ParseMultiProject(
        IReadOnlyList<IXLWorksheet> sheets,
        List<ImportIssueDto> errors)
    {
        var projects = new List<ControlledProjectRow>();
        var operations = new List<ControlledOperationRow>();
        var normalizedProjectRow = 2;
        var normalizedOperationRow = 2;
        var projectNumber = 1;

        foreach (var sheet in sheets)
        {
            var rawProgramName = CellText(sheet.Cell(2, 2));
            var programName = string.IsNullOrWhiteSpace(rawProgramName)
                || rawProgramName.Equals("Part Number Here", StringComparison.OrdinalIgnoreCase)
                    ? sheet.Name.Trim()
                    : rawProgramName.Trim();
            var tasks = new List<LegacyTask>();
            var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 8, 500);
            for (var row = 9; row <= lastRow; row++)
            {
                var title = CellText(sheet.Cell(row, 3));
                if (IsAddRow(title)) break;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var originalStart = ReadDate(sheet.Cell(row, 6));
                var originalEnd = ReadDate(sheet.Cell(row, 8));
                tasks.Add(new LegacyTask(
                    row,
                    CellText(sheet.Cell(row, 2)) ?? tasks.Count.ToString(CultureInfo.InvariantCulture),
                    title,
                    CellText(sheet.Cell(row, 4)),
                    ReadDate(sheet.Cell(row, 5)) ?? originalStart,
                    originalStart,
                    ReadDate(sheet.Cell(row, 7)) ?? originalEnd,
                    originalEnd,
                    ReadInteger(sheet.Cell(row, 9)),
                    ReadInteger(sheet.Cell(row, 10)),
                    ReadPercent(sheet.Cell(row, 11), sheet.Name, row, errors),
                    CellText(sheet.Cell(row, 13)),
                    null));
            }

            if (tasks.Count == 0)
            {
                errors.Add(new ImportIssueDto(sheet.Name, 9, "Task Title", "No project operations were found on this project sheet."));
                continue;
            }

            var projectKey = $"NEW-LEGACY-PROJECT-{projectNumber}";
            projects.Add(new ControlledProjectRow(
                normalizedProjectRow++,
                projectKey,
                null,
                programName,
                string.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                true));
            operations.AddRange(ToOperations(tasks, projectKey, ref normalizedOperationRow));
            projectNumber++;
        }

        return new ControlledImportPayload(projects, operations, MultiProjectFormat);
    }

    private static ControlledImportPayload BuildPayload(
        string programName,
        string projectKey,
        IReadOnlyList<LegacyTask> tasks,
        string sourceFormat)
    {
        var operationRow = 2;
        var project = new ControlledProjectRow(
            2,
            projectKey,
            null,
            programName,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            null,
            true);
        return new ControlledImportPayload(
            [project],
            ToOperations(tasks, projectKey, ref operationRow),
            sourceFormat);
    }

    private static IReadOnlyList<ControlledOperationRow> ToOperations(
        IReadOnlyList<LegacyTask> tasks,
        string projectKey,
        ref int normalizedRow)
    {
        var operationKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tasks.Count; index++)
        {
            var sourceId = tasks[index].SourceId.Trim();
            operationKeys.TryAdd(sourceId, $"NEW-OP-{index + 1}");
        }

        var result = new List<ControlledOperationRow>();
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var key = operationKeys[task.SourceId.Trim()];
            var dependencyKey = !string.IsNullOrWhiteSpace(task.DependencySourceId)
                && operationKeys.TryGetValue(task.DependencySourceId.Trim(), out var mappedDependency)
                    ? mappedDependency
                    : null;
            result.Add(new ControlledOperationRow(
                normalizedRow++,
                projectKey,
                key,
                null,
                index + 1,
                task.Title,
                task.Phase,
                null,
                dependencyKey,
                false,
                task.StartDate,
                task.OriginalStartDate,
                task.EndDate,
                task.OriginalEndDate,
                task.EstimatedDuration,
                task.ActualDuration,
                task.PercentComplete,
                task.Notes,
                task.SourceId));
        }
        return result;
    }

    private static bool HasHeaders(
        IXLWorksheet sheet,
        int row,
        params (int Column, string Header)[] expected) =>
        expected.All(item => NormalizeHeader(CellText(sheet.Cell(row, item.Column))) == NormalizeHeader(item.Header));

    private static string NormalizeHeader(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string CleanProjectName(string? rawName, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(rawName) ? fallback.Trim() : rawName.Trim();
        return value.EndsWith(" Schedule", StringComparison.OrdinalIgnoreCase)
            ? value[..^" Schedule".Length].Trim()
            : value;
    }

    private static bool IsAddRow(string? title) =>
        string.Equals(title?.Trim(), "ADD", StringComparison.OrdinalIgnoreCase);

    private static string? CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        try
        {
            var value = cell.GetString().Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static DateOnly? ReadDate(IXLCell cell)
    {
        try
        {
            if (cell.TryGetValue<DateTime>(out var dateTime))
                return DateOnly.FromDateTime(dateTime);
            if (cell.TryGetValue<double>(out var serial) && serial > 0)
                return DateOnly.FromDateTime(DateTime.FromOADate(serial));
            var text = CellText(cell);
            if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
                || DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                return parsed;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return null;
        }
        return null;
    }

    private static int? ReadInteger(IXLCell cell)
    {
        if (cell.TryGetValue<double>(out var number) && number > 0)
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        var text = CellText(cell);
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        return null;
    }

    private static decimal ReadPercent(
        IXLCell cell,
        string sheet,
        int row,
        ICollection<ImportIssueDto> errors)
    {
        if (cell.IsEmpty()) return 0m;
        decimal value;
        if (cell.TryGetValue<double>(out var number))
        {
            value = (decimal)number;
        }
        else
        {
            var text = CellText(cell);
            if (string.IsNullOrWhiteSpace(text)) return 0m;
            var percentNotation = text.EndsWith('%');
            var numeric = percentNotation ? text[..^1].Trim() : text;
            if (!decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                errors.Add(new ImportIssueDto(sheet, row, "Completion %", $"'{text}' is not a valid completion percentage."));
                return 0m;
            }
            if (percentNotation) value /= 100m;
        }

        if (value > 1m) value /= 100m;
        if (value is >= 0m and <= 1m) return value;
        errors.Add(new ImportIssueDto(sheet, row, "Completion %", "Completion must be between 0% and 100%."));
        return 0m;
    }

    private static void WriteHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var column = 1; column <= headers.Count; column++)
        {
            var cell = sheet.Cell(1, column);
            cell.Value = headers[column - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = headers[column - 1].Contains("Required", StringComparison.Ordinal)
                ? XLColor.FromHtml("#B53A2D")
                : XLColor.FromHtml("#17324D");
            cell.Style.Alignment.WrapText = true;
        }
    }

    private static void FinishSheet(IXLWorksheet sheet, int columnCount)
    {
        var lastRow = Math.Max(sheet.LastRowUsed()?.RowNumber() ?? 1, 2);
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, lastRow, columnCount).SetAutoFilter();
        sheet.Columns(1, columnCount).AdjustToContents();
        foreach (var column in sheet.Columns(1, columnCount))
            column.Width = Math.Clamp(column.Width, 11, 36);
    }

    private static void SetDate(IXLCell cell, DateOnly? date)
    {
        if (date is null) return;
        cell.Value = date.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.NumberFormat.Format = "yyyy-mm-dd";
    }

    private sealed record LegacyTask(
        int SourceRow,
        string SourceId,
        string Title,
        string? Phase,
        DateOnly? StartDate,
        DateOnly? OriginalStartDate,
        DateOnly? EndDate,
        DateOnly? OriginalEndDate,
        int? EstimatedDuration,
        int? ActualDuration,
        decimal PercentComplete,
        string? Notes,
        string? DependencySourceId);
}
