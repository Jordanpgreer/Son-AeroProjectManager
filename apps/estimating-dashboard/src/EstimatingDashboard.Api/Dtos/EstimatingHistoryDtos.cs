namespace EstimatingDashboard.Api.Dtos;

public sealed record EstimatingHistoryRowDto(
    int Id,
    string SourceId,
    int QuoteNumber,
    string Customer,
    string? CustomerContact,
    string SalesPerson,
    string QuoteStatus,
    string? RfqReferenceNumber,
    string EstimatingRep,
    decimal TotalValue,
    DateTime? RfqDueDate,
    DateTime? DateToEstimating,
    string? Issues,
    string? QuoteOnTrack,
    string? QuoteComplexity,
    int NumberOfParts,
    string? EstimatingStatus,
    DateTime? EstimatingCompletionDate,
    string OnTimeStatus,
    int DaysLate,
    int? Workdays,
    string? CompletedMonth,
    int? CompletedYear,
    int? CompletedWeekOfMonth,
    string? CompletedMonthAndWeek,
    bool IsCompleted,
    int? CompletedWeekOfYear,
    bool IsOnTime,
    decimal? OnTimeRatio);

public sealed record EstimatingHistoryPageDto(
    IReadOnlyList<EstimatingHistoryRowDto> Records,
    int Total,
    int Page,
    int PageSize);

public sealed record EstimatingHistoryFilterOptionsDto(
    IReadOnlyList<string> Estimators,
    IReadOnlyList<string> SalesPersons,
    IReadOnlyList<string> Customers,
    IReadOnlyList<string> QuoteStatuses);

public sealed record EstimatingHistoryUserStatsDto(
    string Estimator,
    int InQueue,
    int CompletedThisWeek,
    int CompletedThisMonth,
    int CompletedAllTime,
    decimal TotalQuoteValue,
    decimal CompletedQuoteValue,
    double? AverageCompletionWorkdays,
    int CompletedInPeriod,
    decimal CompletedValueInPeriod,
    int OnTimeInPeriod,
    int LateInPeriod,
    double? AverageCompletionWorkdaysInPeriod);

public sealed record EstimatingHistoryDepartmentStatsDto(
    int InQueue,
    int CompletedThisWeek,
    int CompletedThisMonth,
    int CompletedAllTime,
    decimal TotalQuoteValue,
    decimal CompletedQuoteValue,
    double? AverageCompletionWorkdays,
    int CompletedInPeriod,
    decimal CompletedValueInPeriod,
    int OnTimeInPeriod,
    int LateInPeriod,
    double? AverageCompletionWorkdaysInPeriod);

public sealed record EstimatingHistoryDashboardDto(
    DateTimeOffset GeneratedAt,
    string Period,
    string PeriodLabel,
    DateTime? PeriodStart,
    DateTime? PeriodEnd,
    bool IsTeamView,
    EstimatingHistoryDepartmentStatsDto Department,
    IReadOnlyList<EstimatingHistoryUserStatsDto> Users);

public sealed record EstimatingHistoryImportIssueDto(
    int Row,
    string? Column,
    string Message);

public sealed record EstimatingHistoryImportChangeDto(
    int Row,
    string SourceId,
    int QuoteNumber,
    string Customer,
    string ChangeType);

public sealed record EstimatingHistoryImportValidationDto(
    Guid ReviewId,
    DateTimeOffset ExpiresAt,
    string FileName,
    int TotalRows,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedRecords,
    int ErrorRows,
    IReadOnlyList<EstimatingHistoryImportIssueDto> Errors,
    IReadOnlyList<EstimatingHistoryImportChangeDto> Changes,
    bool CanApply);

public sealed record EstimatingHistoryImportApplyDto(bool ContinueWithErrors = false);

public sealed record EstimatingHistoryImportApplyResultDto(
    Guid BatchId,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedRecords,
    int SkippedRows);

public sealed record EstimatingQuoteAuditChangeDto(
    string FieldName,
    string? OldValue,
    string? NewValue);

public sealed record EstimatingQuoteAuditEventDto(
    Guid ImportBatchId,
    string Action,
    string ChangedBy,
    DateTimeOffset ChangedAt,
    IReadOnlyList<EstimatingQuoteAuditChangeDto> Changes);

public sealed record EstimatingQuoteAuditHistoryDto(
    int QuoteHistoryId,
    int QuoteNumber,
    string Customer,
    IReadOnlyList<EstimatingQuoteAuditEventDto> Events);
