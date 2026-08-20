using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed class ScheduleCalculator
{
    public bool IsWorkingDay(DateOnly date, ScheduleCalendar calendar, IReadOnlySet<DateOnly>? overtimeDates = null)
    {
        return overtimeDates?.Contains(date) == true
            || (calendar.WorkingDays.Contains(date.DayOfWeek) && !calendar.Holidays.Contains(date));
    }

    public DateOnly AddWorkingDaysInclusive(DateOnly start, int workingDays, ScheduleCalendar calendar, IReadOnlySet<DateOnly>? overtimeDates = null)
    {
        if (workingDays <= 0)
        {
            return start;
        }

        var date = start;
        var counted = 0;
        while (true)
        {
            if (IsWorkingDay(date, calendar, overtimeDates))
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

    public int CountWorkingDays(DateOnly start, DateOnly end, ScheduleCalendar calendar, IReadOnlySet<DateOnly>? overtimeDates = null)
    {
        if (end < start)
        {
            return 0;
        }

        var days = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (IsWorkingDay(date, calendar, overtimeDates))
            {
                days++;
            }
        }

        return days;
    }

    public DateOnly PreviousWorkingDay(DateOnly date, ScheduleCalendar calendar)
    {
        var previous = date.AddDays(-1);
        while (!IsWorkingDay(previous, calendar))
        {
            previous = previous.AddDays(-1);
        }

        return previous;
    }

    public decimal CalculateAutomaticProgress(ProjectTask task, ScheduleCalendar calendar, DateOnly today)
    {
        if (task.StartDate is null || task.EndDate is null || today < task.StartDate.Value)
        {
            return 0m;
        }

        var overtimeDates = task.OvertimeDays.Select(day => day.Date).ToHashSet();
        var total = task.EstimatedDuration is > 0
            ? task.EstimatedDuration.Value
            : CountWorkingDays(task.StartDate.Value, task.EndDate.Value, calendar, overtimeDates);
        if (total <= 0)
        {
            return 0m;
        }

        var elapsedThrough = today < task.EndDate.Value ? today : task.EndDate.Value;
        var elapsed = CountWorkingDays(task.StartDate.Value, elapsedThrough, calendar, overtimeDates);
        return Math.Clamp(Math.Min(0.99m, (decimal)elapsed / total), 0m, 0.99m);
    }

    public TaskScheduleStatus CalculateTaskStatus(ProjectTask task, ScheduleCalendar calendar, DateOnly today)
    {
        var durationOverrun = HasDurationOverrun(task, calendar);
        if (task.PercentComplete >= 1m)
        {
            return durationOverrun == true
                ? TaskScheduleStatus.CompletedLate
                : TaskScheduleStatus.Complete;
        }

        var hasStarted = task.PercentComplete > 0m
            || task.StartDateLocked
            || task.StartDate is not null && today >= task.StartDate.Value;
        if (durationOverrun == true && hasStarted)
        {
            return TaskScheduleStatus.Behind;
        }

        if (task.StartDate is not null && today < task.StartDate.Value)
        {
            return TaskScheduleStatus.NotStarted;
        }

        return task.PercentComplete > 0m
            ? TaskScheduleStatus.OnTrack
            : TaskScheduleStatus.NotStarted;
    }

    public bool? HasDurationOverrun(ProjectTask task, ScheduleCalendar calendar)
    {
        var currentDuration = CurrentDuration(task, calendar);
        var originalDuration = OriginalDuration(task, calendar);
        return currentDuration is not null && originalDuration is not null
            ? currentDuration.Value > originalDuration.Value
            : null;
    }

    private int? CurrentDuration(ProjectTask task, ScheduleCalendar calendar)
    {
        if (task.EstimatedDuration is >= 0)
        {
            return task.EstimatedDuration.Value;
        }

        if (task.StartDate is null || task.EndDate is null)
        {
            return null;
        }

        var overtimeDates = task.OvertimeDays.Select(day => day.Date).ToHashSet();
        return CountWorkingDays(task.StartDate.Value, task.EndDate.Value, calendar, overtimeDates);
    }

    private int? OriginalDuration(ProjectTask task, ScheduleCalendar calendar)
    {
        if (task.ActualDuration is >= 0)
        {
            return task.ActualDuration.Value;
        }

        return task.OriginalStartDate is not null && task.OriginalEndDate is not null
            ? CountWorkingDays(task.OriginalStartDate.Value, task.OriginalEndDate.Value, calendar)
            : null;
    }
}
