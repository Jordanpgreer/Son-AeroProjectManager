using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Reports;

internal static class PdfReportBuilder
{
    private static readonly XColor Navy = Color("101822");
    private static readonly XColor Ink = Color("101822");
    private static readonly XColor Ink2 = Color("3A4654");
    private static readonly XColor Muted = Color("67727F");
    private static readonly XColor Line = Color("D0D7DF");
    private static readonly XColor Surface2 = Color("F5F7F9");
    private static readonly XColor Surface3 = Color("ECEFF3");
    private static readonly XColor Red = Color("E23B2C");
    private static readonly XColor RedTint = Color("FCEBE8");
    private static readonly XColor Steel = Color("2F6195");
    private static readonly XColor SteelTint = Color("E8EFF6");
    private static readonly XColor Green = Color("68A986");
    private static readonly XColor GreenTint = Color("EDF7F1");
    private static readonly XColor Done = Color("1F7A4D");
    private static readonly XColor DoneTint = Color("E5F2EA");
    private static readonly XColor Idle = Color("7D8893");
    private static readonly XColor IdleTint = Color("EDF0F3");

    private static readonly object FontLock = new();
    private static bool fontsInitialized;

    public static byte[] BuildProject(Project project, ScheduleCalendar calendar, string? logoPath, bool customerView = false)
    {
        EnsureFonts();
        using var document = CreateDocument($"{project.ProgramName} Project Schedule", customerView);
        using var logo = LoadLogo(logoPath);
        var tasks = project.Tasks.OrderBy(task => task.Sequence).ToList();
        var pageNumber = 1;

        // The customer sheet drops the metadata strip, so more operations fit above the footer.
        var firstPageRows = customerView ? 18 : 16;
        const int continuationRows = 20;
        const int ganttRows = 17;
        DrawProjectOverviewPage(document, project, tasks.Take(firstPageRows).ToList(), 0, logo, pageNumber++, customerView);
        for (var offset = firstPageRows; offset < tasks.Count; offset += continuationRows)
        {
            DrawProjectContinuationPage(document, project, tasks.Skip(offset).Take(continuationRows).ToList(), offset, logo, pageNumber++, customerView);
        }

        for (var offset = 0; offset < Math.Max(1, tasks.Count); offset += ganttRows)
        {
            DrawProjectGanttPage(document, project, tasks.Skip(offset).Take(ganttRows).ToList(), offset, calendar, logo, pageNumber++, customerView);
        }

        return Save(document);
    }

    public static byte[] BuildPortfolio(IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        EnsureFonts();
        using var document = CreateDocument("SON-AERO Portfolio Summary");
        using var logo = LoadLogo(logoPath);
        var ordered = projects.OrderBy(project => project.TargetDelivery).ThenBy(project => project.ProgramName).ToList();
        var pageNumber = 1;

        DrawPortfolioOverviewPage(document, ordered.Take(15).ToList(), ordered, calendar, logo, pageNumber++);
        for (var offset = 15; offset < ordered.Count; offset += 21)
        {
            DrawPortfolioContinuationPage(document, ordered.Skip(offset).Take(21).ToList(), offset, logo, pageNumber++);
        }
        for (var offset = 0; offset < Math.Max(1, ordered.Count); offset += 17)
        {
            DrawPortfolioGanttPage(document, ordered.Skip(offset).Take(17).ToList(), offset, calendar, logo, pageNumber++);
        }

        return Save(document);
    }

    public static byte[] BuildPastProjects(IReadOnlyList<Project> projects, ScheduleCalendar calendar, string? logoPath)
    {
        EnsureFonts();
        using var document = CreateDocument("SON-AERO Past Projects");
        using var logo = LoadLogo(logoPath);
        var ordered = projects.OrderBy(project => FinalCompletionDate(project)).ThenBy(project => project.ProgramName).ToList();
        var pageNumber = 1;

        DrawPastProjectsOverviewPage(document, ordered.Take(15).ToList(), ordered, calendar, logo, pageNumber++);
        for (var offset = 15; offset < ordered.Count; offset += 21)
        {
            DrawPastProjectsContinuationPage(document, ordered.Skip(offset).Take(21).ToList(), offset, logo, pageNumber++);
        }

        return Save(document);
    }

    public static byte[] BuildProjectActivity(
        Project project,
        IReadOnlyList<ProjectAuditEntry> entries,
        string? logoPath)
    {
        EnsureFonts();
        using var document = CreateDocument($"{project.ProgramName} Activity Log");
        document.Info.Subject = "Internal project activity and change history";
        using var logo = LoadLogo(logoPath);
        var ordered = entries.OrderByDescending(entry => entry.ChangedAt).ThenByDescending(entry => entry.Id).ToList();
        var pageNumber = 1;

        if (ordered.Count == 0)
        {
            DrawProjectActivityPage(document, project, [], 0, logo, pageNumber, true);
        }
        else
        {
            for (var offset = 0; offset < ordered.Count; offset += 17)
            {
                DrawProjectActivityPage(document, project, ordered.Skip(offset).Take(17).ToList(), offset, logo, pageNumber++, offset == 0);
            }
        }

        return Save(document);
    }

    private static void DrawProjectActivityPage(
        PdfDocument document,
        Project project,
        IReadOnlyList<ProjectAuditEntry> entries,
        int offset,
        XImage? logo,
        int pageNumber,
        bool firstPage)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PROJECT ACTIVITY", pageNumber);
        DrawText(
            graphics,
            firstPage ? $"{project.ProgramName} Activity Log" : $"{project.ProgramName} Activity Log - Continued",
            firstPage ? Fonts.Title : Fonts.PageTitle,
            Ink,
            28,
            82,
            600,
            28);
        DrawText(graphics, $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}", Fonts.Small, Muted, 28, 108, 520, 16);

