using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed class ProjectMetricsService(ScheduleCalculator scheduleCalculator)
{
    public async Task RefreshProjectAsync(ProjectTrackerDbContext db, Project project, CancellationToken cancellationToken = default, bool recalculateDates = false)
    {
        var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
        var settings = await db.ScheduleSettings.FindAsync([ScheduleSettings.SingletonId], cancellationToken)
            ?? new ScheduleSettings();
        var calendar = new ScheduleCalendar(settings.GetWorkingDays(), holidays);
        RefreshProject(project, calendar, DateOnly.FromDateTime(DateTime.Today), recalculateDates);
    }

    public void RefreshProject(Project project, ScheduleCalendar calendar, DateOnly today, bool recalculateDates = false)
    {
        DateOnly? nextStart = project.ProgramStart;
        foreach (var task in project.Tasks.OrderBy(task => task.Sequence))
        {
            var overtimeDates = task.OvertimeDays.Select(day => day.Date).ToHashSet();
            if (!task.StartDateLocked && nextStart is not null)
            {
                task.StartDate = NextWorkingDay(nextStart.Value, calendar, overtimeDates);
            }

            if (recalculateDates && task.StartDate is not null && task.EstimatedDuration is > 0)
            {
                task.EndDate = scheduleCalculator.AddWorkingDaysInclusive(task.StartDate.Value, task.EstimatedDuration.Value, calendar, overtimeDates);
            }
            else if (task.StartDate is not null && task.EndDate is not null)
            {
                task.EstimatedDuration = scheduleCalculator.CountWorkingDays(task.StartDate.Value, task.EndDate.Value, calendar, overtimeDates);
            }
            else if (task.StartDate is not null && task.EstimatedDuration is > 0)
            {
                task.EndDate = scheduleCalculator.AddWorkingDaysInclusive(task.StartDate.Value, task.EstimatedDuration.Value, calendar, overtimeDates);
            }

            task.PercentComplete = task.PercentCompleteManual
                ? Math.Clamp(task.PercentComplete, 0m, 1m)
                : CalculateAutomaticPercent(task, calendar, today);
            task.Status = scheduleCalculator.CalculateTaskStatus(task, calendar, today);
            task.UpdatedAt = DateTimeOffset.UtcNow;

            nextStart = task.EndDate?.AddDays(1);
        }

        var activeTasks = project.Tasks.Where(task => !string.IsNullOrWhiteSpace(task.Title)).OrderBy(task => task.Sequence).ToList();
        if (activeTasks.Count > 0)
        {
            project.ProgramStart = activeTasks.Select(task => task.StartDate).Where(date => date is not null).Min();
        }
        project.TargetDelivery = activeTasks.Select(task => task.EndDate).Where(date => date is not null).Max();
        project.Progress = CalculateWeightedProgress(activeTasks);
        project.CurrentTask = activeTasks.FirstOrDefault(task => task.Status != TaskScheduleStatus.Complete)?.Title ?? "Program Complete";
        project.Status = CalculateProjectStatus(activeTasks, project.Progress);
        project.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private decimal CalculateAutomaticPercent(ProjectTask task, ScheduleCalendar calendar, DateOnly today)
    {
        if (task.StartDate is null || task.EndDate is null)
        {
            return 0m;
        }

        if (today < task.StartDate.Value)
        {
            return 0m;
        }

        if (today >= task.EndDate.Value)
        {
            return 1m;
        }

        var overtimeDates = task.OvertimeDays.Select(day => day.Date).ToHashSet();
        var total = scheduleCalculator.CountWorkingDays(task.StartDate.Value, task.EndDate.Value, calendar, overtimeDates);
        if (total <= 0)
        {
            return 0m;
        }

        var elapsed = scheduleCalculator.CountWorkingDays(task.StartDate.Value, today, calendar, overtimeDates);
        return Math.Clamp((decimal)elapsed / total, 0m, 1m);
    }

    private DateOnly NextWorkingDay(DateOnly date, ScheduleCalendar calendar, IReadOnlySet<DateOnly> overtimeDates)
    {
        var next = date;
        while (!scheduleCalculator.IsWorkingDay(next, calendar, overtimeDates))
        {
            next = next.AddDays(1);
        }

        return next;
    }

    private static decimal CalculateWeightedProgress(IReadOnlyCollection<ProjectTask> tasks)
    {
        var weightedTasks = tasks.Where(task => task.EstimatedDuration is > 0).ToList();
        var denominator = weightedTasks.Sum(task => task.EstimatedDuration!.Value);
        if (denominator == 0)
        {
            return tasks.Count > 0 && tasks.All(task => task.PercentComplete >= 1m) ? 1m : 0m;
        }

        var numerator = weightedTasks.Sum(task => task.EstimatedDuration!.Value * task.PercentComplete);
        return Math.Clamp(numerator / denominator, 0m, 1m);
    }

    private static ProjectStatus CalculateProjectStatus(IReadOnlyCollection<ProjectTask> tasks, decimal progress)
    {
        if (tasks.Count == 0)
        {
            return ProjectStatus.NotStarted;
        }

        if (progress >= 1m || tasks.All(task => task.Status == TaskScheduleStatus.Complete))
        {
            return ProjectStatus.Complete;
        }

        if (tasks.Any(task => task.Status == TaskScheduleStatus.Behind))
        {
            return ProjectStatus.Behind;
        }

        if (tasks.All(task => task.Status == TaskScheduleStatus.NotStarted))
        {
            return ProjectStatus.NotStarted;
        }

        return ProjectStatus.OnTrack;
    }
}
