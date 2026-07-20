namespace ProjectTracker.Api.Services;

public sealed record ScheduleCalendar(
    IReadOnlySet<DayOfWeek> WorkingDays,
    IReadOnlySet<DateOnly> Holidays)
{
    public static ScheduleCalendar Default { get; } = new(
        new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday
        },
        new HashSet<DateOnly>());
}
