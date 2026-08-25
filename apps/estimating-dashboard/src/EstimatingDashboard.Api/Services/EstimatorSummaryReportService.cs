using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace EstimatingDashboard.Api.Services;

public sealed record EstimatorSummaryReportFile(byte[] Content, string FileName);

public sealed class EstimatorSummaryReportService(
    EstimatingAccessDbContext db,
    EstimatingHistoryQueryService historyQueries)
{
    public async Task<EstimatorSummaryReportFile> CreateAsync(
        string periodValue,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var dashboard = await historyQueries.GetDashboardAsync(periodValue, access, cancellationToken);
        var estimatorNames = dashboard.Users
            .Select(user => user.Estimator)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = await db.QuoteHistory.AsNoTracking().ToListAsync(cancellationToken);
        var reportRows = dashboard.Users
            .Select(user => new EstimatorSummaryReportRow(
                user,
                BuildTrend(
                    records.Where(record => estimatorNames.Contains(record.EstimatingRep)
                        && string.Equals(record.EstimatingRep, user.Estimator, StringComparison.OrdinalIgnoreCase)),
                    dashboard.Period,
                    dashboard.PeriodStart,
                    dashboard.PeriodEnd,
                    dashboard.GeneratedAt.LocalDateTime.Date)))
            .ToList();

        return new EstimatorSummaryReportFile(
            EstimatorSummaryPdfBuilder.Build(reportRows, dashboard),
            $"estimator-summary-{dashboard.Period}-{DateTime.Today:yyyyMMdd}.pdf");
    }

    private static IReadOnlyList<EstimatorTrendPoint> BuildTrend(
        IEnumerable<EstimatingQuoteHistoryRecord> source,
        string period,
        DateTime? periodStart,
        DateTime? periodEnd,
        DateTime today)
    {
        if (period == "week") return [];

        var measured = source
            .Where(record => record.IsCompleted
                && record.EstimatingCompletionDate.HasValue
                && record.OnTimeStatus is EstimatingOnTimeStatuses.OnTime or EstimatingOnTimeStatuses.Late)
            .ToList();

        IReadOnlyList<TrendBucket> buckets = period == "month"
            ? MonthWeekBuckets(periodStart ?? new DateTime(today.Year, today.Month, 1), periodEnd)
            : AllTimeMonthBuckets(measured, today);

        return buckets.Select(bucket =>
        {
            var bucketRecords = measured.Where(record =>
                record.EstimatingCompletionDate >= bucket.Start
                && record.EstimatingCompletionDate < bucket.End).ToList();
            var onTime = bucketRecords.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.OnTime);
            var late = bucketRecords.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.Late);
            return new EstimatorTrendPoint(
                bucket.Label,
                onTime,
                late,
                onTime + late == 0 ? null : Math.Round(onTime * 100d / (onTime + late), 1));
        }).ToList();
    }

    private static IReadOnlyList<TrendBucket> MonthWeekBuckets(DateTime monthStart, DateTime? periodEnd)
    {
        var monthEnd = periodEnd ?? monthStart.AddMonths(1);
        var cursor = monthStart.Date;
        var result = new List<TrendBucket>();
        while (cursor < monthEnd)
        {
            var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)cursor.DayOfWeek + 7) % 7;
            var end = Min(cursor.AddDays(daysUntilSunday + 1), monthEnd);
            var inclusiveEnd = end.AddDays(-1);
            var label = cursor.Month == inclusiveEnd.Month
                ? $"{cursor:MMM d}-{inclusiveEnd.Day}"
                : $"{cursor:MMM d}-{inclusiveEnd:MMM d}";
            result.Add(new TrendBucket(cursor, end, label));
            cursor = end;
        }
        return result;
    }

    private static IReadOnlyList<TrendBucket> AllTimeMonthBuckets(
        IReadOnlyList<EstimatingQuoteHistoryRecord> records,
        DateTime today)
    {
        var latestCompletion = records
            .Select(record => record.EstimatingCompletionDate!.Value)
            .DefaultIfEmpty(today)
            .Max();
        var endMonth = new DateTime(
            Math.Max(today.Year, latestCompletion.Year),
            latestCompletion > today ? latestCompletion.Month : today.Month,
            1);
        var firstCompletion = records
            .Select(record => record.EstimatingCompletionDate!.Value)
            .DefaultIfEmpty(endMonth)
            .Min();
        var cursor = new DateTime(firstCompletion.Year, firstCompletion.Month, 1);
        var result = new List<TrendBucket>();
        while (cursor <= endMonth)
        {
            result.Add(new TrendBucket(cursor, cursor.AddMonths(1), cursor.ToString("MMM yy")));
            cursor = cursor.AddMonths(1);
        }
        return result;
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;

    private sealed record TrendBucket(DateTime Start, DateTime End, string Label);
}

