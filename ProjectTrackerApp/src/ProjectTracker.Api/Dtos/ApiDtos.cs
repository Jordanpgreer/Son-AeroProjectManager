using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Dtos;

public sealed record UserDto(string AccountName, string DisplayName, string Role, bool CanEdit, bool IsAdmin);

public sealed record DashboardDto(
    int ActiveProjects,
    int OnTrackProjects,
    int BehindProjects,
    decimal AverageProgress,
    DateOnly? NearestDelivery,
    IReadOnlyList<ProjectSummaryDto> Projects);

public sealed record ProjectSummaryDto(
    int Id,
    string ProgramName,
    string? ProgramManager,
    string? CustomerName,
    string? SalesOrderNumber,
    string? CurrentTask,
    decimal Progress,
    DateOnly? TargetDelivery,
    DateOnly? FinalCompletionDate,
    int? DaysLeft,
    ProjectStatus Status,
    int TaskCount,
    int BehindTaskCount);

public sealed record ProjectDetailDto(
    int Id,
    string ProgramName,
    string? ProgramManager,
    string? CustomerName,
    string? SalesOrderNumber,
    string? CurrentTask,
    DateOnly? ProgramStart,
    DateOnly? TargetDelivery,
    decimal Progress,
    ProjectStatus Status,
    IReadOnlyList<ProjectTaskDto> Tasks);

public sealed record ProjectTaskDto(
    int Id,
    int ProjectId,
    int Sequence,
    string? ExternalTaskId,
    string Title,
    string? Phase,
    string? WorkStation,
    int? DependencyTaskId,
    DateOnly? StartDate,
    bool StartDateLocked,
    DateOnly? OriginalStartDate,
    DateOnly? EndDate,
    DateOnly? OriginalEndDate,
    int? EstimatedDuration,
    int? ActualDuration,
    decimal PercentComplete,
    bool PercentCompleteManual,
    TaskScheduleStatus Status,
    string? Notes,
    IReadOnlyList<TaskOvertimeDayDto> OvertimeDays);

public sealed record ProjectUpsertDto(
    string ProgramName,
    string? ProgramManager,
    string? CustomerName,
    string? SalesOrderNumber);

public sealed record ProjectCreateDto(
    string ProgramName,
    string? ProgramManager,
    string? CustomerName,
    string? SalesOrderNumber,
    DateOnly? ProgramStart,
    int? TemplateProjectId);

public sealed record TaskUpsertDto(
    int Sequence,
    string? ExternalTaskId,
    string Title,
    string? Phase,
    string? WorkStation,
    int? DependencyTaskId,
    DateOnly? StartDate,
    bool StartDateLocked,
    DateOnly? OriginalStartDate,
    DateOnly? EndDate,
    DateOnly? OriginalEndDate,
    int? EstimatedDuration,
    int? ActualDuration,
    decimal PercentComplete,
    bool PercentCompleteManual,
    string? Notes,
    IReadOnlyList<TaskOvertimeDayUpsertDto>? OvertimeDays);

public sealed record TaskOvertimeDayDto(int Id, DateOnly Date, string? Note);

public sealed record TaskOvertimeDayUpsertDto(DateOnly Date, string? Note);

public sealed record ScheduleSettingsDto(
    IReadOnlyList<DayOfWeek> WorkingDays,
    DateTimeOffset UpdatedAt);

public sealed record ScheduleSettingsUpsertDto(IReadOnlyList<DayOfWeek> WorkingDays);

public sealed record HolidayDto(int Id, DateOnly Date, string Name);

public sealed record HolidayUpsertDto(DateOnly Date, string Name);

public sealed record WorkCenterDto(int Id, string Name);

public sealed record WorkCenterUpsertDto(string Name);

public sealed record ImportWorkbookRequest(string? Path, bool ReplaceExisting = true);

public sealed record ImportWorkbookResult(int ProjectCount, int TaskCount, int HolidayCount);

