namespace EstimatingDashboard.Api.Services;

internal static class EstimatingWorkdays
{
    public static int Inclusive(DateTime start, DateTime end)
    {
        var elapsedDays = (end.Date - start.Date).Days + 1;
        if (elapsedDays <= 0) return 0;

        var weekdays = elapsedDays / 7 * 5;
        for (var offset = 0; offset < elapsedDays % 7; offset++)
        {
            var day = (DayOfWeek)(((int)start.DayOfWeek + offset) % 7);
            if (day is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) weekdays++;
        }
        return weekdays;
    }
}
