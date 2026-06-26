using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Reports;

public sealed class ReportService(ProjectTrackerDbContext db)
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfContentType = "application/pdf";
    private const string BrandName = "SON-AERO";
    private const string BrandSubtitle = "Aerospace Program Control";

    public async Task<ReportFile> PortfolioExcelAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .Include(project => project.Tasks)
            .OrderBy(project => project.TargetDelivery)
            .ThenBy(project => project.ProgramName)
            .ToListAsync(cancellationToken);

        var rows = projects.Select(project => new[]
        {
            project.ProgramName,
            project.ProgramManager ?? string.Empty,
            project.CurrentTask ?? string.Empty,
            Percent(project.Progress),
            Date(project.TargetDelivery),
            project.Status.ToString(),
            project.Tasks.Count.ToString(),
            project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind).ToString()
        }).ToList<IReadOnlyList<string>>();

        var content = SimpleXlsx.Build("Portfolio Summary",
            ["Program", "Manager", "Current Task", "Progress", "Target Delivery", "Status", "Tasks", "Behind Tasks"],
            rows);

        return new ReportFile(content, XlsxContentType, $"portfolio-summary-{DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}.xlsx");
    }

    public async Task<ReportFile> ProjectExcelAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .Include(project => project.Tasks)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project not found.");

        var rows = project.Tasks.OrderBy(task => task.Sequence).Select(task => new[]
        {
            task.ExternalTaskId ?? string.Empty,
            task.Title,
            task.Phase ?? string.Empty,
            Date(task.StartDate),
            Date(task.EndDate),
            Date(task.OriginalStartDate),
            Date(task.OriginalEndDate),
            task.EstimatedDuration?.ToString() ?? string.Empty,
            Percent(task.PercentComplete),
            task.Status.ToString(),
            task.Notes ?? string.Empty
        }).ToList<IReadOnlyList<string>>();

        var content = SimpleXlsx.Build(project.ProgramName,
            ["ID", "Task", "Phase", "Start", "End", "Orig Start", "Orig End", "Duration", "Complete", "Status", "Notes"],
            rows);

        return new ReportFile(content, XlsxContentType, $"{SafeName(project.ProgramName)}-schedule.xlsx");
    }

    public async Task<ReportFile> PortfolioPdfAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .Include(project => project.Tasks)
            .OrderBy(project => project.TargetDelivery)
            .ThenBy(project => project.ProgramName)
            .ToListAsync(cancellationToken);

        var rows = projects.Select(project => new[]
        {
            project.ProgramName,
            project.Status.ToString(),
            Percent(project.Progress),
            Date(project.TargetDelivery),
            project.CurrentTask ?? string.Empty,
            project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind).ToString()
        }).ToList<IReadOnlyList<string>>();

        var metrics = new[]
        {
            new ReportMetric("Active Programs", projects.Count.ToString()),
            new ReportMetric("Behind Tasks", projects.Sum(project => project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind)).ToString(), "risk"),
            new ReportMetric("Avg Progress", Percent(projects.Count == 0 ? 0m : projects.Average(project => project.Progress))),
            new ReportMetric("Generated", DateTime.Now.ToString("MMM d, yyyy"))
        };

        var content = SimplePdf.Build("Portfolio Summary", BrandSubtitle, metrics,
            ["Program", "Status", "Progress", "Target", "Current Operation", "Behind"],
            rows);

        return new ReportFile(content, PdfContentType, $"portfolio-summary-{DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}.pdf");
    }

    public async Task<ReportFile> ProjectPdfAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .Include(project => project.Tasks)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project not found.");

        var rows = project.Tasks.OrderBy(task => task.Sequence).Select(task => new[]
        {
            task.Sequence.ToString(),
            task.Title,
            task.WorkStation ?? "Unassigned",
            Date(task.StartDate),
            Date(task.EndDate),
            Percent(task.PercentComplete),
            task.Status.ToString()
        }).ToList<IReadOnlyList<string>>();

        var metrics = new[]
        {
            new ReportMetric("Status", project.Status.ToString(), project.Status == ProjectStatus.Behind ? "risk" : "neutral"),
            new ReportMetric("Progress", Percent(project.Progress)),
            new ReportMetric("Target", Date(project.TargetDelivery)),
            new ReportMetric("Operations", project.Tasks.Count.ToString())
        };

        var content = SimplePdf.Build($"{project.ProgramName} Schedule", BrandSubtitle, metrics,
            ["#", "Operation", "Station", "Start", "End", "Complete", "Status"],
            rows);

        return new ReportFile(content, PdfContentType, $"{SafeName(project.ProgramName)}-schedule.pdf");
    }

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? string.Empty;

    private static string Percent(decimal value) => $"{Math.Round(value * 100m, 0)}%";

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
    }

    private sealed record ReportMetric(string Label, string Value, string Tone = "neutral");

    private static class SimpleXlsx
    {
        public static byte[] Build(string title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "[Content_Types].xml", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                      <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                      <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                    </Types>
                    """);
                WriteEntry(zip, "_rels/.rels", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """);
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                      <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                    </Relationships>
                    """);
                WriteEntry(zip, "xl/workbook.xml", $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="{XmlEscape(title[..Math.Min(title.Length, 31)])}" sheetId="1" r:id="rId1"/></sheets>
                    </workbook>
                    """);
                WriteStyles(zip);
                WriteWorksheet(zip, title, headers, rows);
            }

            return output.ToArray();
        }

        private static void WriteWorksheet(ZipArchive zip, string title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            var lastColumn = ColumnName(headers.Count);
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var stream = entry.Open();
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true });
            writer.WriteStartDocument();
            writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            writer.WriteStartElement("sheetViews");
            writer.WriteStartElement("sheetView");
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane");
            writer.WriteAttributeString("ySplit", "4");
            writer.WriteAttributeString("topLeftCell", "A5");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("cols");
            for (var column = 1; column <= headers.Count; column++)
            {
                writer.WriteStartElement("col");
                writer.WriteAttributeString("min", column.ToString());
                writer.WriteAttributeString("max", column.ToString());
                writer.WriteAttributeString("width", ColumnWidth(headers[column - 1]).ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData");
            WriteRow(writer, 1, [BrandName], 1);
            WriteRow(writer, 2, [title], 2);
            WriteRow(writer, 3, [$"Generated {DateTime.Now:MMM d, yyyy h:mm tt}"], 2);
            WriteRow(writer, 4, headers, 3);
            for (var i = 0; i < rows.Count; i++)
            {
                WriteRow(writer, i + 5, rows[i], RowStyle(rows[i], i));
            }
            writer.WriteEndElement();
            writer.WriteStartElement("autoFilter");
            writer.WriteAttributeString("ref", $"A4:{lastColumn}{rows.Count + 4}");
            writer.WriteEndElement();
            writer.WriteStartElement("mergeCells");
            writer.WriteAttributeString("count", "3");
            WriteMergeCell(writer, $"A1:{lastColumn}1");
            WriteMergeCell(writer, $"A2:{lastColumn}2");
            WriteMergeCell(writer, $"A3:{lastColumn}3");
            writer.WriteEndElement();
            writer.WriteStartElement("pageMargins");
            writer.WriteAttributeString("left", "0.4");
            writer.WriteAttributeString("right", "0.4");
            writer.WriteAttributeString("top", "0.6");
            writer.WriteAttributeString("bottom", "0.6");
            writer.WriteAttributeString("header", "0.2");
            writer.WriteAttributeString("footer", "0.2");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        private static void WriteRow(XmlWriter writer, int rowIndex, IReadOnlyList<string> values, int styleIndex)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", rowIndex.ToString());
            if (rowIndex <= 4)
            {
                writer.WriteAttributeString("ht", rowIndex == 1 ? "24" : "18");
                writer.WriteAttributeString("customHeight", "1");
            }
            for (var column = 0; column < values.Count; column++)
            {
                writer.WriteStartElement("c");
                writer.WriteAttributeString("r", $"{ColumnName(column + 1)}{rowIndex}");
                writer.WriteAttributeString("s", styleIndex.ToString());
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteElementString("t", values[column]);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static void WriteMergeCell(XmlWriter writer, string reference)
        {
            writer.WriteStartElement("mergeCell");
            writer.WriteAttributeString("ref", reference);
            writer.WriteEndElement();
        }

        private static int RowStyle(IReadOnlyList<string> values, int rowIndex)
        {
            if (values.Any(value => value.Equals("Behind", StringComparison.OrdinalIgnoreCase) || value.Contains("overdue", StringComparison.OrdinalIgnoreCase)))
            {
                return 6;
            }

            if (values.Any(value => value.Equals("Complete", StringComparison.OrdinalIgnoreCase)))
            {
                return 7;
            }

            return rowIndex % 2 == 0 ? 4 : 5;
        }

        private static double ColumnWidth(string header)
        {
            if (header.Contains("Task", StringComparison.OrdinalIgnoreCase) || header.Contains("Operation", StringComparison.OrdinalIgnoreCase) || header.Contains("Current", StringComparison.OrdinalIgnoreCase))
            {
                return 28;
            }

            if (header.Contains("Notes", StringComparison.OrdinalIgnoreCase))
            {
                return 34;
            }

            if (header.Contains("Date", StringComparison.OrdinalIgnoreCase) || header.Contains("Start", StringComparison.OrdinalIgnoreCase) || header.Contains("End", StringComparison.OrdinalIgnoreCase) || header.Contains("Target", StringComparison.OrdinalIgnoreCase))
            {
                return 14;
            }

            return 16;
        }

        private static void WriteStyles(ZipArchive zip)
        {
            WriteEntry(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="4">
                    <font><sz val="10"/><color rgb="FF162033"/><name val="Aptos"/></font>
                    <font><b/><sz val="18"/><color rgb="FFFFFFFF"/><name val="Aptos Display"/></font>
                    <font><b/><sz val="10"/><color rgb="FF6D7A8B"/><name val="Aptos"/></font>
                    <font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="Aptos"/></font>
                  </fonts>
                  <fills count="8">
                    <fill><patternFill patternType="none"/></fill>
                    <fill><patternFill patternType="gray125"/></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FF111827"/><bgColor indexed="64"/></patternFill></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FFF6F8FB"/><bgColor indexed="64"/></patternFill></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FFE9F1F8"/><bgColor indexed="64"/></patternFill></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FFFFFFFF"/><bgColor indexed="64"/></patternFill></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FFFFEAE6"/><bgColor indexed="64"/></patternFill></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FFEAF7EF"/><bgColor indexed="64"/></patternFill></fill>
                  </fills>
                  <borders count="2">
                    <border><left/><right/><top/><bottom/><diagonal/></border>
                    <border><left style="thin"><color rgb="FFD6DEE8"/></left><right style="thin"><color rgb="FFD6DEE8"/></right><top style="thin"><color rgb="FFD6DEE8"/></top><bottom style="thin"><color rgb="FFD6DEE8"/></bottom><diagonal/></border>
                  </borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="8">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                    <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
                    <xf numFmtId="0" fontId="2" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
                    <xf numFmtId="0" fontId="3" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
                    <xf numFmtId="0" fontId="0" fillId="5" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                    <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                    <xf numFmtId="0" fontId="0" fillId="6" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                    <xf numFmtId="0" fontId="0" fillId="7" borderId="1" xfId="0" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
                  </cellXfs>
                  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """);
        }

        private static string ColumnName(int index)
        {
            var column = string.Empty;
            while (index > 0)
            {
                index--;
                column = (char)('A' + index % 26) + column;
                index /= 26;
            }
            return column;
        }

        private static void WriteEntry(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content.Trim());
        }

        private static string XmlEscape(string value) => SecurityElementEscape(value);
    }

    private static class SimplePdf
    {
        public static byte[] Build(string title, string subtitle, IReadOnlyList<ReportMetric> metrics, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            var logoBytes = LoadLogoBytes();
            var hasLogo = logoBytes.Length > 0;
            var content = new StringBuilder();
            DrawRect(content, 0, 708, 612, 84, "111827");
            DrawRect(content, 0, 704, 612, 4, "EF352A");
            if (hasLogo)
            {
                DrawImage(content, "Logo", 32, 730, 174, 48);
            }
            else
            {
                DrawText(content, BrandName, 40, 758, 20, "F2", "FFFFFF");
                DrawText(content, subtitle.ToUpperInvariant(), 40, 739, 8, "F1", "CBD5E1");
            }
            DrawText(content, title, 40, 676, 21, "F2", "111827");
            DrawText(content, $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}", 40, 658, 9, "F1", "64748B");

            var metricWidth = 126.0;
            for (var i = 0; i < Math.Min(metrics.Count, 4); i++)
            {
                var metric = metrics[i];
                var x = 40 + (i * (metricWidth + 10));
                DrawRect(content, x, 600, metricWidth, 42, metric.Tone == "risk" ? "FFEAE6" : "F6F8FB");
                DrawText(content, metric.Label.ToUpperInvariant(), x + 10, 626, 7, "F2", "64748B");
                DrawText(content, metric.Value, x + 10, 609, 13, "F2", metric.Tone == "risk" ? "B42318" : "111827");
            }

            var top = 562.0;
            var left = 40.0;
            var tableWidth = 532.0;
            var colWidth = tableWidth / headers.Count;
            DrawRect(content, left, top, tableWidth, 22, "E9F1F8");
            for (var i = 0; i < headers.Count; i++)
            {
                DrawText(content, Truncate(headers[i], 14), left + 7 + (i * colWidth), top + 7, 7, "F2", "334155");
            }

            var y = top - 24;
            foreach (var row in rows.Take(18).Select((value, index) => new { value, index }))
            {
                var isRisk = row.value.Any(value => value.Equals("Behind", StringComparison.OrdinalIgnoreCase));
                DrawRect(content, left, y, tableWidth, 22, isRisk ? "FFEAE6" : row.index % 2 == 0 ? "FFFFFF" : "F8FAFC");
                for (var i = 0; i < Math.Min(headers.Count, row.value.Count); i++)
                {
                    DrawText(content, Truncate(row.value[i], i == 1 || headers[i].Contains("Current", StringComparison.OrdinalIgnoreCase) ? 22 : 12), left + 7 + (i * colWidth), y + 7, 7, "F1", isRisk ? "9F1B13" : "162033");
                }
                y -= 22;
            }

            if (rows.Count > 18)
            {
                DrawText(content, $"+ {rows.Count - 18} more rows included in the XLSX export", left, y + 4, 8, "F1", "64748B");
            }

            DrawText(content, "Internal program control report", 40, 34, 8, "F1", "64748B");

            var contentObjectId = hasLogo ? 7 : 6;
            var resources = hasLogo
                ? $"<< /Font << /F1 4 0 R /F2 5 0 R >> /XObject << /Logo 6 0 R >> >>"
                : "<< /Font << /F1 4 0 R /F2 5 0 R >> >>";
            var objects = new List<byte[]>
            {
                PdfObject("<< /Type /Catalog /Pages 2 0 R >>"),
                PdfObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                PdfObject($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources {resources} /Contents {contentObjectId} 0 R >>"),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
                PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")
            };
            if (hasLogo)
            {
                objects.Add(PdfStreamObject("<< /Type /XObject /Subtype /Image /Width 225 /Height 62 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", logoBytes));
            }
            objects.Add(PdfStreamObject("<<", Encoding.ASCII.GetBytes(content.ToString())));

            using var output = new MemoryStream();
            WriteAscii(output, "%PDF-1.4\n");
            var offsets = new List<long> { 0 };
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                WriteAscii(output, $"{i + 1} 0 obj\n");
                output.Write(objects[i], 0, objects[i].Length);
                WriteAscii(output, "\nendobj\n");
            }

            var xrefOffset = output.Position;
            WriteAscii(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets.Skip(1))
            {
                WriteAscii(output, $"{offset:0000000000} 00000 n \n");
            }
            WriteAscii(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
            return output.ToArray();
        }

        private static byte[] LoadLogoBytes()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "son-aero-report-logo.jpg");
            return File.Exists(path) ? File.ReadAllBytes(path) : [];
        }

        private static void DrawImage(StringBuilder content, string name, double x, double y, double width, double height)
        {
            content.AppendLine("q");
            content.AppendLine($"{Number(width)} 0 0 {Number(height)} {Number(x)} {Number(y)} cm");
            content.AppendLine($"/{name} Do");
            content.AppendLine("Q");
        }

        private static void DrawRect(StringBuilder content, double x, double y, double width, double height, string hex)
        {
            var (r, g, b) = Rgb(hex);
            content.AppendLine($"{r} {g} {b} rg");
            content.AppendLine($"{Number(x)} {Number(y)} {Number(width)} {Number(height)} re f");
        }

        private static void DrawText(StringBuilder content, string text, double x, double y, int size, string font, string hex)
        {
            var (r, g, b) = Rgb(hex);
            content.AppendLine("BT");
            content.AppendLine($"{r} {g} {b} rg");
            content.AppendLine($"/{font} {size} Tf");
            content.AppendLine($"{Number(x)} {Number(y)} Td");
            content.Append('(').Append(EscapePdf(text)).AppendLine(") Tj");
            content.AppendLine("ET");
        }

        private static string Truncate(string value, int max)
        {
            if (value.Length <= max) return value;
            return value[..Math.Max(0, max - 3)] + "...";
        }

        private static (string R, string G, string B) Rgb(string hex)
        {
            var r = Convert.ToInt32(hex[..2], 16) / 255m;
            var g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255m;
            var b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255m;
            return (Number(r), Number(g), Number(b));
        }

        private static string Number(decimal value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static byte[] PdfObject(string content) => Encoding.ASCII.GetBytes(content);

        private static byte[] PdfStreamObject(string dictionaryStart, byte[] bytes)
        {
            using var output = new MemoryStream();
            WriteAscii(output, $"{dictionaryStart} /Length {bytes.Length} >>\nstream\n");
            output.Write(bytes, 0, bytes.Length);
            WriteAscii(output, "\nendstream");
            return output.ToArray();
        }

        private static void WriteAscii(Stream stream, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string EscapePdf(string value)
        {
            return value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }
    }

    private static string SecurityElementEscape(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }
}