public sealed record EstimatorSummaryReportRow(
    EstimatingHistoryUserStatsDto Statistics,
    IReadOnlyList<EstimatorTrendPoint> Trend);

public sealed record EstimatorTrendPoint(
    string Label,
    int OnTime,
    int Late,
    double? Percentage);

internal static class EstimatorSummaryPdfBuilder
{
    private static readonly XColor Navy = Color("111A24");
    private static readonly XColor Ink = Color("111A24");
    private static readonly XColor Muted = Color("667587");
    private static readonly XColor Line = Color("D5DDE5");
    private static readonly XColor Surface = Color("F4F7F9");
    private static readonly XColor Steel = Color("2F6195");
    private static readonly XColor SteelTint = Color("E7EFF7");
    private static readonly XColor Green = Color("237A53");
    private static readonly XColor GreenTint = Color("E7F4ED");
    private static readonly XColor Red = Color("C73A2B");
    private static readonly XColor RedTint = Color("FBECE9");
    private static readonly object FontLock = new();
    private static bool fontsInitialized;

    public static byte[] Build(
        IReadOnlyList<EstimatorSummaryReportRow> estimators,
        EstimatingHistoryDashboardDto dashboard)
    {
        EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = $"Estimator Summary - {dashboard.PeriodLabel}";
        document.Info.Author = "SON-AERO";
        document.Info.Subject = "Estimating performance statistics";
        document.Info.Creator = "SON-AERO Estimating Dashboard";

        if (estimators.Count == 0)
            DrawEmptyPage(document, dashboard);
        else
            for (var index = 0; index < estimators.Count; index++)
                DrawEstimatorPage(document, estimators[index], dashboard, index + 1, estimators.Count);

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static void DrawEstimatorPage(
        PdfDocument document,
        EstimatorSummaryReportRow row,
        EstimatingHistoryDashboardDto dashboard,
        int pageNumber,
        int pageCount)
    {
        var page = AddPage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawHeader(graphics, page, pageNumber, pageCount);

        var stats = row.Statistics;
        DrawText(graphics, stats.Estimator, Fonts.Title, Ink, 32, 84, 500, 28);
        DrawText(
            graphics,
            $"Estimator summary | {dashboard.PeriodLabel}",
            Fonts.Subtitle,
            Muted,
            32,
            113,
            450,
            15);
        DrawText(
            graphics,
            $"Generated {dashboard.GeneratedAt.LocalDateTime:MMM d, yyyy h:mm tt}",
            Fonts.Small,
            Muted,
            page.Width.Point - 255,
            91,
            223,
            13,
            XStringFormats.TopRight);

        var measured = stats.OnTimeInPeriod + stats.LateInPeriod;
        var onTimePercentage = measured == 0 ? "No data" : $"{Math.Round(stats.OnTimeInPeriod * 100d / measured):0}%";
        var metrics = new[]
        {
            new Metric("Quotes in queue", stats.InQueue.ToString("N0"), Steel, SteelTint),
            new Metric("Completed", stats.CompletedInPeriod.ToString("N0"), Green, GreenTint),
            new Metric("On-time percentage", onTimePercentage, Green, GreenTint),
            new Metric("Late", stats.LateInPeriod.ToString("N0"), Red, RedTint),
            new Metric("Completed value", Currency(stats.CompletedValueInPeriod), Steel, SteelTint),
            new Metric(
                "Average completion",
                stats.AverageCompletionWorkdaysInPeriod.HasValue
                    ? $"{stats.AverageCompletionWorkdaysInPeriod:0.0} workdays"
                    : "No data",
                Steel,
                SteelTint)
        };
        DrawMetrics(graphics, metrics, 32, 144, page.Width.Point - 64);

        DrawSectionLabel(graphics, "ESTIMATOR CONTEXT", 32, 278);
        DrawContext(graphics, stats, 32, 297, page.Width.Point - 64);

        if (dashboard.Period != "week")
        {
            var title = dashboard.Period == "all"
                ? "ON-TIME PERFORMANCE BY MONTH"
                : "ON-TIME PERFORMANCE BY WEEK";
            DrawSectionLabel(graphics, title, 32, 349);
            DrawTrendChart(graphics, row.Trend, 32, 369, page.Width.Point - 64, 168);
        }
        else
        {
            DrawSectionLabel(graphics, "PERIOD NOTES", 32, 349);
            graphics.DrawRectangle(new XSolidBrush(Surface), 32, 369, page.Width.Point - 64, 82);
            DrawText(
                graphics,
                "This export reflects the current week selected in Estimating Logs. Monthly and all-time exports include an additional on-time trend chart.",
                Fonts.Body,
                Muted,
                48,
                390,
                page.Width.Point - 96,
                42);
        }

        DrawFooter(graphics, page, pageNumber);
    }

    private static void DrawEmptyPage(PdfDocument document, EstimatingHistoryDashboardDto dashboard)
    {
        var page = AddPage(document);
        using var graphics = XGraphics.FromPdfPage(page);
        DrawHeader(graphics, page, 1, 1);
        DrawText(graphics, "Estimator Summary", Fonts.Title, Ink, 32, 90, 500, 28);
        DrawText(graphics, dashboard.PeriodLabel, Fonts.Subtitle, Muted, 32, 120, 300, 15);
        graphics.DrawRectangle(new XSolidBrush(Surface), 32, 170, page.Width.Point - 64, 100);
        DrawText(
            graphics,
            "No active estimator statistics are available for this report.",
            Fonts.BodyBold,
            Muted,
            48,
            210,
            page.Width.Point - 96,
            20,
            XStringFormats.TopCenter);
        DrawFooter(graphics, page, 1);
    }

    private static PdfPage AddPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PageSize.Letter;
        page.Orientation = PageOrientation.Landscape;
        return page;
    }

