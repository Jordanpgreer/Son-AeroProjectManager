using System.Globalization;
using System.Text;
using QualityAssurance.Api.Dtos;

namespace QualityAssurance.Api.Services;

public sealed class QualityDashboardReportService
{
    private const double PageWidth = 792;
    private const double PageHeight = 612;
    private const double Margin = 42;
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public byte[] Generate(QualityDashboardDto dashboard, string requestedBy, DateTimeOffset generatedAt)
    {
        if (!dashboard.CanViewTeam)
            throw new UnauthorizedAccessException("Team dashboard permission is required to download this report.");

        var pdf = new SimplePdfDocument(PageWidth, PageHeight);
        AddSummaryPages(pdf, dashboard, requestedBy, generatedAt);
        foreach (var person in dashboard.TeamQueues)
            AddPersonPage(pdf, person, generatedAt);
        return pdf.Build("Arda Quality Team Shipping Performance", requestedBy);
    }

    private static void AddSummaryPages(
        SimplePdfDocument pdf,
        QualityDashboardDto dashboard,
        string requestedBy,
        DateTimeOffset generatedAt)
    {
        const int rowsPerPage = 9;
        var people = dashboard.TeamQueues.ToList();
        var pageCount = Math.Max(1, (int)Math.Ceiling(people.Count / (double)rowsPerPage));
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = pdf.AddPage();
            AddHeader(page, pageIndex == 0 ? "Team Shipping Performance" : "Team Summary - Continued", generatedAt);
            var currentPeople = people.Skip(pageIndex * rowsPerPage).Take(rowsPerPage).ToList();
            if (pageIndex == 0)
            {
                var unassigned = dashboard.CanReviewUnassigned ? dashboard.UnassignedQueue : null;
                var open = people.Sum(person => person.Metrics.Open)
                    + dashboard.GroupQueue.Open
                    + (unassigned?.Open ?? 0);
                var overdue = people.Sum(person => person.Metrics.Overdue)
                    + dashboard.GroupQueue.Overdue
                    + (unassigned?.Overdue ?? 0);
                var openValue = SumNullable(
                    people.Select(person => person.Metrics.OpenDollarValue),
                    dashboard.GroupQueue.OpenDollarValue,
                    unassigned?.OpenDollarValue);
                AddKpi(page, 42, 449, 158, "TEAM MEMBERS", people.Count.ToString(UsCulture));
                AddKpi(page, 210, 449, 158, "OPEN WORK", open.ToString("N0", UsCulture));
                AddKpi(page, 378, 449, 158, "PAST DUE", overdue.ToString("N0", UsCulture), overdue > 0);
                AddKpi(page, 546, 449, 204, "OPEN DOLLAR VALUE", Money(openValue));
                page.Text(42, 372, "TEAM SUMMARY", 9, true, "53708A");
                page.Text(42, 356, "Workload, schedule risk, and completion speed by active team member.", 9, false, "607086");
                AddTeamTable(page, currentPeople, dashboard.GroupQueue, unassigned, 335);
                page.Text(42, 69, $"Prepared for {requestedBy}", 8, false, "607086");
            }
            else
            {
                AddTeamTable(page, currentPeople, null, null, 472);
            }
            AddFooter(page, pdf.PageCount);
        }
    }

    private static void AddTeamTable(
        SimplePdfPage page,
        IReadOnlyList<QualityPersonQueueDto> people,
        QualityQueueMetricsDto? groupQueue,
        QualityQueueMetricsDto? unassigned,
        double top)
    {
        var columns = new[] { 42d, 328d, 410d, 492d, 620d, 750d };
        page.FillRect(42, top - 22, 708, 22, "E8F0F6");
        page.Text(columns[0] + 8, top - 15, "PERSON", 7.5, true, "41556D");
        page.TextRight(columns[2] - 8, top - 15, "OPEN", 7.5, true, "41556D");
        page.TextRight(columns[3] - 8, top - 15, "PAST DUE", 7.5, true, "41556D");
        page.TextRight(columns[4] - 8, top - 15, "OPEN VALUE", 7.5, true, "41556D");
        page.TextRight(columns[5] - 8, top - 15, "AVG TIME", 7.5, true, "41556D");

        var y = top - 22;
        foreach (var person in people)
        {
            y -= 22;
            page.Line(42, y + 22, 750, y + 22, "D4DEE8", .55);
            page.Text(50, y + 7, Truncate(person.DisplayName, 34), 8.5, true, "14263A");
            page.TextRight(columns[2] - 8, y + 7, person.Metrics.Open.ToString("N0", UsCulture), 8.5, false, "14263A");
            page.TextRight(columns[3] - 8, y + 7, person.Metrics.Overdue.ToString("N0", UsCulture), 8.5, person.Metrics.Overdue > 0, person.Metrics.Overdue > 0 ? "B33A32" : "14263A");
            page.TextRight(columns[4] - 8, y + 7, Money(person.Metrics.OpenDollarValue), 8.5, false, "14263A");
            page.TextRight(columns[5] - 8, y + 7, Duration(person.Metrics.AverageCompletionHours), 8.5, false, "14263A");
        }

        if (groupQueue is not null)
        {
            y -= 22;
            page.FillRect(42, y, 708, 22, "EDF4FA");
            page.Text(50, y + 7, "Group queue - needs owner", 8.5, true, "295E8D");
            page.TextRight(columns[2] - 8, y + 7, groupQueue.Open.ToString("N0", UsCulture), 8.5, true, "295E8D");
            page.TextRight(columns[3] - 8, y + 7, groupQueue.Overdue.ToString("N0", UsCulture), 8.5, true, "295E8D");
            page.TextRight(columns[4] - 8, y + 7, Money(groupQueue.OpenDollarValue), 8.5, false, "295E8D");
            page.TextRight(columns[5] - 8, y + 7, "Owner review", 8.5, false, "295E8D");
        }

        if (unassigned is not null)
        {
            y -= 22;
            page.FillRect(42, y, 708, 22, "FFF5DF");
            page.Text(50, y + 7, "Needs assignment", 8.5, true, "8A5B00");
            page.TextRight(columns[2] - 8, y + 7, unassigned.Open.ToString("N0", UsCulture), 8.5, true, "8A5B00");
            page.TextRight(columns[3] - 8, y + 7, unassigned.Overdue.ToString("N0", UsCulture), 8.5, true, "8A5B00");
            page.TextRight(columns[4] - 8, y + 7, Money(unassigned.OpenDollarValue), 8.5, false, "8A5B00");
            page.TextRight(columns[5] - 8, y + 7, "Manager review", 8.5, false, "8A5B00");
        }
    }

    private static void AddPersonPage(SimplePdfDocument pdf, QualityPersonQueueDto person, DateTimeOffset generatedAt)
    {
        var page = pdf.AddPage();
        AddHeader(page, person.DisplayName, generatedAt, "TEAM MEMBER DETAIL");
        AddKpi(page, 42, 449, 158, "OPEN", person.Metrics.Open.ToString("N0", UsCulture));
        AddKpi(page, 210, 449, 158, "PAST DUE", person.Metrics.Overdue.ToString("N0", UsCulture), person.Metrics.Overdue > 0);
        AddKpi(page, 378, 449, 178, "OPEN DOLLAR VALUE", Money(person.Metrics.OpenDollarValue));
        AddKpi(page, 566, 449, 184, "AVG COMPLETION", Duration(person.Metrics.AverageCompletionHours));

        page.Text(42, 372, "COMPLETION PERFORMANCE", 9, true, "53708A");
        AddMiniStat(page, 42, 318, 164, "Completed", person.Metrics.Completed.ToString("N0", UsCulture));
        AddMiniStat(page, 216, 318, 164, "Completed value", Money(person.Metrics.CompletedDollarValue));
        AddMiniStat(page, 390, 318, 164, "Completed value YTD", Money(person.Metrics.CompletedDollarValueYtd));
        AddMiniStat(page, 564, 318, 186, "Current quarter", Money(person.Metrics.CompletedDollarValueCurrentQuarter));

        var safeOpen = Math.Max(1, person.Metrics.Open);
        var overdueWidth = 620d * person.Metrics.Overdue / safeOpen;
        page.Text(42, 286, "OPEN WORK SCHEDULE HEALTH", 9, true, "53708A");
        page.FillRect(42, 263, 620, 12, "CFE4D9");
        if (overdueWidth > 0) page.FillRect(42, 263, overdueWidth, 12, "D8655B");
        page.Text(673, 264, person.Metrics.Open == 0 ? "Clear" : $"{person.Metrics.Overdue} at risk", 8, true, person.Metrics.Overdue > 0 ? "B33A32" : "347455");

        page.Text(42, 235, "OPEN WORKLOAD", 9, true, "53708A");
        AddShipmentTable(page, person.OpenShipments, 217);
        AddFooter(page, pdf.PageCount);
    }

    private static void AddShipmentTable(SimplePdfPage page, IReadOnlyList<QualityShipmentDto> shipments, double top)
    {
        page.FillRect(42, top - 21, 708, 21, "E8F0F6");
        page.Text(50, top - 14, "SALES ORDER", 7.3, true, "41556D");
        page.Text(178, top - 14, "PART", 7.3, true, "41556D");
        page.Text(330, top - 14, "CUSTOMER", 7.3, true, "41556D");
        page.Text(514, top - 14, "SHIP BY", 7.3, true, "41556D");
        page.TextRight(742, top - 14, "DOLLAR VALUE", 7.3, true, "41556D");
        var y = top - 21;
        if (shipments.Count == 0)
        {
            page.Text(50, y - 20, "No open shipments assigned.", 9, false, "607086");
            return;
        }
        foreach (var shipment in shipments.Take(7))
        {
            y -= 20;
            page.Line(42, y + 20, 750, y + 20, "D4DEE8", .5);
            page.Text(50, y + 6, Truncate(shipment.SalesOrderNumber ?? "Restricted", 18), 8, true, "14263A");
            page.Text(178, y + 6, Truncate(shipment.PartNumber ?? "Restricted", 22), 8, false, "14263A");
            page.Text(330, y + 6, Truncate(shipment.Customer ?? "Restricted", 28), 8, false, "14263A");
            page.Text(514, y + 6, shipment.ShipDate?.ToString("MMM d, yyyy", UsCulture) ?? "Not set", 8, false, "14263A");
            page.TextRight(742, y + 6, Money(shipment.DollarValue), 8, false, "14263A");
        }
        if (shipments.Count > 7)
            page.Text(42, 43, $"Showing 7 of {shipments.Count:N0} most urgent open records.", 7.5, false, "607086");
    }

    private static void AddHeader(SimplePdfPage page, string title, DateTimeOffset generatedAt, string eyebrow = "QUALITY ASSURANCE")
    {
        page.FillRect(0, 518, PageWidth, 94, "102B4E");
        page.FillRect(0, 514, PageWidth, 4, "3479B8");
        page.Text(42, 580, "ARDA", 12, true, "FFFFFF");
        page.Text(91, 580, "/ QUALITY ASSURANCE", 8, true, "BFD3E8");
        page.Text(42, 548, eyebrow, 8, true, "91B9DA");
        page.Text(42, 526, title, 20, true, "FFFFFF");
        page.TextRight(750, 580, $"Generated {generatedAt.ToLocalTime():MMM d, yyyy h:mm tt}", 8, false, "DCE9F4");
    }

    private static void AddKpi(SimplePdfPage page, double x, double y, double width, string label, string value, bool risk = false)
    {
        page.FillRect(x, y - 58, width, 58, "F4F8FB");
        page.StrokeRect(x, y - 58, width, 58, risk ? "D8655B" : "D4DEE8", .75);
        page.FillRect(x, y - 3, width, 3, risk ? "D8655B" : "3479B8");
        page.Text(x + 12, y - 18, label, 7.3, true, "607086");
        page.Text(x + 12, y - 43, value, 15, true, risk ? "B33A32" : "14263A");
    }

    private static void AddMiniStat(SimplePdfPage page, double x, double y, double width, string label, string value)
    {
        page.FillRect(x, y, width, 39, "F4F8FB");
        page.Text(x + 10, y + 24, label, 7.2, true, "607086");
        page.Text(x + 10, y + 8, value, 10.5, true, "14263A");
    }

    private static void AddFooter(SimplePdfPage page, int pageNumber)
    {
        page.Line(Margin, 31, PageWidth - Margin, 31, "D4DEE8", .5);
        page.Text(Margin, 18, "Arda Quality Assurance - Internal operational report", 7.2, false, "718096");
        page.TextRight(PageWidth - Margin, 18, $"Page {pageNumber}", 7.2, false, "718096");
    }

    private static decimal? SumNullable(IEnumerable<decimal?> values, params decimal?[] additional)
    {
        var materialized = values.Concat(additional).ToList();
        return materialized.Any(value => value.HasValue)
            ? materialized.Sum(value => value ?? 0)
            : null;
    }

    private static string Money(decimal? value) => value.HasValue
        ? value.Value.ToString("C0", UsCulture)
        : "Restricted";

    private static string Duration(double? hours) => hours switch
    {
        null => "No history",
        < 24 => $"{Math.Round(hours.Value):N0} hr",
        _ => $"{hours.Value / 24:N1} days"
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, Math.Max(1, max - 3)), "...");

    private sealed class SimplePdfDocument(double width, double height)
    {
        private readonly List<SimplePdfPage> pages = [];
        public int PageCount => pages.Count;
        public SimplePdfPage AddPage()
        {
            var page = new SimplePdfPage();
            pages.Add(page);
            return page;
        }

        public byte[] Build(string title, string author)
        {
            var objects = new List<byte[]>();
            var pageObjectIds = new List<int>();
            const int catalogId = 1;
            const int pagesId = 2;
            const int regularFontId = 3;
            const int boldFontId = 4;
            objects.Add([]);
            objects.Add([]);
            objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
            objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"));
            foreach (var page in pages)
            {
                var pageId = objects.Count + 1;
                var contentId = pageId + 1;
                pageObjectIds.Add(pageId);
                objects.Add(Ascii($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {width:0.##} {height:0.##}] /Resources << /Font << /F1 {regularFontId} 0 R /F2 {boldFontId} 0 R >> >> /Contents {contentId} 0 R >>"));
                var stream = Ascii(page.Content);
                objects.Add(Concat(Ascii($"<< /Length {stream.Length} >>\nstream\n"), stream, Ascii("\nendstream")));
            }
            var infoId = objects.Count + 1;
            objects.Add(Ascii($"<< /Title ({Escape(title)}) /Author ({Escape(author)}) /Creator (Arda Quality Assurance) >>"));
            objects[catalogId - 1] = Ascii($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
            objects[pagesId - 1] = Ascii($"<< /Type /Pages /Count {pageObjectIds.Count} /Kids [{string.Join(' ', pageObjectIds.Select(id => $"{id} 0 R"))}] >>");

            using var output = new MemoryStream();
            output.Write(Ascii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));
            var offsets = new List<long> { 0 };
            for (var index = 0; index < objects.Count; index++)
            {
                offsets.Add(output.Position);
                output.Write(Ascii($"{index + 1} 0 obj\n"));
                output.Write(objects[index]);
                output.Write(Ascii("\nendobj\n"));
            }
            var xref = output.Position;
            output.Write(Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
            foreach (var offset in offsets.Skip(1))
                output.Write(Ascii($"{offset:0000000000} 00000 n \n"));
            output.Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root {catalogId} 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF"));
            return output.ToArray();
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            var result = new byte[arrays.Sum(array => array.Length)];
            var offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }
    }

    private sealed class SimplePdfPage
    {
        private readonly StringBuilder content = new();
        public string Content => content.ToString();
        public void Text(double x, double y, string value, double size, bool bold, string color) =>
            content.AppendLine($"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {Rgb(color)} rg 1 0 0 1 {x:0.##} {y:0.##} Tm ({Escape(value)}) Tj ET");
        public void TextRight(double right, double y, string value, double size, bool bold, string color)
        {
            var approximateWidth = Normalize(value).Length * size * (bold ? .54 : .49);
            Text(Math.Max(0, right - approximateWidth), y, value, size, bold, color);
        }
        public void FillRect(double x, double y, double w, double h, string color) =>
            content.AppendLine($"{Rgb(color)} rg {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f");
        public void StrokeRect(double x, double y, double w, double h, string color, double lineWidth) =>
            content.AppendLine($"{Rgb(color)} RG {lineWidth:0.##} w {x:0.##} {y:0.##} {w:0.##} {h:0.##} re S");
        public void Line(double x1, double y1, double x2, double y2, string color, double lineWidth) =>
            content.AppendLine($"{Rgb(color)} RG {lineWidth:0.##} w {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S");
    }

    private static string Rgb(string hex)
    {
        var r = int.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var g = int.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        var b = int.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return FormattableString.Invariant($"{r:0.###} {g:0.###} {b:0.###}");
    }

    private static string Escape(string value) => Normalize(value)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            if (character is >= ' ' and <= '~') builder.Append(character);
            else if (character is '\u2013' or '\u2014') builder.Append('-');
        }
        return builder.ToString();
    }

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);
}
