using ClosedXML.Excel;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Reports;

internal static class ExcelReportBuilder
{
    private static readonly XLColor Navy = XLColor.FromHtml("#101822");
    private static readonly XLColor Ink = XLColor.FromHtml("#101822");
    private static readonly XLColor Ink2 = XLColor.FromHtml("#3A4654");
    private static readonly XLColor Muted = XLColor.FromHtml("#67727F");
    private static readonly XLColor Line = XLColor.FromHtml("#D0D7DF");
    private static readonly XLColor Surface2 = XLColor.FromHtml("#F5F7F9");
    private static readonly XLColor Surface3 = XLColor.FromHtml("#ECEFF3");
    private static readonly XLColor Red = XLColor.FromHtml("#E23B2C");
    private static readonly XLColor RedTint = XLColor.FromHtml("#FCEBE8");
    private static readonly XLColor Steel = XLColor.FromHtml("#2F6195");
    private static readonly XLColor SteelTint = XLColor.FromHtml("#E8EFF6");
    private static readonly XLColor Green = XLColor.FromHtml("#1F7A4D");
    private static readonly XLColor GreenTint = XLColor.FromHtml("#E5F2EA");
    private static readonly XLColor Done = XLColor.FromHtml("#46586B");
    private static readonly XLColor DoneTint = XLColor.FromHtml("#E9EDF1");
    private static readonly XLColor Idle = XLColor.FromHtml("#7D8893");
    private static readonly XLColor IdleTint = XLColor.FromHtml("#EDF0F3");

    public static byte[] BuildProject(Project project, ScheduleCalendar calendar, string? logoPath)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = $"{project.ProgramName} Project Schedule";
        workbook.Properties.Subject = "SON-AERO internal project control report";
        workbook.Properties.Company = "SON-AERO";

