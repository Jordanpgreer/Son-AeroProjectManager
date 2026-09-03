using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Api.Mapping;

public static class ProjectDtoMapper
{
    public static ProjectSummaryDto ToSummaryDto(Project project)
    {
        var schedule = ProjectScheduleContext.From(project);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysLeft = project.TargetDelivery is null ? (int?)null : project.TargetDelivery.Value.DayNumber - today.DayNumber;
        var finalCompletionDate = project.Status == ProjectStatus.Complete ? project.CompletedOn : null;
        var recentProjectNote = ProjectNoteService.GetMostRecent(project);
        var recentNote = recentProjectNote is null
            ? null
            : new ProjectNoteDto(
                recentProjectNote.Task.Notes!.Trim(),
                recentProjectNote.Task.Title,
                recentProjectNote.UpdatedAt);

        return new ProjectSummaryDto(
            project.Id,
            project.Version,
            project.ProgramName,
            project.ProgramManager,
            project.Engineer,
            project.SalesPerson,
            project.CustomerName,
            project.SalesOrderNumber,
            project.SalesOrderUrl,
            project.JobNumber,
            project.JobUrl,
            project.RequiredQuantity,
            project.JobQuantity,
            project.RequiredQuantitySource,
            project.JobQuantitySource,
            project.CurrentTask,
            project.PriorityRank,
            project.Progress,
            project.TargetDelivery,
            finalCompletionDate,
            daysLeft,
            project.DaysBehind,
            project.Status,
            project.Tasks.Count,
            project.Tasks.Count(task => task.Status is TaskScheduleStatus.Behind or TaskScheduleStatus.CompletedLate),
            recentNote,
            schedule.PlannedStart,
            schedule.PlannedFinish,
            schedule.ActualStart,
            schedule.ActualFinish,
            schedule.VarianceDays,
            schedule.Performance);
    }

    public static ProjectDetailDto ToDetailDto(Project project)
    {
        var schedule = ProjectScheduleContext.From(project);
        var missingImportFields = ProjectImportCompletion.GetMissingFields(project)
            .Select(field => new ProjectMissingFieldDto(field.Key, field.Label))
            .ToList();
        return new ProjectDetailDto(
            project.Id,
            project.Version,
            project.ProgramName,
            project.ProgramManager,
            project.Engineer,
            project.SalesPerson,
            project.CustomerName,
            project.SalesOrderNumber,
            project.SalesOrderUrl,
            project.JobNumber,
            project.JobUrl,
            project.RequiredQuantity,
            project.JobQuantity,
            project.RequiredQuantitySource,
            project.JobQuantitySource,
            project.QuantityLastSyncProvider,
            project.QuantityLastSyncedAt,
            project.CurrentTask,
            project.ProgramStart,
            project.TargetDelivery,
            project.CompletedOn,
            project.Progress,
            project.Status,
            project.DaysBehind,
            project.Tasks.OrderBy(task => task.Sequence).Select(ToTaskDto).ToList(),
            schedule.PlannedStart,
            schedule.PlannedFinish,
            schedule.ActualStart,
            schedule.ActualFinish,
            schedule.VarianceDays,
            schedule.Performance,
            missingImportFields.Count > 0,
            missingImportFields);
    }

    public static ProjectTaskDto ToTaskDto(ProjectTask task) => new(
        task.Id,
        task.Version,
        task.ProjectId,
        task.Sequence,
        task.ExternalTaskId,
        task.Title,
        task.Phase,
        task.WorkStation,
        task.DependencyTaskId,
        task.StartDate,
        task.StartDateLocked,
        task.OriginalStartDate,
        task.EndDate,
        task.OriginalEndDate,
        task.EstimatedDuration,
        task.ActualDuration,
        task.PercentComplete,
        task.PercentCompleteManual,
        task.Status,
        task.Notes,
        task.OvertimeDays.OrderBy(day => day.Date).Select(day => new TaskOvertimeDayDto(day.Id, day.Date, day.Note)).ToList());

    public static ProjectMessageDto ToMessageDto(ProjectMessage message) => new(
        message.Id,
        message.ProjectId,
        message.AuthorAccountName,
        message.AuthorDisplayName,
        message.Body,
        message.CreatedAt);

    public static ProjectAuditEntryDto ToAuditEntryDto(ProjectAuditEntry entry) => new(
        entry.Id,
        entry.ProjectId,
        entry.ProjectTaskId,
        entry.Action,
        entry.Summary,
        ProjectAuditService.ReadChanges(entry.ChangesJson)
            .Select(change => new ProjectAuditChangeDto(change.Field, change.OldValue, change.NewValue))
            .ToList(),
        entry.ChangedByAccountName,
        entry.ChangedByDisplayName,
        entry.ChangedAt);
}
