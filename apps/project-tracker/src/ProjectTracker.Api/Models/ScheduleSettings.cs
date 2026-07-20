namespace ProjectTracker.Api.Models;

public sealed class ScheduleSettings
{
    public const int SingletonId = 1;
    public const int DefaultWorkingDaysMask = 30; // Monday through Thursday.

    public int Id { get; set; } = SingletonId;
    public int WorkingDaysMask { get; set; } = DefaultWorkingDaysMask;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public HashSet<DayOfWeek> GetWorkingDays() => Enum.GetValues<DayOfWeek>()
        .Where(day => (WorkingDaysMask & (1 << (int)day)) != 0)
        .ToHashSet();

    public static int ToMask(IEnumerable<DayOfWeek> days) => days
        .Distinct()
        .Aggregate(0, (mask, day) => mask | (1 << (int)day));
}
