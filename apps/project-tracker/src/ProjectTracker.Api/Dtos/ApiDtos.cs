using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Dtos;

public sealed record UserDto(
    string AccountName,
    string DisplayName,
    bool IsRegistered,
    bool IsActive,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Permissions,
    bool CanEdit,
    bool IsAdmin,
    bool WalkthroughEnabled,
    bool AssistantEnabled,
    string AssistantName,
    AccessPreviewInfoDto? Preview);

public sealed record AccessPreviewInfoDto(
    string ActorAccountName,
    string TargetKey,
    string TargetKind,
    string TargetTitle,
    bool ReadOnly,
    string EndUrl);

public sealed record RegisteredUserDto(
    int Id,
    string AccountName,
    string DisplayName,
    bool IsActive,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<int> GroupIds);

public sealed record AccessGroupDto(
    int Id,
    string Name,
    string? Description,
    bool IsSystemGroup,
    IReadOnlyList<string> Permissions,
    int UserCount);

public sealed record PermissionDefinitionDto(
    string Key,
    string Label,
    string Description,
    string Category,
    string ModuleKey = "project-tracker",
    string ModuleName = "Project Tracker");

public sealed record AccessOverviewDto(
    IReadOnlyList<RegisteredUserDto> Users,
    IReadOnlyList<AccessGroupDto> Groups,
    IReadOnlyList<PermissionDefinitionDto> Permissions);

public sealed record ModuleAccessRoleDto(
    string Role,
    IReadOnlyList<PermissionDefinitionDto> Permissions);

public sealed record ModuleAccessCatalogEntryDto(
    string Key,
    string Name,
    IReadOnlyList<ModuleAccessRoleDto> Roles);

public sealed record UserModuleAccessDto(
    string ModuleKey,
    bool Enabled,
    string? Role,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? UpdatedAt);

public sealed record ModuleAccessUserDto(
    int UserId,
    string AccountName,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<UserModuleAccessDto> Modules);

public sealed record ModuleAccessUpdateDto(bool Enabled, string? Role);

public sealed record ArchivedProjectDto(
    int Id,
    long Version,
    string ProgramName,
    string? CustomerName,
    string? SalesOrderNumber,
    DateTimeOffset DeletedAt,
    string? DeletedByDisplayName);

public sealed record ArchivedProjectPermanentDeleteDto(
    long Version,
    string Confirmation);

public sealed record RegisteredUserUpsertDto(string AccountName, string? DisplayName, bool IsActive, IReadOnlyList<int> GroupIds);

public sealed record UserGroupAssignmentDto(IReadOnlyList<int> GroupIds);

public sealed record AccessGroupUpsertDto(string? Name, string? Description, bool IsSystemGroup, IReadOnlyList<string>? Permissions);

public sealed record EstimatingHistoryImportAccessUpdateDto(bool Enabled);

public sealed record AccessGroupDeleteConflictDto(
    string Code,
    string Message,
    int UserCount);

public sealed record DashboardDto(
    int ActiveProjects,
    int OnTrackProjects,
    int BehindProjects,
    decimal AverageProgress,
    DateOnly? NearestDelivery,
    IReadOnlyList<ProjectSummaryDto> Projects);

public sealed record TrackerPreviewDto(
    int ActiveProjects,
    int OnTrack,
    int Behind,
    double AverageProgress,
    IReadOnlyList<TrackerPreviewRowDto> Programs);

public sealed record TrackerPreviewRowDto(string Name, double Progress, string Status);

public sealed record ProjectNoteDto(string Note, string Step, DateTimeOffset At);

