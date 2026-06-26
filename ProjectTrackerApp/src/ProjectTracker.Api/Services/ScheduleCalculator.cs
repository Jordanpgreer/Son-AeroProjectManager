using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed class ScheduleCalculator
{
    public bool IsWorkingDay(DateOnly date, IReadOnlySet<DateOnly> holidays)
    {
        return date.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday
            && !holidays.Contains(date);
    }

    public DateOnly AddWorkingDaysInclusive(DateOnly start, int workingDays, IReadOnlySet<DateOnly> holidays)
    {
        if (workingDays <= 0)
        {
            return start;
        }

        var date = start;
        var counted = 0;
        while (true)
        {
            if (IsWorkingDay(date, holidays))
            {
                counted++;
                if (counted == workingDays)
                {
                    return date;
                }
            }

            date = date.AddDays(1);
        }
    }

    public int CountWorkingDays(DateOnly start, DateOnly end, IReadOnlySet<DateOnly> holidays)
    {
        if (end < start)
        {
            return 0;
        }

        var days = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (IsWorkingDay(date, holidays))
            {
                days++;
            }
        }

        return days;
    }

    public TaskScheduleStatus CalculateTaskStatus(ProjectTask task, IReadOnlySet<DateOnly> holidays, DateOnly today)
    {
        if (task.PercentComplete >= 1m)
        {
            return TaskScheduleStatus.Complete;
        }

        if (task.StartDate is null || task.EndDate is null)
        {
            if (task.Status == TaskScheduleStatus.Behind)
            {
                return TaskScheduleStatus.Behind;
            }

            return task.PercentComplete > 0m ? TaskScheduleStatus.OnTrack : TaskScheduleStatus.NotStarted;
        }

        if (today < task.StartDate.Value)
        {
            return TaskScheduleStatus.NotStarted;
        }

        var total = CountWorkingDays(task.StartDate.Value, task.EndDate.Value, holidays);
        if (total <= 0)
        {
            return task.PercentComplete > 0m ? TaskScheduleStatus.OnTrack : TaskScheduleStatus.Behind;
        }

        var elapsedThrough = today < task.EndDate.Value ? today : task.EndDate.Value;
        var elapsed = CountWorkingDays(task.StartDate.Value, elapsedThrough, holidays);
        var expectedProgress = (decimal)elapsed / total;

        return task.PercentComplete >= expectedProgress ? TaskScheduleStatus.OnTrack : TaskScheduleStatus.Behind;
    }
}
