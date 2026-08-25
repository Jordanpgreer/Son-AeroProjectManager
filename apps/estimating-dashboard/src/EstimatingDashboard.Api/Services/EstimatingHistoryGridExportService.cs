using ClosedXML.Excel;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingHistoryGridExportRequest(
    string? Search,
    string? Estimator,
    string? SalesPerson,
    string? Customer,
    string? QuoteStatus,
    string? EstimatingStatus,
    string? Complexity,
    string? Issues,
    string? QuoteOnTrack,
    string? View,
    string? Completion,
    string? OnTime,
    DateTime? DueFrom,
    DateTime? DueTo,
    DateTime? CompletedFrom,
    DateTime? CompletedTo,
    decimal? MinimumValue,
    decimal? MaximumValue,
    string? Sort,
    string? Direction);

public sealed record EstimatingHistoryGridExportFile(byte[] Content, string FileName);

public sealed class EstimatingHistoryGridExportService(EstimatingHistoryQueryService queries)
{
    private const int ExportPageSize = 200;

    public async Task<EstimatingHistoryGridExportFile> CreateAsync(
        EstimatingHistoryGridExportRequest request,
        CancellationToken cancellationToken)
    {
        var records = new List<EstimatingHistoryRowDto>();
        var page = 1;
        EstimatingHistoryPageDto result;
        do
        {
            result = await queries.GetPageAsync(
                request.Search,
                request.Estimator,
                request.SalesPerson,
                request.Customer,
                request.QuoteStatus,
                request.EstimatingStatus,
                request.Complexity,
                request.Issues,
                request.QuoteOnTrack,
                request.View,
                request.Completion,
                request.OnTime,
                request.DueFrom,
                request.DueTo,
                request.CompletedFrom,
                request.CompletedTo,
                request.MinimumValue,
                request.MaximumValue,
                request.Sort,
                request.Direction,
                page,
                ExportPageSize,
                cancellationToken);
            records.AddRange(result.Records);
            page++;
        } while (records.Count < result.Total);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Grid Results");
        var headers = new[]
        {
            "Quote", "Customer", "Customer Contact", "Salesperson", "Quote Status",
            "RFQ / Reference", "Estimator", "Total Value", "RFQ Due", "Assigned",
            "Issues", "On Track?", "Complexity", "Parts", "Estimating Status",
            "Completed", "On-Time Status", "Days Late", "Workdays", "Source ID"
        };

        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var row = index + 2;
            sheet.Cell(row, 1).Value = record.QuoteNumber;
            sheet.Cell(row, 2).Value = record.Customer;
            sheet.Cell(row, 3).Value = record.CustomerContact;
            sheet.Cell(row, 4).Value = record.SalesPerson;
            sheet.Cell(row, 5).Value = record.QuoteStatus;
            sheet.Cell(row, 6).Value = record.RfqReferenceNumber;
            sheet.Cell(row, 7).Value = record.EstimatingRep;
            sheet.Cell(row, 8).Value = record.TotalValue;
            SetDate(sheet.Cell(row, 9), record.RfqDueDate);
            SetDate(sheet.Cell(row, 10), record.DateToEstimating);
            sheet.Cell(row, 11).Value = record.Issues;
            sheet.Cell(row, 12).Value = record.QuoteOnTrack;
            sheet.Cell(row, 13).Value = record.QuoteComplexity;
            sheet.Cell(row, 14).Value = record.NumberOfParts;
            sheet.Cell(row, 15).Value = record.EstimatingStatus;
            SetDate(sheet.Cell(row, 16), record.EstimatingCompletionDate);
            sheet.Cell(row, 17).Value = record.OnTimeStatus;
            sheet.Cell(row, 18).Value = record.DaysLate;
            if (record.Workdays.HasValue)
                sheet.Cell(row, 19).Value = record.Workdays.Value;
            sheet.Cell(row, 20).Value = record.SourceId;
        }

        var finalRow = Math.Max(2, records.Count + 1);
        if (records.Count > 0)
            sheet.Range(1, 1, finalRow, headers.Length).CreateTable("EstimatingLogResults").Theme = XLTableTheme.TableStyleMedium2;
        else
        {
            sheet.Cell(2, 1).Value = "No estimating log records matched the current search and filters.";
            sheet.Range(2, 1, 2, headers.Length).Merge();
            sheet.Range(1, 1, 1, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#173B63");
            sheet.Range(1, 1, 1, headers.Length).Style.Font.FontColor = XLColor.White;
        }

        sheet.Column(8).Style.NumberFormat.Format = "\"$\"#,##0.00";
        sheet.Columns(9, 10).Style.DateFormat.Format = "mmm d, yyyy";
        sheet.Column(16).Style.DateFormat.Format = "mmm d, yyyy";
        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(2);
        sheet.Columns().AdjustToContents(1, Math.Min(finalRow, 250));
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Min(column.Width + 2, 42);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new EstimatingHistoryGridExportFile(
            stream.ToArray(),
            $"estimating-log-results-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    private static void SetDate(IXLCell cell, DateTime? value)
    {
        if (value.HasValue)
            cell.Value = value.Value;
    }
}
