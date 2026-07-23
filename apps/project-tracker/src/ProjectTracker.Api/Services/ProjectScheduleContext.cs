using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record ProjectScheduleContext(
    DateOnly? PlannedStart,
    DateOnly? PlannedFinish,
    DateOnly? ActualStart,
    DateOnly? ActualFinish,
    int? VarianceDays,
    string? Performance)
{
    public static ProjectScheduleContext From(Project project)
    {
        var plannedStart = Min(project.Tasks.Select(task => task.OriginalStartDate)) ?? project.ProgramStart;
        var plannedFinish = Max(project.Tasks.Select(task => task.OriginalEndDate)) ?? project.TargetDelivery;
        var actualStart = Min(project.Tasks.Select(task => task.StartDate)) ?? project.ProgramStart;
        var actualFinish = project.Status == ProjectStatus.Complete
            ? project.CompletedOn ?? Max(project.Tasks.Select(task => task.EndDate))
            : null;
        var variance = plannedFinish is not null && actualFinish is not null
            ? actualFinish.Value.DayNumber - plannedFinish.Value.DayNumber
            : (int?)null;
        var performance = variance switch
        {
            < 0 => $"{Math.Abs(variance.Value)} days ahead",
            > 0 => $"{variance.Value} days behind",
            0 => "On schedule",
            _ => null
        };

        return new ProjectScheduleContext(plannedStart, plannedFinish, actualStart, actualFinish, variance, performance);
    }

    private static DateOnly? Min(IEnumerable<DateOnly?> dates)
    {
        var values = dates.Where(date => date is not null).Select(date => date!.Value).ToList();
        return values.Count == 0 ? null : values.Min();
    }

    private static DateOnly? Max(IEnumerable<DateOnly?> dates)
    {
        var values = dates.Where(date => date is not null).Select(date => date!.Value).ToList();
        return values.Count == 0 ? null : values.Max();
    }
}