        DrawMetadata(graphics, 28, 130, 736, new[]
        {
            ("PART NUMBER", project.ProgramName),
            ("JOB NUMBER", project.JobNumber ?? "Not set"),
            ("CUSTOMER", project.CustomerName ?? "Not set"),
            ("TOTAL ACTIVITY", project.AuditEntries.Count > 0 ? project.AuditEntries.Count.ToString() : entries.Count.ToString())
        });

        DrawSectionLabel(
            graphics,
            entries.Count == 0 ? "NO ACTIVITY RECORDED" : $"ACTIVITY {offset + 1}-{offset + entries.Count}",
            28,
            176);
        DrawActivityTable(graphics, entries, 28, 194, 736, 20.5);
        DrawFooter(graphics, page, pageNumber, $"{project.ProgramName} | Confidential internal activity log");
    }

    private static void DrawProjectOverviewPage(PdfDocument document, Project project, IReadOnlyList<ProjectTask> tasks, int offset, XImage? logo, int pageNumber, bool customerView)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PROJECT SCHEDULE", pageNumber);
        DrawText(graphics, project.ProgramName, Fonts.Title, Ink, 28, 82, 520, 28);
        DrawText(graphics, $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}", Fonts.Small, Muted, 28, 108, 520, 16);

        var metricsY = 130d;
        if (!customerView)
        {
            DrawMetadata(graphics, 28, 130, 736, new[]
            {
                ("CUSTOMER", project.CustomerName ?? "Not set"),
                ("SALES ORDER", project.SalesOrderNumber ?? "Not set"),
                ("JOB NUMBER", project.JobNumber ?? "Not set"),
                ("PROGRAM MANAGER", project.ProgramManager ?? "Not set"),
                ("CURRENT OPERATION", project.CurrentTask ?? "Not set")
            });
            metricsY = 169;
        }

        DrawMetrics(graphics, 28, metricsY, 736, new[]
        {
            new PdfMetric("Status", ReportText.Status(project.Status), StatusColor(project.Status), StatusTint(project.Status)),
            new PdfMetric("Completion", ReportText.Percent(project.Progress), Steel, SteelTint),
            new PdfMetric("Target Delivery", ReportText.Date(project.TargetDelivery), project.Status == ProjectStatus.Behind ? Red : Ink2, project.Status == ProjectStatus.Behind ? RedTint : Surface2),
            new PdfMetric("Operations", project.Tasks.Count.ToString(), Ink2, Surface2),
            customerView
                ? new PdfMetric("Current Operation", project.CurrentTask ?? "Not set", Steel, SteelTint)
                : new PdfMetric("Behind", project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind).ToString(), Red, RedTint)
        });

        var sectionY = metricsY + 58;
        DrawSectionLabel(graphics, "OPERATION SCHEDULE", 28, sectionY);
        DrawTaskTable(graphics, tasks, offset, 28, sectionY + 17, 736, 19.2, customerView);
        DrawFooter(graphics, page, pageNumber, ProjectFooter(project, customerView));
    }

    private static void DrawProjectContinuationPage(PdfDocument document, Project project, IReadOnlyList<ProjectTask> tasks, int offset, XImage? logo, int pageNumber, bool customerView)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PROJECT SCHEDULE", pageNumber);
        DrawText(graphics, $"{project.ProgramName} - Operation Schedule", Fonts.PageTitle, Ink, 28, 84, 650, 24);
        DrawSectionLabel(graphics, $"OPERATIONS {offset + 1}-{offset + tasks.Count}", 28, 120);
        DrawTaskTable(graphics, tasks, offset, 28, 138, 736, 20.5, customerView);
        DrawFooter(graphics, page, pageNumber, ProjectFooter(project, customerView));
    }

    private static string ProjectFooter(Project project, bool customerView) =>
        customerView ? $"{project.ProgramName} | Project schedule" : $"{project.ProgramName} | Internal project control";

    private static void DrawPortfolioOverviewPage(PdfDocument document, IReadOnlyList<Project> rows, IReadOnlyList<Project> allProjects, ScheduleCalendar calendar, XImage? logo, int pageNumber)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PORTFOLIO CONTROL", pageNumber);
        DrawText(graphics, "Development Portfolio", Fonts.Title, Ink, 28, 82, 520, 28);
        DrawText(graphics, $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  |  {WorkWeekLabel(calendar)}", Fonts.Small, Muted, 28, 108, 520, 16);
        var active = allProjects.Count(project => project.Status != ProjectStatus.Complete);
        var behind = allProjects.Count(project => project.Status == ProjectStatus.Behind);
        var average = allProjects.Count == 0 ? 0m : allProjects.Average(project => project.Progress);
        var nearest = allProjects.Where(project => project.Status != ProjectStatus.Complete && project.TargetDelivery is not null).Select(project => project.TargetDelivery).Min();
        DrawMetrics(graphics, 28, 137, 736, new[]
        {
            new PdfMetric("Active Projects", active.ToString(), Ink2, Surface2),
            new PdfMetric("Behind Schedule", behind.ToString(), Red, RedTint),
            new PdfMetric("Average Completion", ReportText.Percent(average), Steel, SteelTint),
            new PdfMetric("Nearest Delivery", ReportText.Date(nearest), Ink2, Surface2),
            new PdfMetric("Operations", allProjects.Sum(project => project.Tasks.Count).ToString(), Ink2, Surface2)
        });
        DrawSectionLabel(graphics, "PROJECT STATUS", 28, 197);
        DrawPortfolioTable(graphics, rows, 0, 28, 214, 736, 22);
        DrawFooter(graphics, page, pageNumber, "SON-AERO | Internal portfolio control");
    }

    private static void DrawPortfolioContinuationPage(PdfDocument document, IReadOnlyList<Project> projects, int offset, XImage? logo, int pageNumber)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PORTFOLIO CONTROL", pageNumber);
        DrawText(graphics, "Development Portfolio - Continued", Fonts.PageTitle, Ink, 28, 84, 650, 24);
        DrawSectionLabel(graphics, $"PROJECTS {offset + 1}-{offset + projects.Count}", 28, 120);
        DrawPortfolioTable(graphics, projects, offset, 28, 138, 736, 20.5);
        DrawFooter(graphics, page, pageNumber, "SON-AERO | Internal portfolio control");
    }

    private static void DrawPastProjectsOverviewPage(PdfDocument document, IReadOnlyList<Project> rows, IReadOnlyList<Project> allProjects, ScheduleCalendar calendar, XImage? logo, int pageNumber)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PAST PROJECTS", pageNumber);
        DrawText(graphics, "Completed Project Performance", Fonts.Title, Ink, 28, 82, 520, 28);
        DrawText(graphics, $"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  |  {WorkWeekLabel(calendar)}", Fonts.Small, Muted, 28, 108, 520, 16);
        var dated = allProjects.Where(project => project.TargetDelivery is not null && FinalCompletionDate(project) is not null).ToList();
        var onTime = dated.Count(project => FinalCompletionDate(project) <= project.TargetDelivery);
        var late = dated.Count - onTime;
        var onTimePercent = dated.Count == 0 ? 0m : (decimal)onTime / dated.Count;
        var average = allProjects.Count == 0 ? 0m : allProjects.Average(project => project.Progress);
        DrawMetrics(graphics, 28, 137, 736, new[]
        {
            new PdfMetric("Completed Projects", allProjects.Count.ToString(), Ink2, Surface2),
            new PdfMetric("On Time Percentage", ReportText.Percent(onTimePercent), Green, GreenTint),
            new PdfMetric("Late Projects", late.ToString(), Red, RedTint),
            new PdfMetric("Average Completion", ReportText.Percent(average), Steel, SteelTint)
        });
        DrawSectionLabel(graphics, "COMPLETED PROJECTS", 28, 197);
        DrawPastProjectsTable(graphics, rows, 0, 28, 214, 736, 22);
        DrawFooter(graphics, page, pageNumber, "SON-AERO | Completed project archive");
    }

    private static void DrawPastProjectsContinuationPage(PdfDocument document, IReadOnlyList<Project> projects, int offset, XImage? logo, int pageNumber)
    {
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PAST PROJECTS", pageNumber);
        DrawText(graphics, "Completed Project Performance - Continued", Fonts.PageTitle, Ink, 28, 84, 650, 24);
        DrawSectionLabel(graphics, $"PROJECTS {offset + 1}-{offset + projects.Count}", 28, 120);
        DrawPastProjectsTable(graphics, projects, offset, 28, 138, 736, 20.5);
        DrawFooter(graphics, page, pageNumber, "SON-AERO | Completed project archive");
    }

    private static void DrawTaskTable(XGraphics graphics, IReadOnlyList<ProjectTask> tasks, int offset, double x, double y, double width, double rowHeight, bool customerView)
    {
        // Customer sheets drop the work center and notes columns and spread the remaining width.
        var columns = customerView
            ? new[]
            {
                new PdfColumn("#", 30), new PdfColumn("Operation", 290), new PdfColumn("Start", 90),
                new PdfColumn("End", 90), new PdfColumn("Duration", 66), new PdfColumn("Complete", 76),
                new PdfColumn("Status", 94)
            }
            : new[]
            {
                new PdfColumn("#", 26), new PdfColumn("Operation", 176), new PdfColumn("Work Center", 90),
                new PdfColumn("Start", 68), new PdfColumn("End", 68), new PdfColumn("Duration", 54),
                new PdfColumn("Complete", 56), new PdfColumn("Status", 68), new PdfColumn("Notes", 130)
            };
        var statusColumn = customerView ? 6 : 7;
        DrawTableHeader(graphics, columns, x, y, rowHeight + 2);
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var rowY = y + rowHeight + 2 + (index * rowHeight);
            graphics.DrawRectangle(new XSolidBrush(index % 2 == 0 ? XColors.White : Surface2), x, rowY, width, rowHeight);
            graphics.DrawRectangle(new XSolidBrush(StatusColor(task.Status)), x, rowY, 3, rowHeight);
            var values = customerView
                ? new[]
                {
                    (offset + index + 1).ToString(), task.Title,
                    CompactDate(task.StartDate), CompactDate(task.EndDate), task.EstimatedDuration?.ToString() ?? string.Empty,
                    ReportText.Percent(task.PercentComplete), ReportText.Status(task.Status)
                }
                : new[]
                {
                    (offset + index + 1).ToString(), task.Title, task.WorkStation ?? "Unassigned",
                    CompactDate(task.StartDate), CompactDate(task.EndDate), task.EstimatedDuration?.ToString() ?? string.Empty,
                    ReportText.Percent(task.PercentComplete), ReportText.Status(task.Status), task.Notes ?? string.Empty
                };
            var cellX = x;
            for (var column = 0; column < columns.Length; column++)
            {
                if (column == statusColumn) graphics.DrawRectangle(new XSolidBrush(StatusTint(task.Status)), cellX, rowY, columns[column].Width, rowHeight);
                DrawText(graphics, FitText(graphics, values[column], Fonts.Table, columns[column].Width - 10), column == statusColumn ? Fonts.TableBold : Fonts.Table,
                    column == statusColumn ? StatusColor(task.Status) : Ink2, cellX + 5, rowY + 5, columns[column].Width - 10, rowHeight - 5);
                cellX += columns[column].Width;
            }
            graphics.DrawLine(new XPen(Line, 0.35), x, rowY + rowHeight, x + width, rowY + rowHeight);
        }
    }

    private static void DrawPortfolioTable(XGraphics graphics, IReadOnlyList<Project> projects, int offset, double x, double y, double width, double rowHeight)
    {
        var columns = new[]
        {
            new PdfColumn("Part No.", 145), new PdfColumn("Customer", 93), new PdfColumn("Manager", 92),
            new PdfColumn("Current Operation", 147), new PdfColumn("Progress", 55), new PdfColumn("Target", 75),
            new PdfColumn("Status", 72), new PdfColumn("Ops", 35), new PdfColumn("Behind", 42)
        };
        DrawTableHeader(graphics, columns, x, y, rowHeight + 2);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var rowY = y + rowHeight + 2 + (index * rowHeight);
            graphics.DrawRectangle(new XSolidBrush(index % 2 == 0 ? XColors.White : Surface2), x, rowY, width, rowHeight);
            graphics.DrawRectangle(new XSolidBrush(StatusColor(project.Status)), x, rowY, 3, rowHeight);
            var values = new[]
            {
                project.ProgramName, project.CustomerName ?? string.Empty, project.ProgramManager ?? string.Empty,
                project.CurrentTask ?? string.Empty, ReportText.Percent(project.Progress), CompactDate(project.TargetDelivery),
                ReportText.Status(project.Status), project.Tasks.Count.ToString(), project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind).ToString()
            };
            var cellX = x;
            for (var column = 0; column < columns.Length; column++)
            {
                if (column == 6) graphics.DrawRectangle(new XSolidBrush(StatusTint(project.Status)), cellX, rowY, columns[column].Width, rowHeight);
                DrawText(graphics, FitText(graphics, values[column], column == 0 ? Fonts.TableBold : Fonts.Table, columns[column].Width - 10),
                    column is 0 or 6 ? Fonts.TableBold : Fonts.Table, column == 6 ? StatusColor(project.Status) : Ink2,
                    cellX + 5, rowY + 6, columns[column].Width - 10, rowHeight - 5);
                cellX += columns[column].Width;
            }
            graphics.DrawLine(new XPen(Line, 0.35), x, rowY + rowHeight, x + width, rowY + rowHeight);
        }
    }

    private static void DrawPastProjectsTable(XGraphics graphics, IReadOnlyList<Project> projects, int offset, double x, double y, double width, double rowHeight)
    {
        var columns = new[]
        {
            new PdfColumn("Part No.", 140), new PdfColumn("Customer", 95), new PdfColumn("Manager", 90),
            new PdfColumn("Target", 82), new PdfColumn("Final Completion", 98), new PdfColumn("Result", 62),
            new PdfColumn("Progress", 58), new PdfColumn("Ops", 35), new PdfColumn("Sales Order", 96)
        };
        DrawTableHeader(graphics, columns, x, y, rowHeight + 2);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var finalCompletion = FinalCompletionDate(project);
            var isLate = project.TargetDelivery is not null && finalCompletion is not null && finalCompletion > project.TargetDelivery;
            var rowY = y + rowHeight + 2 + (index * rowHeight);
            graphics.DrawRectangle(new XSolidBrush(index % 2 == 0 ? XColors.White : Surface2), x, rowY, width, rowHeight);
            graphics.DrawRectangle(new XSolidBrush(isLate ? Red : Green), x, rowY, 3, rowHeight);
            var values = new[]
            {
                project.ProgramName, project.CustomerName ?? string.Empty, project.ProgramManager ?? string.Empty,
                CompactDate(project.TargetDelivery), CompactDate(finalCompletion), isLate ? "Late" : "On Time",
                ReportText.Percent(project.Progress), project.Tasks.Count.ToString(), project.SalesOrderNumber ?? string.Empty
            };
            var cellX = x;
            for (var column = 0; column < columns.Length; column++)
            {
                if (column == 5) graphics.DrawRectangle(new XSolidBrush(isLate ? RedTint : GreenTint), cellX, rowY, columns[column].Width, rowHeight);
                DrawText(graphics, FitText(graphics, values[column], column == 0 ? Fonts.TableBold : Fonts.Table, columns[column].Width - 10),
                    column is 0 or 5 ? Fonts.TableBold : Fonts.Table, column == 5 ? (isLate ? Red : Green) : Ink2,
                    cellX + 5, rowY + 6, columns[column].Width - 10, rowHeight - 5);
                cellX += columns[column].Width;
            }
            graphics.DrawLine(new XPen(Line, 0.35), x, rowY + rowHeight, x + width, rowY + rowHeight);
        }
    }

    private static void DrawActivityTable(
        XGraphics graphics,
        IReadOnlyList<ProjectAuditEntry> entries,
        double x,
        double y,
        double width,
        double rowHeight)
    {
        var columns = new[]
        {
            new PdfColumn("Date / Time", 105),
            new PdfColumn("User", 105),
            new PdfColumn("Action", 105),
            new PdfColumn("Summary", 205),
            new PdfColumn("Changes", 216)
        };
        DrawTableHeader(graphics, columns, x, y, rowHeight + 2);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var rowY = y + rowHeight + 2 + (index * rowHeight);
            graphics.DrawRectangle(new XSolidBrush(index % 2 == 0 ? XColors.White : Surface2), x, rowY, width, rowHeight);
            graphics.DrawRectangle(new XSolidBrush(Steel), x, rowY, 3, rowHeight);
            var values = new[]
            {
                entry.ChangedAt.LocalDateTime.ToString("MMM d, yyyy h:mm tt"),
                entry.ChangedByDisplayName,
                FriendlyAction(entry.Action),
                entry.Summary,
                DescribeChanges(entry)
            };
            var cellX = x;
            for (var column = 0; column < columns.Length; column++)
            {
                var font = column is 1 or 2 ? Fonts.TableBold : Fonts.Table;
                DrawText(
                    graphics,
                    FitText(graphics, values[column], font, columns[column].Width - 10),
                    font,
                    column == 2 ? Steel : Ink2,
                    cellX + 5,
                    rowY + 6,
                    columns[column].Width - 10,
                    rowHeight - 5);
                cellX += columns[column].Width;
            }
            graphics.DrawLine(new XPen(Line, 0.35), x, rowY + rowHeight, x + width, rowY + rowHeight);
        }
    }

    private static string DescribeChanges(ProjectAuditEntry entry)
    {
        var changes = ProjectAuditService.ReadChanges(entry.ChangesJson);
        return changes.Count == 0
            ? "No field-level changes"
            : string.Join("; ", changes.Select(change =>
                $"{change.Field}: {change.OldValue ?? "blank"} -> {change.NewValue ?? "blank"}"));
    }

    private static string FriendlyAction(string action)
    {
        return string.Concat(action.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
    }

    private static void DrawTableHeader(XGraphics graphics, IReadOnlyList<PdfColumn> columns, double x, double y, double height)
    {
        graphics.DrawRectangle(new XSolidBrush(SteelTint), x, y, columns.Sum(column => column.Width), height);
        var cellX = x;
        foreach (var column in columns)
        {
            DrawText(graphics, FitText(graphics, column.Label.ToUpperInvariant(), Fonts.TableHeader, column.Width - 10), Fonts.TableHeader, Ink2, cellX + 5, y + 6, column.Width - 10, height - 6);
            cellX += column.Width;
        }
        graphics.DrawLine(new XPen(Steel, 1.2), x, y + height, cellX, y + height);
    }

    private static void DrawProjectGanttPage(PdfDocument document, Project project, IReadOnlyList<ProjectTask> tasks, int offset, ScheduleCalendar calendar, XImage? logo, int pageNumber, bool customerView)
    {
        var allTasks = project.Tasks.OrderBy(task => task.Sequence).ToList();
        var bounds = allTasks.Select(task => NormalizeRange(task.StartDate, task.EndDate)).Where(range => range is not null).Select(range => range!.Value).ToList();
        var start = bounds.Count > 0 ? bounds.Min(range => range.Start).AddDays(-2) : DateOnly.FromDateTime(DateTime.Today);
        var end = bounds.Count > 0 ? bounds.Max(range => range.End).AddDays(3) : start.AddDays(30);
        var buckets = BuildBuckets(start, end);
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "OPERATION GANTT", pageNumber);
        DrawText(graphics, $"{project.ProgramName} Timeline", Fonts.PageTitle, Ink, 28, 82, 520, 24);
        var legend = customerView
            ? $"{start:MMM d, yyyy} - {end:MMM d, yyyy}  |  Green on track, red behind, graphite complete"
            : $"{start:MMM d, yyyy} - {end:MMM d, yyyy}  |  {WorkWeekLabel(calendar)}  |  Green on track, red behind, graphite complete";
        DrawText(graphics, legend, Fonts.Small, Muted, 28, 106, 700, 14);
        Func<ProjectTask, string>? subtitle = customerView ? null : task => task.WorkStation ?? "Unassigned";
        DrawGantt(graphics, tasks, offset, buckets, calendar, 28, 130, 736, task => task.Title, subtitle,
            task => NormalizeRange(task.StartDate, task.EndDate), task => task.PercentComplete, task => StatusColor(task.Status), task => StatusTint(task.Status));
        DrawFooter(graphics, page, pageNumber, customerView ? $"{project.ProgramName} | Project timeline" : $"{project.ProgramName} | Operation timeline");
    }

    private static void DrawPortfolioGanttPage(PdfDocument document, IReadOnlyList<Project> projects, int offset, ScheduleCalendar calendar, XImage? logo, int pageNumber)
    {
        var bounds = projects.Select(project => NormalizeRange(project.ProgramStart, project.TargetDelivery)).Where(range => range is not null).Select(range => range!.Value).ToList();
        var start = bounds.Count > 0 ? bounds.Min(range => range.Start).AddDays(-7) : DateOnly.FromDateTime(DateTime.Today);
        var end = bounds.Count > 0 ? bounds.Max(range => range.End).AddDays(14) : start.AddMonths(3);
        var buckets = BuildBuckets(start, end, preferWeekly: true);
        var page = AddLandscapePage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawBrandHeader(graphics, page, logo, "PORTFOLIO GANTT", pageNumber);
        DrawText(graphics, "Portfolio Delivery Timeline", Fonts.PageTitle, Ink, 28, 82, 520, 24);
        DrawText(graphics, $"{start:MMM d, yyyy} - {end:MMM d, yyyy}  |  Green on track, red behind, graphite complete", Fonts.Small, Muted, 28, 106, 700, 14);
        DrawGantt(graphics, projects, offset, buckets, calendar, 28, 130, 736, project => project.ProgramName, project => project.CustomerName ?? "Not set",
            project => NormalizeRange(project.ProgramStart, project.TargetDelivery), project => project.Progress, project => StatusColor(project.Status), project => StatusTint(project.Status));
        DrawFooter(graphics, page, pageNumber, "SON-AERO | Portfolio delivery timeline");
    }

    private static void DrawGantt<T>(
        XGraphics graphics,
        IReadOnlyList<T> items,
        int offset,
        IReadOnlyList<TimelineBucket> buckets,
        ScheduleCalendar calendar,
        double x,
        double y,
        double width,
        Func<T, string> title,
        Func<T, string>? subtitle,
        Func<T, (DateOnly Start, DateOnly End)?> range,
        Func<T, decimal> progress,
        Func<T, XColor> color,
        Func<T, XColor> tint)
    {
        const double labelWidth = 196;
        const double monthBand = 17;
        const double axisHeight = 38;
        const double rowHeight = 24;
        var trackWidth = width - labelWidth;
        var bucketWidth = trackWidth / buckets.Count;
        var bodyHeight = Math.Max(1, items.Count) * rowHeight;
        var bottom = y + axisHeight + bodyHeight;

        graphics.DrawRectangle(new XSolidBrush(Surface2), x, y, labelWidth, axisHeight);
        graphics.DrawRectangle(new XSolidBrush(SteelTint), x + labelWidth, y, trackWidth, axisHeight);
        DrawText(graphics, "OPERATION / PROJECT", Fonts.TableHeader, Ink2, x + 8, y + 22, labelWidth - 16, 12);

        var monthBoundaries = new List<double>();
        var monthStart = 0;
        while (monthStart < buckets.Count)
        {
            var key = (buckets[monthStart].Start.Year, buckets[monthStart].Start.Month);
            var monthEnd = monthStart;
            while (monthEnd + 1 < buckets.Count && (buckets[monthEnd + 1].Start.Year, buckets[monthEnd + 1].Start.Month) == key) monthEnd++;
            var monthX = x + labelWidth + (monthStart * bucketWidth);
            var monthWidth = (monthEnd - monthStart + 1) * bucketWidth;
            if (monthWidth >= 24)
            {
                var monthLabel = buckets[monthStart].Start.ToString(monthWidth < 55 ? "MMM" : "MMM yyyy").ToUpperInvariant();
                DrawText(graphics, FitText(graphics, monthLabel, Fonts.AxisBold, monthWidth - 8), Fonts.AxisBold, Ink2, monthX + 4, y + 4, monthWidth - 8, 12);
            }
            if (monthStart > 0) monthBoundaries.Add(monthX);
            monthStart = monthEnd + 1;
        }
        graphics.DrawLine(new XPen(Line, 0.5), x + labelWidth, y + monthBand, x + width, y + monthBand);

        var labelStep = Math.Max(1, (int)Math.Ceiling(34 / Math.Max(bucketWidth, 1)));
        var tickPositions = new List<double>();
        for (var index = 0; index < buckets.Count; index += labelStep)
        {
            var tickX = x + labelWidth + (index * bucketWidth);
            if (index > 0) tickPositions.Add(tickX);
            DrawText(graphics, buckets[index].Label, Fonts.Axis, Muted, tickX + 3, y + 23, Math.Max(32, (bucketWidth * labelStep) - 4), 12);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var rowY = y + axisHeight + (index * rowHeight);
            graphics.DrawRectangle(new XSolidBrush(index % 2 == 0 ? XColors.White : Surface2), x, rowY, width, rowHeight);
            for (var bucketIndex = 0; bucketIndex < buckets.Count; bucketIndex++)
            {
                var background = BucketBackground(buckets[bucketIndex], calendar);
                if (background != XColors.White)
                {
                    graphics.DrawRectangle(new XSolidBrush(background), x + labelWidth + (bucketIndex * bucketWidth), rowY, bucketWidth, rowHeight);
                }
            }
        }

        // Gridlines sit on top of the row fills so the timeline stays readable across the page.
        foreach (var tick in tickPositions) graphics.DrawLine(new XPen(Surface3, 0.4), tick, y + axisHeight, tick, bottom);
        foreach (var boundary in monthBoundaries) graphics.DrawLine(new XPen(Line, 0.5), boundary, y + monthBand, boundary, bottom);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var rowY = y + axisHeight + (index * rowHeight);
            var heading = $"{offset + index + 1}. {FitText(graphics, title(item), Fonts.TableBold, labelWidth - 30)}";
            var subtitleText = subtitle?.Invoke(item);
            if (string.IsNullOrWhiteSpace(subtitleText))
            {
                DrawText(graphics, heading, Fonts.TableBold, Ink, x + 8, rowY + 8, labelWidth - 16, 11);
            }
            else
            {
                DrawText(graphics, heading, Fonts.TableBold, Ink, x + 8, rowY + 4, labelWidth - 16, 11);
                DrawText(graphics, FitText(graphics, subtitleText, Fonts.Tiny, labelWidth - 32), Fonts.Tiny, Muted, x + 19, rowY + 14, labelWidth - 27, 9);
            }

            var itemRange = range(item);
            if (itemRange is not null)
            {
                var matching = buckets.Select((bucket, bucketIndex) => new { bucket, bucketIndex })
                    .Where(entry => Overlaps(itemRange.Value, entry.bucket))
                    .Select(entry => entry.bucketIndex)
                    .ToList();
                if (matching.Count > 0)
                {
                    const double barHeight = 13;
                    var barX = x + labelWidth + (matching[0] * bucketWidth) + 1;
                    var barWidth = Math.Max(3, (matching.Count * bucketWidth) - 2);
                    var barY = rowY + ((rowHeight - barHeight) / 2);
                    graphics.DrawRoundedRectangle(new XSolidBrush(tint(item)), barX, barY, barWidth, barHeight, 4, 4);
                    var progressWidth = barWidth * (double)Math.Clamp(progress(item), 0m, 1m);
                    if (progressWidth > 1) graphics.DrawRoundedRectangle(new XSolidBrush(color(item)), barX, barY, progressWidth, barHeight, 4, 4);
                    graphics.DrawRoundedRectangle(new XPen(color(item), 0.5), barX, barY, barWidth, barHeight, 4, 4);
                    if (barWidth > 30)
                    {
                        DrawText(graphics, ReportText.Percent(progress(item)), Fonts.TinyBold, progress(item) >= 0.35m ? XColors.White : color(item),
                            barX + 5, barY + 3, barWidth - 10, 9);
                    }
                }
            }
            graphics.DrawLine(new XPen(Line, 0.35), x, rowY + rowHeight, x + width, rowY + rowHeight);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var todayIndex = buckets.Select((bucket, index) => new { bucket, index }).FirstOrDefault(entry => today >= entry.bucket.Start && today <= entry.bucket.End);
        if (todayIndex is not null)
        {
            var todayX = x + labelWidth + (todayIndex.index * bucketWidth) + (bucketWidth / 2);
            graphics.DrawLine(new XPen(Red, 1), todayX, y + monthBand, todayX, bottom);
            var flagX = Math.Min(todayX - 1, x + width - 31);
            graphics.DrawRectangle(new XSolidBrush(Red), flagX, y + 3, 30, 11);
            DrawText(graphics, "TODAY", Fonts.TinyBold, XColors.White, flagX + 4, y + 5, 24, 9);
        }
        graphics.DrawLine(new XPen(Line, 0.7), x + labelWidth, y, x + labelWidth, bottom);
        graphics.DrawRectangle(new XPen(Line, 0.7), x, y, width, axisHeight + bodyHeight);
    }

    private static void DrawBrandHeader(XGraphics graphics, PdfPage page, XImage? logo, string eyebrow, int pageNumber)
    {
        var width = page.Width.Point;
        graphics.DrawRectangle(new XSolidBrush(Navy), 0, 0, width, 64);
        graphics.DrawRectangle(new XSolidBrush(Red), 0, 64, width, 3);
        if (logo is not null) graphics.DrawImage(logo, 25, 9, 164, 45);
        else DrawText(graphics, "SON-AERO", Fonts.Logo, XColors.White, 27, 19, 180, 28);
        DrawText(graphics, eyebrow, Fonts.Eyebrow, Color("CBD5E1"), width - 250, 20, 220, 12, XStringFormats.TopRight);
        DrawText(graphics, $"PAGE {pageNumber}", Fonts.Tiny, Color("98A3AF"), width - 250, 38, 220, 10, XStringFormats.TopRight);
    }

    private static void DrawFooter(XGraphics graphics, PdfPage page, int pageNumber, string label)
    {
        var y = page.Height.Point - 24;
        graphics.DrawLine(new XPen(Line, 0.5), 28, y - 7, page.Width.Point - 28, y - 7);
        DrawText(graphics, label, Fonts.Tiny, Muted, 28, y, 550, 10);
        DrawText(graphics, $"Page {pageNumber}", Fonts.TinyBold, Muted, page.Width.Point - 100, y, 72, 10, XStringFormats.TopRight);
    }

    private static void DrawMetadata(XGraphics graphics, double x, double y, double width, IReadOnlyList<(string Label, string Value)> values)
    {
        var itemWidth = width / values.Count;
        graphics.DrawRectangle(new XSolidBrush(Surface2), x, y, width, 28);
        for (var index = 0; index < values.Count; index++)
        {
            var itemX = x + (index * itemWidth);
            if (index > 0) graphics.DrawLine(new XPen(Line, 0.5), itemX, y + 5, itemX, y + 23);
            DrawText(graphics, values[index].Label, Fonts.TinyBold, Muted, itemX + 8, y + 5, itemWidth - 16, 8);
            DrawText(graphics, FitText(graphics, values[index].Value, Fonts.SmallBold, itemWidth - 16), Fonts.SmallBold, Ink2, itemX + 8, y + 15, itemWidth - 16, 10);
        }
    }

    private static void DrawMetrics(XGraphics graphics, double x, double y, double width, IReadOnlyList<PdfMetric> metrics)
    {
        const double gap = 8;
        var metricWidth = (width - ((metrics.Count - 1) * gap)) / metrics.Count;
        for (var index = 0; index < metrics.Count; index++)
        {
            var metric = metrics[index];
            var metricX = x + (index * (metricWidth + gap));
            graphics.DrawRectangle(new XSolidBrush(metric.Fill), metricX, y, metricWidth, 43);
            graphics.DrawRectangle(new XSolidBrush(metric.Accent), metricX, y, 3, 43);
            DrawText(graphics, metric.Label.ToUpperInvariant(), Fonts.TinyBold, Muted, metricX + 10, y + 7, metricWidth - 18, 9);
            DrawText(graphics, FitText(graphics, metric.Value, Fonts.Metric, metricWidth - 18), Fonts.Metric, metric.Accent, metricX + 10, y + 20, metricWidth - 18, 18);
        }
    }

    private static void DrawSectionLabel(XGraphics graphics, string value, double x, double y)
    {
        graphics.DrawRectangle(new XSolidBrush(Red), x, y + 1, 3, 11);
        DrawText(graphics, value, Fonts.EyebrowDark, Muted, x + 9, y, 320, 14);
    }

    private static void DrawText(XGraphics graphics, string value, XFont font, XColor color, double x, double y, double width, double height, XStringFormat? format = null)
    {
        graphics.DrawString(value, font, new XSolidBrush(color), new XRect(x, y, width, height), format ?? XStringFormats.TopLeft);
    }

    private static string FitText(XGraphics graphics, string value, XFont font, double width)
    {
        if (string.IsNullOrEmpty(value) || graphics.MeasureString(value, font).Width <= width) return value;
        var text = value;
        while (text.Length > 1 && graphics.MeasureString(text + "...", font).Width > width) text = text[..^1];
        return text.TrimEnd() + "...";
    }

    private static IReadOnlyList<TimelineBucket> BuildBuckets(DateOnly start, DateOnly end, bool preferWeekly = false)
    {
        if (end < start) (start, end) = (end, start);
        var span = end.DayNumber - start.DayNumber + 1;
        if (!preferWeekly && span <= 120)
        {
            return Enumerable.Range(0, span).Select(offset =>
            {
                var day = start.AddDays(offset);
                return new TimelineBucket(day, day, day.ToString("dd"));
            }).ToList();
        }
        if (span <= 730)
        {
            var cursor = StartOfWeek(start);
            var result = new List<TimelineBucket>();
            while (cursor <= end)
            {
                result.Add(new TimelineBucket(cursor, cursor.AddDays(6), cursor.ToString("dd MMM")));
                cursor = cursor.AddDays(7);
            }
            return result;
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

    private static (DateOnly Start, DateOnly End)? NormalizeRange(DateOnly? start, DateOnly? end)
    {
        if (start is null && end is null) return null;
        var normalizedStart = start ?? end!.Value;
        var normalizedEnd = end ?? normalizedStart;
        return normalizedEnd < normalizedStart ? (normalizedStart, normalizedStart) : (normalizedStart, normalizedEnd);
    }

    private static DateOnly? FinalCompletionDate(Project project)
    {
        return project.CompletedOn;
    }

    private static bool Overlaps((DateOnly Start, DateOnly End) range, TimelineBucket bucket) => range.Start <= bucket.End && range.End >= bucket.Start;

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static string WorkWeekLabel(ScheduleCalendar calendar)
    {
        var days = calendar.WorkingDays.OrderBy(day => ((int)day + 6) % 7).Select(day => day.ToString()[..3]);
        return $"Work week: {string.Join(", ", days)}";
    }

    private static XColor BucketBackground(TimelineBucket bucket, ScheduleCalendar calendar)
    {
        if (bucket.Start == bucket.End)
        {
            if (calendar.Holidays.Contains(bucket.Start)) return RedTint;
            if (!calendar.WorkingDays.Contains(bucket.Start.DayOfWeek)) return Surface3;
        }
        return XColors.White;
    }

    private static string CompactDate(DateOnly? date) => date?.ToString("MM/dd/yyyy") ?? string.Empty;

    private static XColor StatusColor(ProjectStatus status) => status switch
    {
        ProjectStatus.Behind => Red,
        ProjectStatus.OnTrack => Green,
        ProjectStatus.Complete => Done,
        _ => Idle
    };

    private static XColor StatusTint(ProjectStatus status) => status switch
    {
        ProjectStatus.Behind => RedTint,
        ProjectStatus.OnTrack => GreenTint,
        ProjectStatus.Complete => DoneTint,
        _ => IdleTint
    };

    private static XColor StatusColor(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.Behind => Red,
        TaskScheduleStatus.OnTrack => Green,
        TaskScheduleStatus.Complete => Done,
        _ => Idle
    };

    private static XColor StatusTint(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.Behind => RedTint,
        TaskScheduleStatus.OnTrack => GreenTint,
        TaskScheduleStatus.Complete => DoneTint,
        _ => IdleTint
    };

    private static PdfDocument CreateDocument(string title, bool customerView = false)
    {
        var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Author = "SON-AERO";
        document.Info.Subject = customerView ? "Customer project schedule report" : "Internal project control report";
        document.Info.Creator = "SON-AERO Project Manager";
        return document;
    }

    private static PdfPage AddLandscapePage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PageSize.Letter;
        page.Orientation = PageOrientation.Landscape;
        return page;
    }

    private static XImage? LoadLogo(string? logoPath) => string.IsNullOrWhiteSpace(logoPath) ? null : XImage.FromFile(logoPath);

    private static byte[] Save(PdfDocument document)
    {
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static void EnsureFonts()
    {
        if (fontsInitialized) return;
        lock (FontLock)
        {
            if (fontsInitialized) return;
            if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            fontsInitialized = true;
        }
    }

    private static XColor Color(string hex) => XColor.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));

    private sealed record PdfMetric(string Label, string Value, XColor Accent, XColor Fill);
    private sealed record PdfColumn(string Label, double Width);
    private sealed record TimelineBucket(DateOnly Start, DateOnly End, string Label);

    private static class Fonts
    {
        public static readonly XFont Logo = new("Arial", 20, XFontStyleEx.Bold);
        public static readonly XFont Title = new("Arial", 22, XFontStyleEx.Bold);
        public static readonly XFont PageTitle = new("Arial", 17, XFontStyleEx.Bold);
        public static readonly XFont Metric = new("Arial", 12, XFontStyleEx.Bold);
        public static readonly XFont Small = new("Arial", 8, XFontStyleEx.Regular);
        public static readonly XFont SmallBold = new("Arial", 8, XFontStyleEx.Bold);
        public static readonly XFont Tiny = new("Arial", 6.5, XFontStyleEx.Regular);
        public static readonly XFont TinyBold = new("Arial", 6.5, XFontStyleEx.Bold);
        public static readonly XFont Eyebrow = new("Arial", 7.5, XFontStyleEx.Bold);
        public static readonly XFont EyebrowDark = new("Arial", 7.5, XFontStyleEx.Bold);
        public static readonly XFont Table = new("Arial", 7.2, XFontStyleEx.Regular);
        public static readonly XFont TableBold = new("Arial", 7.2, XFontStyleEx.Bold);
        public static readonly XFont TableHeader = new("Arial", 7, XFontStyleEx.Bold);
        public static readonly XFont Axis = new("Arial", 6.2, XFontStyleEx.Regular);
        public static readonly XFont AxisBold = new("Arial", 6.5, XFontStyleEx.Bold);
    }
}
