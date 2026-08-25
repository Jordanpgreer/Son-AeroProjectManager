namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingHistoryPeriod(
    string Key,
    string Label,
    DateTime? Start,
    DateTime? End);

public static class EstimatingHistoryPeriods
{
    public static EstimatingHistoryPeriod Dashboard(string? value, DateTime today) =>
        Normalize(value) switch
        {
            "month" => Month(today),
            "all" => new("all", "All time", null, null),
            _ => Week(today)
        };

    public static EstimatingHistoryPeriod Report(string? value, DateTime today) =>
        Normalize(value) switch
        {
            "month" => Month(today),
            "year" => new(
                "year",
                $"{today.Year}",
                new DateTime(today.Year, 1, 1),
                new DateTime(today.Year + 1, 1, 1)),
            _ => Week(today)
        };

    public static bool IsValidReportPeriod(string? value) =>
        Normalize(value) is "week" or "month" or "year";

    public static bool Includes(Models.EstimatingQuoteHistoryRecord record, EstimatingHistoryPeriod period) =>
        record.IsCompleted
        && (!period.Start.HasValue || record.EstimatingCompletionDate >= period.Start.Value)
        && (!period.End.HasValue || record.EstimatingCompletionDate < period.End.Value);

    private static EstimatingHistoryPeriod Week(DateTime today)
    {
        var start = today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return new("week", "This week", start, start.AddDays(7));
    }

    private static EstimatingHistoryPeriod Month(DateTime today)
    {
        var start = new DateTime(today.Year, today.Month, 1);
        return new("month", "This month", start, start.AddMonths(1));
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
