using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed class ProjectMetricsService(ScheduleCalculator scheduleCalculator)
{
    public async Task RefreshProjectAsync(ProjectTrackerDbContext db, Project project, CancellationToken cancellationToken = default)
    {
        var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
        RefreshProject(project, holidays, DateOnly.FromDateTime(DateTime.Today));
    }

    public void RefreshProject(Project project, IReadOnlySet<DateOnly> holidays, DateOnly today)
    {
        DateOnly? nextStart = null;
        foreach (var task in project.Tasks.OrderBy(task => task.Sequence))
        {
            if (!task.StartDateLocked && nextStart is not null)
            {
                task.StartDate = NextWorkingDay(nextStart.Value, holidays);
            }

            if (task.StartDate is not null && task.EndDate is not null)
            {
                task.EstimatedDuration = scheduleCalculator.CountWorkingDays(task.StartDate.Value, task.EndDate.Value, holidays);
            }
            else if (task.StartDate is not null && task.EstimatedDuration is > 0)
            {
                task.EndDate = scheduleCalculator.AddWorkingDaysInclusive(task.StartDate.Value, task.EstimatedDuration.Value, holidays);
            }

            task.PercentComplete = task.PercentCompleteManual
                ? Math.Clamp(task.PercentComplete, 0m, 1m)
                : CalculateAutomaticPercent(task, holidays, today);
            task.Status = scheduleCalculator.CalculateTaskStatus(task, holidays, today);
            task.UpdatedAt = DateTimeOffset.UtcNow;

            nextStart = task.EndDate?.AddDays(1);
        }

        var activeTasks = project.Tasks.Where(task => !string.IsNullOrWhiteSpace(task.Title)).OrderBy(task => task.Sequence).ToList();
        project.ProgramStart = activeTasks.Select(task => task.StartDate).Where(date => date is not null).Min();
        project.TargetDelivery = activeTasks.Select(task => task.EndDate).Where(date => date is not null).Max();
        project.Progress = CalculateWeightedProgress(activeTasks);
        project.CurrentTask = activeTasks.FirstOrDefault(task => task.Status != TaskScheduleStatus.Complete)?.Title ?? "Program Complete";
        project.Status = CalculateProjectStatus(activeTasks, project.Progress);
        project.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private decimal CalculateAutomaticPercent(ProjectTask task, IReadOnlySet<DateOnly> holidays, DateOnly today)
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

        var total = scheduleCalculator.CountWorkingDays(task.StartDate.Value, task.EndDate.Value, holidays);
        if (total <= 0)
        {
            return 0m;
        }

        var elapsed = scheduleCalculator.CountWorkingDays(task.StartDate.Value, today, holidays);
        return Math.Clamp((decimal)elapsed / total, 0m, 1m);
    }

    private DateOnly NextWorkingDay(DateOnly date, IReadOnlySet<DateOnly> holidays)
    {
        var next = date;
        while (!scheduleCalculator.IsWorkingDay(next, holidays))
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