public sealed record ProjectSummaryDto(
    int Id,
    long Version,
    string ProgramName,
    string? ProgramManager,
    string? Engineer,
    string? SalesPerson,
    string? CustomerName,
    string? SalesOrderNumber,
    string? SalesOrderUrl,
    string? JobNumber,
    string? JobUrl,
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    string? RequiredQuantitySource,
    string? JobQuantitySource,
    string? CurrentTask,
    int? PriorityRank,
    decimal Progress,
    DateOnly? TargetDelivery,
    DateOnly? FinalCompletionDate,
    int? DaysLeft,
    int? DaysBehind,
    ProjectStatus Status,
    int TaskCount,
    int BehindTaskCount,
    ProjectNoteDto? RecentNote,
    DateOnly? PlannedStart,
    DateOnly? PlannedFinish,
    DateOnly? ActualStart,
    DateOnly? ActualFinish,
    int? ScheduleVarianceDays,
    string? SchedulePerformance);

public sealed record ProjectDetailDto(
    int Id,
    long Version,
    string ProgramName,
    string? ProgramManager,
    string? Engineer,
    string? SalesPerson,
    string? CustomerName,
    string? SalesOrderNumber,
    string? SalesOrderUrl,
    string? JobNumber,
    string? JobUrl,
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    string? RequiredQuantitySource,
    string? JobQuantitySource,
    string? QuantityLastSyncProvider,
    DateTimeOffset? QuantityLastSyncedAt,
    string? CurrentTask,
    DateOnly? ProgramStart,
    DateOnly? TargetDelivery,
    DateOnly? CompletedOn,
    decimal Progress,
    ProjectStatus Status,
    int? DaysBehind,
    IReadOnlyList<ProjectTaskDto> Tasks,
    DateOnly? PlannedStart,
    DateOnly? PlannedFinish,
    DateOnly? ActualStart,
    DateOnly? ActualFinish,
    int? ScheduleVarianceDays,
    string? SchedulePerformance,
    bool RequiresImportCompletion,
    IReadOnlyList<ProjectMissingFieldDto> MissingImportFields);

public sealed record ProjectMissingFieldDto(string Key, string Label);

public sealed record ProjectVersionDto(int Id, long Version, DateTimeOffset UpdatedAt);

public sealed record ProjectTaskDto(
    int Id,
    long Version,
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
    string? Engineer,
    string? SalesPerson,
    string? CustomerName,
    string? SalesOrderNumber,
    string? SalesOrderUrl,
    string? JobNumber,
    string? JobUrl,
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    long Version);

public sealed record ProjectCreateDto(
    string ProgramName,
    string? ProgramManager,
    string? Engineer,
    string? SalesPerson,
    string? CustomerName,
    string? SalesOrderNumber,
    string? SalesOrderUrl,
    string? JobNumber,
    string? JobUrl,
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    DateOnly? ProgramStart,
    int? TemplateProjectId);

public sealed record ProjectQuantitySyncRequestDto(
    long Version,
    bool PreserveQuantities = false);

public sealed record ProjectRoutingOverrideRequestDto(long Version);

public sealed record ProjectQuantitySyncResultDto(
    ProjectDetailDto Project,
    string Provider,
    IReadOnlyList<string> UpdatedFields,
    IReadOnlyList<string> RetainedFields,
    IReadOnlyList<string> Warnings,
    int RoutingStepsAdded = 0,
    int RoutingStepsUpdated = 0,
    int ArdaOnlyOperationsRetained = 0,
    int RoutingOperationsRemoved = 0,
    bool ExistingOperationsPreserved = false,
    int OperationProgressUpdated = 0);

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
    IReadOnlyList<TaskOvertimeDayUpsertDto>? OvertimeDays,
    long Version,
    long ProjectVersion);

public sealed record TaskOvertimeDayDto(int Id, DateOnly Date, string? Note);

public sealed record TaskOvertimeDayUpsertDto(DateOnly Date, string? Note);

public sealed record ScheduleSettingsDto(
    IReadOnlyList<DayOfWeek> WorkingDays,
    DateTimeOffset UpdatedAt);

public sealed record ScheduleSettingsUpsertDto(IReadOnlyList<DayOfWeek> WorkingDays);

