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
        var projectState = ProjectComputedState.Capture(project);
        if (project.CompletedOn is not null)
        {
            project.Progress = 1m;
            project.Status = ProjectStatus.Complete;
            project.CurrentTask = "Program Complete";
            UpdateProjectVersion(project, projectState);
            return;
        }

        DateOnly? nextStart = project.ProgramStart;
        var scheduledTasks = new Dictionary<int, ProjectTask>();
        foreach (var task in project.Tasks.OrderBy(task => task.Sequence))
        {
            var taskState = TaskComputedState.Capture(task);
            var overtimeDates = task.OvertimeDays.Select(day => day.Date).ToHashSet();
            var dependencyStart = GetDependencyStart(task, scheduledTasks);
            var calculatedStart = dependencyStart ?? nextStart;
            if (!task.StartDateLocked && calculatedStart is not null)
            {
                task.StartDate = NextWorkingDay(calculatedStart.Value, calendar, overtimeDates);
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
            UpdateTaskVersion(task, taskState);

            nextStart = task.EndDate?.AddDays(1);
            scheduledTasks[task.Id] = task;
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
        if (project.Status == ProjectStatus.Complete)
        {
            project.CompletedOn = activeTasks
                .Select(task => task.EndDate)
                .Where(date => date is not null)
                .Max()
                ?? today;
        }
        UpdateProjectVersion(project, projectState);
    }

    private static void UpdateTaskVersion(ProjectTask task, TaskComputedState before)
    {
        if (before == TaskComputedState.Capture(task))
        {
            return;
        }

        task.Version++;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void UpdateProjectVersion(Project project, ProjectComputedState before)
    {
        if (before == ProjectComputedState.Capture(project))
        {
            return;
        }

        project.Version++;
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

    private static DateOnly? GetDependencyStart(ProjectTask task, IReadOnlyDictionary<int, ProjectTask> scheduledTasks)
    {
        if (task.DependencyTaskId is null)
        {
            return null;
        }

        return scheduledTasks.TryGetValue(task.DependencyTaskId.Value, out var dependency) && dependency.EndDate is not null
            ? dependency.EndDate.Value.AddDays(1)
            : null;
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

    private sealed record TaskComputedState(
        DateOnly? StartDate,
        DateOnly? EndDate,
        int? EstimatedDuration,
        decimal PercentComplete,
        TaskScheduleStatus Status)
    {
        public static TaskComputedState Capture(ProjectTask task) => new(
            task.StartDate,
            task.EndDate,
            task.EstimatedDuration,
            task.PercentComplete,
            task.Status);
    }

    private sealed record ProjectComputedState(
        DateOnly? ProgramStart,
        DateOnly? TargetDelivery,
        DateOnly? CompletedOn,
        decimal Progress,
        ProjectStatus Status,
        string? CurrentTask)
    {
        public static ProjectComputedState Capture(Project project) => new(
            project.ProgramStart,
            project.TargetDelivery,
            project.CompletedOn,
            project.Progress,
            project.Status,
            project.CurrentTask);
    }
}
