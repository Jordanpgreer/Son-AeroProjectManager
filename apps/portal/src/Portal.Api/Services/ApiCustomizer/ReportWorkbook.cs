using System.Globalization;
using ClosedXML.Excel;

namespace Portal.Api.Services.ApiCustomizer;

public static class ReportWorkbook
{
    public static byte[] Create(ReportSnapshot snapshot)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = snapshot.Result.Name;
        workbook.Properties.Subject = snapshot.Result.Sample ? "Arda sample data - not Fulcrum records" : "Arda Fulcrum report";
        foreach (var result in snapshot.Result.Sheets)
        {
            var definition = snapshot.Definition.Sheets.FirstOrDefault(s => s.Id == result.Id) ?? new ReportSheet();
            var sheet = workbook.Worksheets.Add(result.Name);
            for (var columnIndex = 0; columnIndex < result.Columns.Count; columnIndex++)
            {
                var column = result.Columns[columnIndex];
                var x = definition.StartColumn + columnIndex;
                sheet.Column(x).Width = column.Width;
                var header = sheet.Cell(definition.StartRow, x);
                header.SetValue(column.Header);
                header.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + snapshot.Definition.HeaderColor);
                header.Style.Font.FontColor = XLColor.White;
                header.Style.Font.Bold = true;
                header.Style.Alignment.WrapText = true;
                for (var rowIndex = 0; rowIndex < result.Rows.Count; rowIndex++)
                {
                    var cell = sheet.Cell(definition.StartRow + rowIndex + 1, x);
                    var value = result.Rows[rowIndex][columnIndex];
                    if (value is decimal number && column.Format is "number" or "currency" or "duration")
                    {
                        cell.SetValue((double)number);
                        cell.Style.NumberFormat.Format = column.Format == "duration" ? "[h]:mm:ss" : column.Format == "currency" ? "$#,##0.00;[Red]($#,##0.00)" : "#,##0.########";
                    }
                    else if (column.Format == "date" && value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    { cell.SetValue(date.DateTime); cell.Style.NumberFormat.Format = "yyyy-mm-dd"; }
                    else if (column.Format == "boolean" && value is bool boolean) cell.SetValue(boolean);
                    // SetValue(string) preserves identifiers and never interprets source text as a formula.
                    else if (value is not null) cell.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    cell.Style.Alignment.WrapText = snapshot.Definition.AutoSize || snapshot.Definition.WrapText;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                    if (rowIndex % 2 == 1) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F5F7");
                }
            }
            if (snapshot.Definition.AutoSize)
            {
                int Lines(string text, int width) => text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)Math.Max(1, width - 3))));
                sheet.Row(definition.StartRow).Height = Math.Min(409, 8 + 15 * result.Columns.Max(c => Lines(c.Header, c.Width)));
                for (var i = 0; i < result.Rows.Count; i++)
                    sheet.Row(definition.StartRow + i + 1).Height = Math.Min(409, 8 + 15 * result.Columns.Select((c, index) => Lines(ReportPresentation.Display(result.Rows[i][index], c.Format), c.Width)).Max());
            }
            if (snapshot.Definition.FreezeHeaders) sheet.SheetView.FreezeRows(definition.StartRow);
            if (snapshot.Definition.AutoFilter && result.Rows.Count > 0)
                sheet.Range(definition.StartRow, definition.StartColumn,
                    definition.StartRow + result.Rows.Count, definition.StartColumn + result.Columns.Count - 1).SetAutoFilter();
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
