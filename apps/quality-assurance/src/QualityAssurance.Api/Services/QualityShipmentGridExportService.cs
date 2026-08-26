using ClosedXML.Excel;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed record QualityShipmentGridExportFile(byte[] Content, string FileName);

public sealed class QualityShipmentGridExportService(QualityShipmentService shipments)
{
    private sealed record ExportColumn(string Key, string Label);

    public async Task<QualityShipmentGridExportFile> CreateAsync(
        QualityAssuranceAccessProfile access,
        string? status,
        string? scope,
        string? sort,
        string? direction,
        string? search,
        string? shipmentStatus,
        IReadOnlyCollection<string>? customer,
        string? assignee,
        CancellationToken cancellationToken)
    {
        var rows = await shipments.ExportRowsAsync(
            access, status, scope, sort, direction, search,
            shipmentStatus, customer, assignee, cancellationToken);
        var visible = QualityFieldAccess.For(access)
            .Where(field => field.CanView)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = BuildColumns(visible, access);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Grid Results");
        for (var index = 0; index < columns.Count; index++)
            sheet.Cell(1, index + 1).Value = columns[index].Label;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                SetValue(sheet.Cell(rowIndex + 2, columnIndex + 1), columns[columnIndex].Key, row);
        }

        var lastRow = Math.Max(2, rows.Count + 1);
        if (rows.Count > 0)
            sheet.Range(1, 1, lastRow, columns.Count)
                .CreateTable("QualityShippingGridResults")
                .Theme = XLTableTheme.TableStyleMedium2;
        else
        {
            sheet.Cell(2, 1).Value = "No Shipping Status records matched the current search and filters.";
            sheet.Range(2, 1, 2, columns.Count).Merge();
            sheet.Range(1, 1, 1, columns.Count).Style.Fill.BackgroundColor = XLColor.FromHtml("#173B63");
            sheet.Range(1, 1, 1, columns.Count).Style.Font.FontColor = XLColor.White;
        }

        foreach (var column in columns.Select((value, index) => (value, index)))
        {
            var sheetColumn = sheet.Column(column.index + 1);
            if (column.value.Key == "dollarValue") sheetColumn.Style.NumberFormat.Format = "\"$\"#,##0.00";
            if (column.value.Key is "qaArrivalDate" or "shipDate" or "sourceRequestedDate")
                sheetColumn.Style.DateFormat.Format = "mmm d, yyyy";
            if (column.value.Key == "lastWorkedAt") sheetColumn.Style.DateFormat.Format = "mmm d, yyyy h:mm AM/PM";
        }
        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(Math.Min(2, columns.Count));
        sheet.Columns().AdjustToContents(1, Math.Min(lastRow, 250));
        foreach (var column in sheet.ColumnsUsed()) column.Width = Math.Min(column.Width + 2, 48);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new QualityShipmentGridExportFile(
            stream.ToArray(),
            $"quality-shipping-results-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    private static List<ExportColumn> BuildColumns(
        IReadOnlySet<string> visible,
        QualityAssuranceAccessProfile access)
    {
        var columns = new List<ExportColumn>();
        Add("status", "Status");
        Add("salesOrderNumber", "Sales Order #");
        Add("qaArrivalDate", "QA Arrival");
        Add("partNumber", "Part Number");
        Add("purchaseOrderNumber", "P.O.");
        Add("customer", "Customer");
        Add("quantity", "Quantity");
        Add("dollarValue", "Dollar Value");
        Add("shipDate", "Ship By");
        Add("holdReason", "Hold Reason");
        Add("sourceRequestedDate", "Source Scheduled");
        if (visible.Contains("nextAction")
            || access.HasPermission(QualityAssurancePermissions.AssignmentView))
            columns.Add(new ExportColumn("action", "Action"));
        Add("lastWorkedAt", "Last Worked");
        Add("comments", "Latest Comment");
        columns.Add(new ExportColumn("queueAge", "Queue Age (Days)"));
        return columns;

        void Add(string key, string label)
        {
            if (visible.Contains(key)) columns.Add(new ExportColumn(key, label));
        }
    }

    private static void SetValue(IXLCell cell, string key, QualityShipmentDto row)
    {
        switch (key)
        {
            case "status": cell.Value = row.Status ?? string.Empty; break;
            case "salesOrderNumber": cell.Value = row.SalesOrderNumber ?? string.Empty; break;
            case "qaArrivalDate" when row.QaArrivalDate.HasValue: cell.Value = row.QaArrivalDate.Value.ToDateTime(TimeOnly.MinValue); break;
            case "partNumber": cell.Value = row.PartNumber ?? string.Empty; break;
            case "purchaseOrderNumber": cell.Value = row.PurchaseOrderNumber ?? string.Empty; break;
            case "customer": cell.Value = row.Customer ?? string.Empty; break;
            case "quantity" when row.Quantity.HasValue: cell.Value = row.Quantity.Value; break;
            case "dollarValue" when row.DollarValue.HasValue: cell.Value = row.DollarValue.Value; break;
            case "shipDate" when row.ShipDate.HasValue: cell.Value = row.ShipDate.Value.ToDateTime(TimeOnly.MinValue); break;
            case "holdReason": cell.Value = row.HoldReason ?? string.Empty; break;
            case "sourceRequestedDate" when row.SourceRequestedDate.HasValue: cell.Value = row.SourceRequestedDate.Value.ToDateTime(TimeOnly.MinValue); break;
            case "action": cell.Value = row.AssignedDisplayName ?? row.AssignedGroupName ?? row.NextAction ?? "Unassigned"; break;
            case "lastWorkedAt" when row.LastWorkedAt.HasValue: cell.Value = row.LastWorkedAt.Value.LocalDateTime; break;
            case "comments": cell.Value = row.Comments ?? string.Empty; break;
            case "queueAge": cell.Value = Math.Max(0, (DateTimeOffset.UtcNow - row.CreatedAt).Days); break;
        }
    }
}