public sealed record WalkthroughSettingsDto(
    bool Enabled,
    bool AssistantEnabled,
    string AssistantName,
    IReadOnlyList<string> AssistantIdleModules,
    int AssistantIdleDelayMinutes,
    DateTimeOffset UpdatedAt);

public sealed record WalkthroughSettingsUpsertDto(
    bool Enabled,
    bool AssistantEnabled,
    string? AssistantName,
    IReadOnlyList<string>? AssistantIdleModules,
    int? AssistantIdleDelayMinutes);

public sealed record WalkthroughBootstrapDto(
    bool Enabled,
    string DisplayName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Permissions,
    string? ExitUrl);

public sealed record HolidayDto(int Id, DateOnly Date, string Name);

public sealed record HolidayUpsertDto(DateOnly Date, string Name);

public sealed record WorkCenterDto(int Id, string Name);

public sealed record WorkCenterUpsertDto(string Name);

public sealed record ProjectPriorityDto(int PriorityRank, long Version);

public sealed record ProjectActionDto(long Version);

public sealed record ConcurrencyConflictDto(
    string Code,
    string Message,
    string ResourceType,
    int ResourceId);

public sealed record OperationDependentDto(int Id, int Sequence, string Title);

public sealed record OperationDependencyConflictDto(
    string Code,
    string Message,
    int OperationId,
    IReadOnlyList<OperationDependentDto> Dependents);

public sealed record ProjectMessageDto(
    int Id,
    int ProjectId,
    string AuthorAccountName,
    string AuthorDisplayName,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record ProjectMessageCreateDto(string Body);

public sealed record MentionableUserDto(string AccountName, string DisplayName, string MentionHandle);

public sealed record UserNotificationDto(
    int Id,
    NotificationKind Kind,
    int ProjectId,
    string ProjectName,
    int? ProjectTaskId,
    string? OperationName,
    string ActorAccountName,
    string ActorDisplayName,
    string Title,
    string BodyPreview,
    DateOnly? ScheduledDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationCountDto(int UnreadCount);

public sealed record ProjectNotificationPreferenceDto(
    int ProjectId,
    bool Enabled,
    bool IsAutomatic,
    IReadOnlyList<string> AssignedRoles);

public sealed record ProjectNotificationPreferenceUpdateDto(bool Enabled);

public sealed record OperationScheduleResponseDto(string Response);

public sealed record ProjectAuditChangeDto(string Field, string? OldValue, string? NewValue);

public sealed record ProjectAuditEntryDto(
    int Id,
    int ProjectId,
    int? ProjectTaskId,
    string Action,
    string Summary,
    IReadOnlyList<ProjectAuditChangeDto> Changes,
    string ChangedByAccountName,
    string ChangedByDisplayName,
    DateTimeOffset ChangedAt);

public sealed record ImportWorkbookRequest(string? Path, bool ReplaceExisting = true);

public sealed record ImportWorkbookResult(int ProjectCount, int TaskCount, int HolidayCount);

public sealed record ImportIssueDto(
    string Sheet,
    int Row,
    string? Column,
    string Message);

public sealed record ImportChangeDto(
    string Sheet,
    int Row,
    string RecordKey,
    string ChangeType,
    string Field,
    string? CurrentValue,
    string? UploadedValue);

public sealed record ImportValidationResultDto(
    string ReviewId,
    DateTimeOffset ExpiresAt,
    string FileName,
    int ProjectRows,
    int OperationRows,
    int ProjectsAdded,
    int ProjectsUpdated,
    int OperationsAdded,
    int OperationsUpdated,
    int ChangeCount,
    IReadOnlyList<ImportIssueDto> Errors,
    IReadOnlyList<ImportChangeDto> Changes,
    string ReviewWorkbookUrl,
    bool CanConfirm,
    string WorkbookFormat,
    int ProjectsRequiringCompletion);

public sealed record ImportApplyResultDto(
    int ProjectsAdded,
    int ProjectsUpdated,
    int OperationsAdded,
    int OperationsUpdated,
    int ChangeCount);

