using System.Globalization;
using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public sealed record ReportSourceRow(Dictionary<string, JsonElement> Records, Dictionary<string, long> Keys);

public static class ReportPresentation
{
    public static List<ReportColumn> Columns(ReportDefinition definition) => definition.OutputColumns.Count > 0
        ? definition.OutputColumns
        : definition.Sheets.SelectMany(s => s.Columns).DistinctBy(c => (c.SheetId, c.Path)).ToList();

    public static ReportSheetResult Combine(ReportDefinition definition, Dictionary<string, List<ReportSourceRow>> data)
    {
        var grain = definition.DetailSheetId ?? definition.Sheets[0].Id;
        var columns = Columns(definition);
        List<string> Chain(string id)
        {
            var chain = new List<string>();
            for (string? cursor = id; cursor is not null; cursor = definition.Sheets.Single(s => s.Id == cursor).ParentSheetId)
                chain.Add(cursor);
            return chain;
        }
        var grainChain = Chain(grain);
        var readers = columns.Select(column =>
        {
            if (grainChain.Contains(column.SheetId))
                return (Func<ReportSourceRow, object?>)(row => FulcrumReportRunner.Read(row.Records[column.SheetId], column.Path));
            var common = Chain(column.SheetId).FirstOrDefault(grainChain.Contains)
                ?? throw new ReportValidationException("A combined report needs related records. Use separate tables for independent sources.");
            // Aggregate other branches by their shared parent, never a Cartesian join.
            var values = data[column.SheetId].GroupBy(row => row.Keys[common]).ToDictionary(group => group.Key, group =>
            {
                var cells = group.Select(row => FulcrumReportRunner.Read(row.Records[column.SheetId], column.Path)).ToList();
                return cells.Count switch { 0 => null, 1 => cells[0], _ => (object)string.Join("\n", cells.Select(value => Display(value, column.Format))) };
            });
            return row => values.GetValueOrDefault(row.Keys[common]);
        }).ToList();
        var rows = data[grain].Select(row => readers.Select(read => read(row)).ToArray()).ToList();
        if (definition.OutputSortColumn is { } sort)
        {
            var comparer = Comparer<object?>.Create((left, right) => left is decimal l && right is decimal r
                ? l.CompareTo(r) : StringComparer.OrdinalIgnoreCase.Compare(left?.ToString(), right?.ToString()));
            rows = (definition.OutputSortDescending ? rows.OrderByDescending(r => r[sort], comparer) : rows.OrderBy(r => r[sort], comparer)).ToList();
        }
        return new("__report", "Report", columns, rows);
    }

    public static ReportSheetResult Size(ReportSheetResult result, bool autoSize)
    {
        if (result.Rows.Any(row => row.Any(value => value is string text && text.Length > 32767)))
            throw new ReportValidationException("A report cell exceeds Excel's text limit. Choose a more detailed row type or fewer fields.");
        if (!autoSize) return result;
        var columns = result.Columns.Select((column, index) =>
        {
            var longest = result.Rows.Select(row => Display(row[index], column.Format))
                .Prepend(column.Header).SelectMany(text => text.Split('\n')).Select(line => line.Length).DefaultIfEmpty(0).Max();
            return column with { Width = Math.Clamp(longest + 3, 12, 60) };
        }).ToList();
        return result with { Columns = columns };
    }

    public static string Display(object? value, string format) => value switch
    {
        null => "",
        bool flag => flag ? "TRUE" : "FALSE",
        decimal number when format == "currency" => number.ToString("$#,##0.00;($#,##0.00)", CultureInfo.InvariantCulture),
        decimal number when format == "number" => number.ToString("#,##0.########", CultureInfo.InvariantCulture),
        decimal number when format == "duration" => Duration(number),
        string text when format == "date" && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date.ToString("yyyy-MM-dd"),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static string Duration(decimal days)
    {
        if (days < 0 || days > 10675199)
            throw new ReportValidationException("A duration is outside the supported range. Select an appropriate time field or change its display format.");
        var seconds = (long)decimal.Round(days * 86400, 0, MidpointRounding.AwayFromZero);
        return $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
    }
}