    private static void DrawHeader(XGraphics graphics, PdfPage page, int pageNumber, int pageCount)
    {
        graphics.DrawRectangle(new XSolidBrush(Navy), 0, 0, page.Width.Point, 65);
        graphics.DrawRectangle(new XSolidBrush(Red), 0, 65, page.Width.Point, 3);
        DrawText(graphics, "SON-AERO", Fonts.Brand, XColors.White, 30, 16, 220, 27);
        DrawText(graphics, "ESTIMATING PERFORMANCE", Fonts.EyebrowLight, Color("CBD5E1"), 30, 45, 220, 10);
        DrawText(
            graphics,
            $"ESTIMATOR SUMMARY  |  {pageNumber} OF {pageCount}",
            Fonts.EyebrowLight,
            Color("CBD5E1"),
            page.Width.Point - 300,
            28,
            268,
            12,
            XStringFormats.TopRight);
    }

    private static void DrawMetrics(XGraphics graphics, IReadOnlyList<Metric> metrics, double x, double y, double width)
    {
        const double gap = 10;
        const double height = 53;
        var cardWidth = (width - (2 * gap)) / 3;
        for (var index = 0; index < metrics.Count; index++)
        {
            var column = index % 3;
            var row = index / 3;
            var cardX = x + (column * (cardWidth + gap));
            var cardY = y + (row * (height + gap));
            var metric = metrics[index];
            graphics.DrawRectangle(new XSolidBrush(metric.Fill), cardX, cardY, cardWidth, height);
            graphics.DrawRectangle(new XSolidBrush(metric.Accent), cardX, cardY, 4, height);
            DrawText(graphics, metric.Label.ToUpperInvariant(), Fonts.Eyebrow, Muted, cardX + 13, cardY + 8, cardWidth - 24, 10);
            DrawText(graphics, metric.Value, Fonts.Metric, metric.Accent, cardX + 13, cardY + 24, cardWidth - 24, 22);
        }
    }

    private static void DrawContext(
        XGraphics graphics,
        EstimatingHistoryUserStatsDto stats,
        double x,
        double y,
        double width)
    {
        var values = new[]
        {
            ("COMPLETED THIS WEEK", stats.CompletedThisWeek.ToString("N0")),
            ("COMPLETED THIS MONTH", stats.CompletedThisMonth.ToString("N0")),
            ("COMPLETED ALL TIME", stats.CompletedAllTime.ToString("N0")),
            ("TOTAL QUOTE VALUE", Currency(stats.TotalQuoteValue)),
            ("COMPLETED VALUE", Currency(stats.CompletedQuoteValue))
        };
        var itemWidth = width / values.Length;
        graphics.DrawRectangle(new XSolidBrush(Surface), x, y, width, 39);
        for (var index = 0; index < values.Length; index++)
        {
            var itemX = x + (index * itemWidth);
            if (index > 0) graphics.DrawLine(new XPen(Line, .6), itemX, y + 6, itemX, y + 33);
            DrawText(graphics, values[index].Item1, Fonts.TinyBold, Muted, itemX + 9, y + 7, itemWidth - 18, 9);
            DrawText(graphics, values[index].Item2, Fonts.SmallBold, Ink, itemX + 9, y + 20, itemWidth - 18, 13);
        }
    }