        BuildProjectSummary(workbook.Worksheets.Add("Project Summary"), project, calendar, logoPath);
        BuildProjectTimeline(workbook.Worksheets.Add("Gantt Timeline"), project, calendar, logoPath);
        return Save(workbook);
    }

    public static byte[] BuildPortfolio(IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "SON-AERO Portfolio Summary";
        workbook.Properties.Subject = "SON-AERO internal project control report";
        workbook.Properties.Company = "SON-AERO";

        BuildPortfolioSummary(workbook.Worksheets.Add("Portfolio Summary"), projects, calendar, logoPath);
        BuildPortfolioTimeline(workbook.Worksheets.Add("Portfolio Timeline"), projects, calendar, logoPath);
        return Save(workbook);
    }

    public static byte[] BuildPastProjects(IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "SON-AERO Past Projects";
        workbook.Properties.Subject = "SON-AERO completed project performance report";
        workbook.Properties.Company = "SON-AERO";

        BuildPastProjectsSummary(workbook.Worksheets.Add("Past Projects"), projects, calendar, logoPath);
        BuildPortfolioTimeline(workbook.Worksheets.Add("Completion Timeline"), projects, calendar, logoPath);
        return Save(workbook);
    }

    private static void BuildProjectSummary(IXLWorksheet sheet, Project project, ScheduleCalendar calendar, string? logoPath)
    {
        ConfigureSheet(sheet, XLPageOrientation.Landscape);
        AddBrandHeader(sheet, 10, logoPath, "PROJECT SCHEDULE");

        sheet.Range("A6:J6").Merge().Value = project.ProgramName;
        StyleTitle(sheet.Range("A6:J6"), 22);
        sheet.Range("A7:J7").Merge().Value = $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  |  {WorkWeekLabel(calendar)}";
        StyleSubtitle(sheet.Range("A7:J7"));

        AddDetail(sheet, "A9", "B9:C9", "Customer", project.CustomerName ?? "Not set");
        AddDetail(sheet, "D9", "E9:F9", "Sales Order", project.SalesOrderNumber ?? "Not set");
        AddDetail(sheet, "G9", "H9:J9", "Program Manager", project.ProgramManager ?? "Not set");
        AddDetail(sheet, "A10", "B10:F10", "Current Operation", project.CurrentTask ?? "Not set");
        AddDetail(sheet, "G10", "H10:J10", "Schedule Window", $"{ReportText.Date(project.ProgramStart)} - {ReportText.Date(project.TargetDelivery)}");

        AddMetric(sheet, "A12:B13", "Status", ReportText.Status(project.Status), StatusColor(project.Status), StatusTint(project.Status));
        AddMetric(sheet, "C12:D13", "Completion", ReportText.Percent(project.Progress), Steel, SteelTint);
        AddMetric(sheet, "E12:F13", "Target Delivery", ReportText.Date(project.TargetDelivery), project.Status == ProjectStatus.Behind ? Red : Ink2, project.Status == ProjectStatus.Behind ? RedTint : Surface2);
        AddMetric(sheet, "G12:H13", "Operations", project.Tasks.Count.ToString(), Ink2, Surface2);
        AddMetric(sheet, "I12:J13", "Behind", project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind).ToString(), Red, RedTint);

        var headers = new[] { "Step", "Operation", "Work Center", "Phase", "Start", "End", "Duration", "Complete", "Status", "Notes" };
        const int headerRow = 15;
        WriteTableHeader(sheet, headerRow, headers);
        var tasks = project.Tasks.OrderBy(task => task.Sequence).ToList();
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var row = headerRow + 1 + index;
            sheet.Cell(row, 1).Value = task.Sequence;
            sheet.Cell(row, 2).Value = task.Title;
            sheet.Cell(row, 3).Value = task.WorkStation ?? "Unassigned";
            SetOptionalText(sheet.Cell(row, 4), task.Phase);
            SetDate(sheet.Cell(row, 5), task.StartDate);
            SetDate(sheet.Cell(row, 6), task.EndDate);
            if (task.EstimatedDuration is not null) sheet.Cell(row, 7).Value = task.EstimatedDuration.Value;
            sheet.Cell(row, 8).Value = task.PercentComplete;
            sheet.Cell(row, 8).Style.NumberFormat.Format = "0%";
            sheet.Cell(row, 9).Value = ReportText.Status(task.Status);
            SetOptionalText(sheet.Cell(row, 10), task.Notes);

            var range = sheet.Range(row, 1, row, headers.Length);
            StyleDataRow(range, index);
            range.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            range.Style.Border.BottomBorderColor = Line;
            sheet.Cell(row, 9).Style.Fill.BackgroundColor = StatusTint(task.Status);
            sheet.Cell(row, 9).Style.Font.FontColor = StatusColor(task.Status);
            sheet.Cell(row, 9).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            sheet.Cell(row, 1).Style.Border.LeftBorderColor = StatusColor(task.Status);
            sheet.Row(row).Height = 23;
        }

        var lastRow = Math.Max(headerRow + 1, headerRow + tasks.Count);
        sheet.Range(headerRow, 1, lastRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = 31;
        sheet.Column(3).Width = 19;
        sheet.Column(4).Width = 17;
        sheet.Columns(5, 6).Width = 14;
        sheet.Column(7).Width = 10;
        sheet.Column(8).Width = 11;
        sheet.Column(9).Width = 14;
        sheet.Column(10).Width = 34;
        sheet.Range(headerRow + 1, 1, lastRow, headers.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(headerRow + 1, 2, lastRow, 4).Style.Alignment.WrapText = true;
        sheet.Range(headerRow + 1, 10, lastRow, 10).Style.Alignment.WrapText = true;
        sheet.PageSetup.PrintAreas.Add($"A1:J{lastRow}");
        sheet.PageSetup.SetRowsToRepeatAtTop(15, 15);
    }

    private static void BuildPortfolioSummary(IXLWorksheet sheet, IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        ConfigureSheet(sheet, XLPageOrientation.Landscape);
        AddBrandHeader(sheet, 10, logoPath, "PORTFOLIO CONTROL");
        sheet.Range("A6:J6").Merge().Value = "Development Portfolio";
        StyleTitle(sheet.Range("A6:J6"), 22);
        sheet.Range("A7:J7").Merge().Value = $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  |  {WorkWeekLabel(calendar)}";
        StyleSubtitle(sheet.Range("A7:J7"));

        var active = projects.Count(project => project.Status != ProjectStatus.Complete);
        var behind = projects.Count(project => project.Status == ProjectStatus.Behind);
        var average = projects.Count == 0 ? 0m : projects.Average(project => project.Progress);
        var nearest = projects.Where(project => project.Status != ProjectStatus.Complete && project.TargetDelivery is not null)
            .Select(project => project.TargetDelivery)
            .Min();
        AddMetric(sheet, "A9:B10", "Active Projects", active.ToString(), Ink2, Surface2);
        AddMetric(sheet, "C9:D10", "Behind Schedule", behind.ToString(), Red, RedTint);
        AddMetric(sheet, "E9:F10", "Average Completion", ReportText.Percent(average), Steel, SteelTint);
        AddMetric(sheet, "G9:H10", "Nearest Delivery", ReportText.Date(nearest), Ink2, Surface2);
        AddMetric(sheet, "I9:J10", "Total Operations", projects.Sum(project => project.Tasks.Count).ToString(), Ink2, Surface2);

        var headers = new[] { "Part No.", "Customer", "Sales Order", "Manager", "Current Operation", "Progress", "Target", "Status", "Operations", "Behind" };
        const int headerRow = 12;
        WriteTableHeader(sheet, headerRow, headers);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var row = headerRow + 1 + index;
            sheet.Cell(row, 1).Value = project.ProgramName;
            SetOptionalText(sheet.Cell(row, 2), project.CustomerName);
            SetOptionalText(sheet.Cell(row, 3), project.SalesOrderNumber);
            SetOptionalText(sheet.Cell(row, 4), project.ProgramManager);
            SetOptionalText(sheet.Cell(row, 5), project.CurrentTask);
            sheet.Cell(row, 6).Value = project.Progress;
            sheet.Cell(row, 6).Style.NumberFormat.Format = "0%";
            SetDate(sheet.Cell(row, 7), project.TargetDelivery);
            sheet.Cell(row, 8).Value = ReportText.Status(project.Status);
            sheet.Cell(row, 9).Value = project.Tasks.Count;
            sheet.Cell(row, 10).Value = project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind);
            var range = sheet.Range(row, 1, row, headers.Length);
            StyleDataRow(range, index);
            range.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            range.Style.Border.BottomBorderColor = Line;
            sheet.Cell(row, 8).Style.Fill.BackgroundColor = StatusTint(project.Status);
            sheet.Cell(row, 8).Style.Font.FontColor = StatusColor(project.Status);
            sheet.Cell(row, 8).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            sheet.Cell(row, 1).Style.Border.LeftBorderColor = StatusColor(project.Status);
            sheet.Row(row).Height = 25;
        }

        var lastRow = Math.Max(headerRow + 1, headerRow + projects.Count);
        sheet.Range(headerRow, 1, lastRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Column(1).Width = 22;
        sheet.Column(2).Width = 20;
        sheet.Column(3).Width = 17;
        sheet.Column(4).Width = 18;
        sheet.Column(5).Width = 30;
        sheet.Column(6).Width = 12;
        sheet.Column(7).Width = 15;
        sheet.Column(8).Width = 15;
        sheet.Columns(9, 10).Width = 11;
        sheet.Range(headerRow + 1, 1, lastRow, headers.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(headerRow + 1, 1, lastRow, 5).Style.Alignment.WrapText = true;
        sheet.PageSetup.PrintAreas.Add($"A1:J{lastRow}");
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
    }

    private static void BuildPastProjectsSummary(IXLWorksheet sheet, IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        ConfigureSheet(sheet, XLPageOrientation.Landscape);
        AddBrandHeader(sheet, 10, logoPath, "PAST PROJECTS");
        sheet.Range("A6:I6").Merge().Value = "Completed Project Performance";
        StyleTitle(sheet.Range("A6:I6"), 22);
        sheet.Range("A7:I7").Merge().Value = $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  |  {WorkWeekLabel(calendar)}";
        StyleSubtitle(sheet.Range("A7:I7"));

        var dated = projects.Where(project => project.TargetDelivery is not null && FinalCompletionDate(project) is not null).ToList();
        var onTime = dated.Count(project => FinalCompletionDate(project) <= project.TargetDelivery);
        var late = dated.Count - onTime;
        var onTimePercent = dated.Count == 0 ? 0m : (decimal)onTime / dated.Count;
        var average = projects.Count == 0 ? 0m : projects.Average(project => project.Progress);
        AddMetric(sheet, "A9:B10", "Completed Projects", projects.Count.ToString(), Ink2, Surface2);
        AddMetric(sheet, "C9:D10", "On Time Percentage", ReportText.Percent(onTimePercent), Green, GreenTint);
        AddMetric(sheet, "E9:F10", "Late Projects", late.ToString(), Red, RedTint);
        AddMetric(sheet, "G9:I10", "Average Completion", ReportText.Percent(average), Steel, SteelTint);

        var headers = new[] { "Part No.", "Customer", "Sales Order", "Manager", "Target", "Final Completion", "Result", "Operations", "Progress" };
        const int headerRow = 12;
        WriteTableHeader(sheet, headerRow, headers);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var finalCompletion = FinalCompletionDate(project);
            var isLate = project.TargetDelivery is not null && finalCompletion is not null && finalCompletion > project.TargetDelivery;
            var row = headerRow + 1 + index;
            sheet.Cell(row, 1).Value = project.ProgramName;
            SetOptionalText(sheet.Cell(row, 2), project.CustomerName);
            SetOptionalText(sheet.Cell(row, 3), project.SalesOrderNumber);
            SetOptionalText(sheet.Cell(row, 4), project.ProgramManager);
            SetDate(sheet.Cell(row, 5), project.TargetDelivery);
            SetDate(sheet.Cell(row, 6), finalCompletion);
            sheet.Cell(row, 7).Value = isLate ? "Late" : "On Time";
            sheet.Cell(row, 8).Value = project.Tasks.Count;
            sheet.Cell(row, 9).Value = project.Progress;
            sheet.Cell(row, 9).Style.NumberFormat.Format = "0%";
            var range = sheet.Range(row, 1, row, headers.Length);
            StyleDataRow(range, index);
            range.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            range.Style.Border.BottomBorderColor = Line;
            sheet.Cell(row, 7).Style.Fill.BackgroundColor = isLate ? RedTint : GreenTint;
            sheet.Cell(row, 7).Style.Font.FontColor = isLate ? Red : Green;
            sheet.Cell(row, 7).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            sheet.Cell(row, 1).Style.Border.LeftBorderColor = isLate ? Red : Green;
            sheet.Row(row).Height = 25;
        }

        var lastRow = Math.Max(headerRow + 1, headerRow + projects.Count);
        sheet.Range(headerRow, 1, lastRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, 4).Width = 20;
        sheet.Columns(5, 6).Width = 17;
        sheet.Column(7).Width = 14;
        sheet.Column(8).Width = 12;
        sheet.Column(9).Width = 12;
        sheet.Range(headerRow + 1, 1, lastRow, headers.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(headerRow + 1, 1, lastRow, 4).Style.Alignment.WrapText = true;
        sheet.PageSetup.PrintAreas.Add($"A1:I{lastRow}");
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
    }

    private static void BuildProjectTimeline(IXLWorksheet sheet, Project project, ScheduleCalendar calendar, string? logoPath)
    {
        var tasks = project.Tasks.OrderBy(task => task.Sequence).ToList();
        var bounds = tasks.Select(task => NormalizeRange(task.StartDate, task.EndDate)).Where(range => range is not null).Select(range => range!.Value).ToList();
        var start = bounds.Count > 0 ? bounds.Min(range => range.Start) : DateOnly.FromDateTime(DateTime.Today);
        var end = bounds.Count > 0 ? bounds.Max(range => range.End) : start.AddDays(30);
        var buckets = BuildBuckets(start.AddDays(-2), end.AddDays(3));
        BuildTimelineSheet(sheet, $"{project.ProgramName} Timeline", "OPERATION GANTT", buckets, tasks.Count, logoPath, calendar,
            (row, itemIndex) =>
            {
                var task = tasks[itemIndex];
                sheet.Cell(row, 1).Value = task.Sequence;
                sheet.Cell(row, 2).Value = task.Title;
                sheet.Cell(row, 3).Value = task.WorkStation ?? "Unassigned";
                SetDate(sheet.Cell(row, 4), task.StartDate);
                SetDate(sheet.Cell(row, 5), task.EndDate);
                sheet.Cell(row, 6).Value = task.PercentComplete;
                sheet.Cell(row, 6).Style.NumberFormat.Format = "0%";
                return new TimelineBar(NormalizeRange(task.StartDate, task.EndDate), task.PercentComplete, StatusColor(task.Status), StatusTint(task.Status));
            });
    }

    private static void BuildPortfolioTimeline(IXLWorksheet sheet, IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        var ordered = projects.OrderBy(project => project.TargetDelivery).ThenBy(project => project.ProgramName).ToList();
        var bounds = ordered.Select(project => NormalizeRange(project.ProgramStart, project.TargetDelivery)).Where(range => range is not null).Select(range => range!.Value).ToList();
        var start = bounds.Count > 0 ? bounds.Min(range => range.Start) : DateOnly.FromDateTime(DateTime.Today);
        var end = bounds.Count > 0 ? bounds.Max(range => range.End) : start.AddMonths(3);
        var buckets = BuildBuckets(start.AddDays(-7), end.AddDays(14), preferWeekly: true);
        BuildTimelineSheet(sheet, "Portfolio Delivery Timeline", "PORTFOLIO GANTT", buckets, ordered.Count, logoPath, calendar,
            (row, itemIndex) =>
            {
                var project = ordered[itemIndex];
                sheet.Cell(row, 1).Value = itemIndex + 1;
                sheet.Cell(row, 2).Value = project.ProgramName;
                sheet.Cell(row, 3).Value = project.CustomerName ?? "Not set";
                SetDate(sheet.Cell(row, 4), project.ProgramStart);
                SetDate(sheet.Cell(row, 5), project.TargetDelivery);
                sheet.Cell(row, 6).Value = project.Progress;
                sheet.Cell(row, 6).Style.NumberFormat.Format = "0%";
                return new TimelineBar(NormalizeRange(project.ProgramStart, project.TargetDelivery), project.Progress, StatusColor(project.Status), StatusTint(project.Status));
            });
    }

    private static void BuildTimelineSheet(
        IXLWorksheet sheet,
        string title,
        string eyebrow,
        IReadOnlyList<TimelineBucket> buckets,
        int itemCount,
        string? logoPath,
        ScheduleCalendar calendar,
        Func<int, int, TimelineBar> writeItem)
    {
        ConfigureSheet(sheet, XLPageOrientation.Landscape);
        var lastColumn = 6 + buckets.Count;
        AddBrandHeader(sheet, lastColumn, logoPath, eyebrow);
        sheet.Range(5, 1, 5, lastColumn).Merge().Value = title;
        StyleTitle(sheet.Range(5, 1, 5, lastColumn), 19);
        sheet.Range(6, 1, 6, 6).Merge().Value = "Status: green on track | red behind | graphite complete | gray not started";
        StyleSubtitle(sheet.Range(6, 1, 6, 6));
        sheet.Range(6, 7, 6, lastColumn).Merge().Value = $"{buckets[0].Start:MMM d, yyyy} - {buckets[^1].End:MMM d, yyyy}  |  {WorkWeekLabel(calendar)}";
        sheet.Range(6, 7, 6, lastColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        StyleSubtitle(sheet.Range(6, 7, 6, lastColumn));

        var labels = new[] { "#", "Operation / Project", "Work Center / Customer", "Start", "End", "Complete" };
        for (var column = 1; column <= labels.Length; column++)
        {
            sheet.Cell(8, column).Value = labels[column - 1];
            sheet.Range(8, column, 9, column).Merge();
        }
        StyleTimelineHeader(sheet.Range(8, 1, 9, 6));

        var monthStart = 0;
        while (monthStart < buckets.Count)
        {
            var monthKey = (buckets[monthStart].Start.Year, buckets[monthStart].Start.Month);
            var monthEnd = monthStart;
            while (monthEnd + 1 < buckets.Count && (buckets[monthEnd + 1].Start.Year, buckets[monthEnd + 1].Start.Month) == monthKey) monthEnd++;
            var range = sheet.Range(8, 7 + monthStart, 8, 7 + monthEnd);
            var monthLabel = buckets[monthStart].Start.ToString(monthEnd == monthStart ? "MMM" : "MMM yyyy").ToUpperInvariant();
            range.Merge().Value = monthLabel;
            StyleTimelineHeader(range);
            monthStart = monthEnd + 1;
        }
        for (var index = 0; index < buckets.Count; index++)
        {
            var cell = sheet.Cell(9, 7 + index);
            cell.Value = buckets[index].Label;
            StyleTimelineHeader(cell.AsRange());
            cell.Style.Font.FontSize = 8;
            cell.Style.Alignment.TextRotation = buckets.Count > 70 ? 90 : 0;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
        {
            var row = 10 + itemIndex;
            var bar = writeItem(row, itemIndex);
            var labelRange = sheet.Range(row, 1, row, 6);
            StyleDataRow(labelRange, itemIndex);
            labelRange.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            labelRange.Style.Border.BottomBorderColor = Line;
            sheet.Row(row).Height = 22;
            for (var bucketIndex = 0; bucketIndex < buckets.Count; bucketIndex++)
            {
                var bucket = buckets[bucketIndex];
                var cell = sheet.Cell(row, 7 + bucketIndex);
                cell.Style.Fill.BackgroundColor = BucketBackground(bucket, calendar);
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
                cell.Style.Border.BottomBorderColor = Line;
            }

            if (bar.Range is not null)
            {
                var matching = buckets.Select((bucket, index) => new { bucket, index })
                    .Where(item => Overlaps(bar.Range.Value, item.bucket))
                    .Select(item => item.index)
                    .ToList();
                var completeCount = (int)Math.Ceiling(matching.Count * Math.Clamp(bar.Progress, 0m, 1m));
                for (var index = 0; index < matching.Count; index++)
                {
                    var cell = sheet.Cell(row, 7 + matching[index]);
                    cell.Style.Fill.BackgroundColor = index < completeCount ? bar.Color : bar.Tint;
                    cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.TopBorderColor = bar.Color;
                    cell.Style.Border.BottomBorderColor = bar.Color;
                }
            }
        }

        var lastRow = Math.Max(10, 9 + itemCount);
        var todayBucket = buckets.Select((bucket, index) => new { bucket, index }).FirstOrDefault(item => today >= item.bucket.Start && today <= item.bucket.End);
        if (todayBucket is not null)
        {
            var range = sheet.Range(8, 7 + todayBucket.index, lastRow, 7 + todayBucket.index);
            range.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            range.Style.Border.LeftBorderColor = Red;
        }

        sheet.Column(1).Width = 5;
        sheet.Column(2).Width = 30;
        sheet.Column(3).Width = 20;
        sheet.Columns(4, 5).Width = 13;
        sheet.Column(6).Width = 10;
        var bucketWidth = buckets.Count > 90 ? 2.4 : buckets.Count > 55 ? 3.2 : 4.5;
        sheet.Columns(7, lastColumn).Width = bucketWidth;
        sheet.Range(10, 2, lastRow, 3).Style.Alignment.WrapText = true;
        sheet.Range(10, 1, lastRow, lastColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.SheetView.FreezeRows(9);
        sheet.SheetView.FreezeColumns(6);
        sheet.SheetView.ZoomScale = buckets.Count > 70 ? 70 : 85;
        sheet.PageSetup.PrintAreas.Add($"A1:{sheet.Column(lastColumn).ColumnLetter()}{lastRow}");
        sheet.PageSetup.SetRowsToRepeatAtTop(8, 9);
    }

    private static IReadOnlyList<TimelineBucket> BuildBuckets(DateOnly start, DateOnly end, bool preferWeekly = false)
    {
        if (end < start) (start, end) = (end, start);
        var span = end.DayNumber - start.DayNumber + 1;
        if (!preferWeekly && span <= 60)
        {
            return Enumerable.Range(0, span)
                .Select(offset => new TimelineBucket(start.AddDays(offset), start.AddDays(offset), start.AddDays(offset).ToString("dd")))
                .ToList();
        }

        if (span <= 730)
        {
            var cursor = StartOfWeek(start);
            var buckets = new List<TimelineBucket>();
            while (cursor <= end)
            {
                buckets.Add(new TimelineBucket(cursor, cursor.AddDays(6), cursor.ToString("dd MMM")));
                cursor = cursor.AddDays(7);
            }
            return buckets;
        }

        var month = new DateOnly(start.Year, start.Month, 1);
        var monthly = new List<TimelineBucket>();
        while (month <= end)
        {
            monthly.Add(new TimelineBucket(month, month.AddMonths(1).AddDays(-1), month.ToString("MMM")));
            month = month.AddMonths(1);
        }
        return monthly;
    }

    private static void ConfigureSheet(IXLWorksheet sheet, XLPageOrientation orientation)
    {
        sheet.ShowGridLines = false;
        sheet.Style.Font.FontName = "Aptos";
        sheet.Style.Font.FontSize = 10;
        sheet.Style.Font.FontColor = Ink;
        sheet.PageSetup.PageOrientation = orientation;
        sheet.PageSetup.PaperSize = XLPaperSize.LetterPaper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.Margins.Left = 0.25;
        sheet.PageSetup.Margins.Right = 0.25;
        sheet.PageSetup.Margins.Top = 0.4;
        sheet.PageSetup.Margins.Bottom = 0.4;
    }

    private static void AddBrandHeader(IXLWorksheet sheet, int lastColumn, string? logoPath, string eyebrow)
    {
        var header = sheet.Range(1, 1, 4, lastColumn);
        header.Merge();
        header.Style.Fill.BackgroundColor = Navy;
        sheet.Row(1).Height = 20;
        sheet.Rows(2, 3).Height = 18;
        sheet.Row(4).Height = 8;
        sheet.Range(4, 1, 4, lastColumn).Style.Fill.BackgroundColor = Red;
        if (!string.IsNullOrWhiteSpace(logoPath))
        {
            sheet.AddPicture(logoPath).MoveTo(sheet.Cell("A1"), 10, 8).WithSize(174, 48);
        }
        else
        {
            sheet.Cell("A2").Value = "SON-AERO";
            sheet.Cell("A2").Style.Font.Bold = true;
            sheet.Cell("A2").Style.Font.FontSize = 20;
            sheet.Cell("A2").Style.Font.FontColor = XLColor.White;
        }
        var labelColumn = Math.Max(2, lastColumn - 4);
        var label = sheet.Range(2, labelColumn, 2, lastColumn);
        label.Merge().Value = eyebrow;
        label.Style.Font.FontColor = XLColor.FromHtml("#CBD5E1");
        label.Style.Font.FontSize = 9;
        label.Style.Font.Bold = true;
        label.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void AddDetail(IXLWorksheet sheet, string labelCell, string valueRange, string label, string value)
    {
        var labelTarget = sheet.Cell(labelCell);
        labelTarget.Value = label.ToUpperInvariant();
        labelTarget.Style.Font.FontSize = 8;
        labelTarget.Style.Font.Bold = true;
        labelTarget.Style.Font.FontColor = Muted;
        var range = sheet.Range(valueRange);
        range.Merge().Value = value;
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = Ink2;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void AddMetric(IXLWorksheet sheet, string address, string label, string value, XLColor accent, XLColor fill)
    {
        var range = sheet.Range(address);
        range.Merge();
        range.Style.Fill.BackgroundColor = fill;
        range.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
        range.Style.Border.LeftBorderColor = accent;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = Line;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        var cell = range.FirstCell();
        cell.Value = $"{label.ToUpperInvariant()}\n{value}";
        cell.Style.Alignment.WrapText = true;
        cell.Style.Font.FontColor = accent;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 11;
    }

    private static void WriteTableHeader(IXLWorksheet sheet, int row, IReadOnlyList<string> headers)
    {
        for (var column = 1; column <= headers.Count; column++) sheet.Cell(row, column).Value = headers[column - 1];
        var range = sheet.Range(row, 1, row, headers.Count);
        range.Style.Fill.BackgroundColor = SteelTint;
        range.Style.Font.FontColor = Ink2;
        range.Style.Font.FontSize = 9;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        range.Style.Border.BottomBorderColor = Steel;
        sheet.Row(row).Height = 24;
    }

    private static void StyleTitle(IXLRange range, double size)
    {
        range.Style.Font.FontName = "Arial";
        range.Style.Font.FontSize = size;
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = Ink;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void StyleSubtitle(IXLRange range)
    {
        range.Style.Font.FontSize = 9;
        range.Style.Font.FontColor = Muted;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void StyleTimelineHeader(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = SteelTint;
        range.Style.Font.FontColor = Ink2;
        range.Style.Font.FontSize = 9;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = Line;
    }

    private static void StyleDataRow(IXLRange range, int index)
    {
        range.Style.Fill.BackgroundColor = index % 2 == 0 ? XLColor.White : Surface2;
        range.Style.Font.FontColor = Ink2;
    }

    private static void SetDate(IXLCell cell, DateOnly? value)
    {
        if (value is null) return;
        cell.Value = value.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.NumberFormat.Format = "mmm d, yyyy";
    }

    private static void SetOptionalText(IXLCell cell, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) cell.Value = value;
    }

    private static string WorkWeekLabel(ScheduleCalendar calendar)
    {
        var days = calendar.WorkingDays.OrderBy(day => ((int)day + 6) % 7).Select(day => day.ToString()[..3]);
        return $"Work week: {string.Join(", ", days)}";
    }

    private static (DateOnly Start, DateOnly End)? NormalizeRange(DateOnly? start, DateOnly? end)
    {
        if (start is null && end is null) return null;
        var normalizedStart = start ?? end!.Value;
        var normalizedEnd = end ?? normalizedStart;
        return normalizedEnd < normalizedStart ? (normalizedStart, normalizedStart) : (normalizedStart, normalizedEnd);
    }

    private static DateOnly? FinalCompletionDate(Project project)
    {
        return project.Tasks
            .Select(task => task.EndDate)
            .Where(date => date is not null)
            .Max();
    }

    private static bool Overlaps((DateOnly Start, DateOnly End) range, TimelineBucket bucket) => range.Start <= bucket.End && range.End >= bucket.Start;

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    private static XLColor BucketBackground(TimelineBucket bucket, ScheduleCalendar calendar)
    {
        if (bucket.Start == bucket.End)
        {
            if (calendar.Holidays.Contains(bucket.Start)) return RedTint;
            if (!calendar.WorkingDays.Contains(bucket.Start.DayOfWeek)) return Surface3;
        }
        return XLColor.White;
    }

    private static XLColor StatusColor(ProjectStatus status) => status switch
    {
        ProjectStatus.Behind => Red,
        ProjectStatus.OnTrack => Green,
        ProjectStatus.Complete => Done,
        _ => Idle
    };

    private static XLColor StatusTint(ProjectStatus status) => status switch
    {
        ProjectStatus.Behind => RedTint,
        ProjectStatus.OnTrack => GreenTint,
        ProjectStatus.Complete => DoneTint,
        _ => IdleTint
    };

    private static XLColor StatusColor(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.Behind => Red,
        TaskScheduleStatus.OnTrack => Green,
        TaskScheduleStatus.Complete => Done,
        _ => Idle
    };

    private static XLColor StatusTint(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.Behind => RedTint,
        TaskScheduleStatus.OnTrack => GreenTint,
        TaskScheduleStatus.Complete => DoneTint,
        _ => IdleTint
    };

    private static byte[] Save(XLWorkbook workbook)
    {
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private sealed record TimelineBucket(DateOnly Start, DateOnly End, string Label);
    private sealed record TimelineBar((DateOnly Start, DateOnly End)? Range, decimal Progress, XLColor Color, XLColor Tint);
}
