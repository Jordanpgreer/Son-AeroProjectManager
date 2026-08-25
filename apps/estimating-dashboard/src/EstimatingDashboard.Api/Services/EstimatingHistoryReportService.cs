using ClosedXML.Excel;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingHistoryReportFile(
    byte[] Content,
    string FileName);

public sealed class EstimatingHistoryReportService(EstimatingAccessDbContext db)
{
    public async Task<EstimatingHistoryReportFile> CreateAsync(
        string periodValue,
        string? estimator,
        CancellationToken cancellationToken)
    {
        var period = EstimatingHistoryPeriods.Report(periodValue, DateTime.Today);
        var cleanEstimator = Clean(estimator);
        var records = await db.QuoteHistory.AsNoTracking().ToListAsync(cancellationToken);
        var tracked = records.Where(record => IsReportableEstimator(record.EstimatingRep)).ToList();
        if (cleanEstimator is null)
            tracked = tracked.Where(record => !IsFormerEstimator(record.EstimatingRep)).ToList();
        else
            tracked = tracked.Where(record =>
                string.Equals(record.EstimatingRep, cleanEstimator, StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = tracked
            .GroupBy(record => record.EstimatingRep, StringComparer.OrdinalIgnoreCase)
            .Select(group => ReportStats(group.Key, group, period))
            .OrderBy(row => row.Estimator, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Estimator Statistics");
        sheet.Cell("A1").Value = "SON-AERO Estimating Performance Report";
        sheet.Range("A1:H1").Merge();
        sheet.Cell("A2").Value = "Period";
        sheet.Cell("B2").Value = period.Label;
        sheet.Cell("A3").Value = "Generated";
        sheet.Cell("B3").Value = DateTime.Now;
        sheet.Cell("B3").Style.DateFormat.Format = "mmm d, yyyy h:mm AM/PM";
        if (cleanEstimator is not null)
        {
            sheet.Cell("D2").Value = "Estimator filter";
            sheet.Cell("E2").Value = cleanEstimator;
        }

        var headers = new[]
        {
            "Estimator",
            "Quotes in Queue",
            "Completed",
            "Completed Quote Value",
            "Total Quote Value",
            "Avg Completion Workdays",
            "On Time",
            "Late"
        };
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(5, column + 1).Value = headers[column];

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var target = index + 6;
            sheet.Cell(target, 1).Value = row.Estimator;
            sheet.Cell(target, 2).Value = row.InQueue;
            sheet.Cell(target, 3).Value = row.Completed;
            sheet.Cell(target, 4).Value = row.CompletedValue;
            sheet.Cell(target, 5).Value = row.TotalValue;
            if (row.AverageWorkdays.HasValue)
                sheet.Cell(target, 6).Value = row.AverageWorkdays.Value;
            sheet.Cell(target, 7).Value = row.OnTime;
            sheet.Cell(target, 8).Value = row.Late;
        }

        var finalRow = Math.Max(6, rows.Count + 5);
        var tableRange = sheet.Range(5, 1, finalRow, headers.Length);
        if (rows.Count > 0)
            tableRange.CreateTable("EstimatorStatistics").Theme = XLTableTheme.TableStyleMedium2;
        else
        {
            sheet.Cell(6, 1).Value = "No estimator statistics matched this report.";
            sheet.Range("A6:H6").Merge();
            sheet.Range("A5:H5").Style.Fill.BackgroundColor = XLColor.FromHtml("#C73A2B");
            sheet.Range("A5:H5").Style.Font.FontColor = XLColor.White;
        }

        sheet.Range(6, 4, finalRow, 5).Style.NumberFormat.Format = "\"$\"#,##0.00";
        sheet.Range(6, 6, finalRow, 6).Style.NumberFormat.Format = "0.0";
        sheet.SheetView.FreezeRows(5);
        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = Math.Max(sheet.Column(1).Width, 20);
        sheet.Column(4).Width = Math.Max(sheet.Column(4).Width, 22);
        sheet.Column(5).Width = Math.Max(sheet.Column(5).Width, 20);
        sheet.Column(6).Width = Math.Max(sheet.Column(6).Width, 24);
        sheet.Range("A1:H1").Style
            .Font.SetBold()
            .Font.SetFontSize(16)
            .Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#111A24"));
        sheet.Row(1).Height = 27;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var suffix = cleanEstimator is null ? "team" : FileSafe(cleanEstimator);
        return new EstimatingHistoryReportFile(
            stream.ToArray(),
            $"estimating-statistics-{period.Key}-{suffix}-{DateTime.Today:yyyyMMdd}.xlsx");
    }

    private static ReportRow ReportStats(
        string estimator,
        IEnumerable<EstimatingQuoteHistoryRecord> source,
        EstimatingHistoryPeriod period)
    {
        var records = source.ToList();
        var completed = records.Where(record => EstimatingHistoryPeriods.Includes(record, period)).ToList();
        var workdays = completed
            .Where(record => record.Workdays.HasValue && record.Workdays.Value >= 0)
            .Select(record => record.Workdays!.Value)
            .ToList();
        return new ReportRow(
            estimator,
            records.Count(record => string.Equals(record.QuoteStatus, "Needs Approval", StringComparison.OrdinalIgnoreCase)),
            completed.Count,
            completed.Sum(record => record.TotalValue),
            records.Sum(record => record.TotalValue),
            workdays.Count == 0 ? null : Math.Round(workdays.Average(), 1),
            completed.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.OnTime),
            completed.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.Late));
    }

    private static bool IsReportableEstimator(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "Unassigned", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "Sales", StringComparison.OrdinalIgnoreCase);

    private static bool IsFormerEstimator(string value) =>
        string.Equals(value.Trim(), "Abel", StringComparison.OrdinalIgnoreCase)
        || value.Trim().StartsWith("Abel ", StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FileSafe(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));

    private sealed record ReportRow(
        string Estimator,
        int InQueue,
        int Completed,
        decimal CompletedValue,
        decimal TotalValue,
        double? AverageWorkdays,
        int OnTime,
        int Late);
}