    private static void DrawTrendChart(
        XGraphics graphics,
        IReadOnlyList<EstimatorTrendPoint> trend,
        double x,
        double y,
        double width,
        double height)
    {
        graphics.DrawRectangle(new XSolidBrush(Surface), x, y, width, height);
        const double left = 38;
        const double right = 15;
        const double top = 14;
        const double bottom = 34;
        var plotX = x + left;
        var plotY = y + top;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        foreach (var percentage in new[] { 100, 75, 50, 25, 0 })
        {
            var lineY = plotY + ((100 - percentage) / 100d * plotHeight);
            graphics.DrawLine(new XPen(Line, .45), plotX, lineY, plotX + plotWidth, lineY);
            DrawText(graphics, $"{percentage}%", Fonts.Tiny, Muted, x + 4, lineY - 4, 29, 9, XStringFormats.TopRight);
        }

        if (trend.Count == 0 || trend.All(point => !point.Percentage.HasValue))
        {
            DrawText(
                graphics,
                "No measured on-time completions are available for this period.",
                Fonts.BodyBold,
                Muted,
                plotX,
                plotY + (plotHeight / 2) - 7,
                plotWidth,
                16,
                XStringFormats.TopCenter);
            return;
        }

        var slotWidth = plotWidth / Math.Max(1, trend.Count);
        var barWidth = Math.Clamp(slotWidth * .52, 2.5, 34);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(trend.Count / 12d));
        for (var index = 0; index < trend.Count; index++)
        {
            var point = trend[index];
            var centerX = plotX + ((index + .5) * slotWidth);
            if (point.Percentage.HasValue)
            {
                var barHeight = point.Percentage.Value / 100d * plotHeight;
                var barY = plotY + plotHeight - barHeight;
                graphics.DrawRectangle(new XSolidBrush(Steel), centerX - (barWidth / 2), barY, barWidth, barHeight);
                if (trend.Count <= 12)
                    DrawText(
                        graphics,
                        $"{point.Percentage:0}%",
                        Fonts.TinyBold,
                        Steel,
                        centerX - (slotWidth / 2),
                        Math.Max(plotY, barY - 11),
                        slotWidth,
                        9,
                        XStringFormats.TopCenter);
            }

            if (index % labelEvery == 0 || index == trend.Count - 1)
                DrawText(
                    graphics,
                    point.Label,
                    Fonts.Tiny,
                    Muted,
                    centerX - (slotWidth * labelEvery / 2),
                    plotY + plotHeight + 8,
                    slotWidth * labelEvery,
                    18,
                    XStringFormats.TopCenter);
        }
    }

    private static void DrawSectionLabel(XGraphics graphics, string value, double x, double y)
    {
        graphics.DrawRectangle(new XSolidBrush(Red), x, y + 1, 3, 11);
        DrawText(graphics, value, Fonts.Eyebrow, Muted, x + 9, y, 360, 14);
    }

    private static void DrawFooter(XGraphics graphics, PdfPage page, int pageNumber)
    {
        var y = page.Height.Point - 23;
        graphics.DrawLine(new XPen(Line, .5), 32, y - 7, page.Width.Point - 32, y - 7);
        DrawText(graphics, "SON-AERO | Internal estimating performance report", Fonts.Tiny, Muted, 32, y, 400, 10);
        DrawText(graphics, $"Page {pageNumber}", Fonts.TinyBold, Muted, page.Width.Point - 100, y, 68, 10, XStringFormats.TopRight);
    }

    private static void DrawText(
        XGraphics graphics,
        string value,
        XFont font,
        XColor color,
        double x,
        double y,
        double width,
        double height,
        XStringFormat? format = null) => graphics.DrawString(
            value,
            font,
            new XSolidBrush(color),
            new XRect(x, y, width, height),
            format ?? XStringFormats.TopLeft);

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

    private static string Currency(decimal value) => value.ToString("$#,##0");

    private static XColor Color(string hex) => XColor.FromArgb(
        Convert.ToInt32(hex[..2], 16),
        Convert.ToInt32(hex.Substring(2, 2), 16),
        Convert.ToInt32(hex.Substring(4, 2), 16));

    private sealed record Metric(string Label, string Value, XColor Accent, XColor Fill);

    private static class Fonts
    {
        public static readonly XFont Brand = new("Arial", 20, XFontStyleEx.Bold);
        public static readonly XFont Title = new("Arial", 22, XFontStyleEx.Bold);
        public static readonly XFont Subtitle = new("Arial", 9, XFontStyleEx.Regular);
        public static readonly XFont Metric = new("Arial", 12, XFontStyleEx.Bold);
        public static readonly XFont Body = new("Arial", 9, XFontStyleEx.Regular);
        public static readonly XFont BodyBold = new("Arial", 9, XFontStyleEx.Bold);
        public static readonly XFont Small = new("Arial", 8, XFontStyleEx.Regular);
        public static readonly XFont SmallBold = new("Arial", 8, XFontStyleEx.Bold);
        public static readonly XFont Tiny = new("Arial", 6.5, XFontStyleEx.Regular);
        public static readonly XFont TinyBold = new("Arial", 6.5, XFontStyleEx.Bold);
        public static readonly XFont Eyebrow = new("Arial", 7, XFontStyleEx.Bold);
        public static readonly XFont EyebrowLight = new("Arial", 7.5, XFontStyleEx.Bold);
    }
}
