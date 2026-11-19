namespace EstimatingDashboard.Api.Dtos;

internal sealed record QuoteTimingDiagnosticDto(int QuoteHistoryId, int QuoteNumber,
    QuoteTimingStoredDto Stored, QuoteTimingMappingsDto Mappings,
    IReadOnlyList<QuoteTimingSyncDto> RecentSyncs,
    QuoteTimingReadDto List, QuoteTimingReadDto Detail, QuoteTimingDefinitionsDto Definitions);

internal sealed record QuoteTimingDefinitionDto(string Name, string Label);
internal sealed record QuoteTimingDefinitionsDto(string Status, int? UpstreamStatusCode,
    DateTimeOffset? RetryAt, IReadOnlyList<QuoteTimingDefinitionDto> Fields, bool Truncated);

internal sealed record QuoteTimingDatesDto(DateTime? DateToEstimating,
    DateTime? EstimatingCompletionDate, DateTime? RfqDueDate);
internal sealed record QuoteTimingStoredDto(QuoteTimingDatesDto Dates, int? Workdays,
    bool IsCompleted, DateTimeOffset UpdatedAt);
internal sealed record QuoteTimingMappingsDto(string DateToEstimating,
    string EstimatingCompletionDate, string RfqDueDate);
internal sealed record QuoteTimingSyncDto(string Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, int QuotesReceived, int NewRecords, int UpdatedRecords, int UnchangedRecords);
internal sealed record QuoteTimingFieldDto(string Purpose, string Name, bool SelectedByMapper,
    string ValueKind, string Shape);
internal sealed record QuoteTimingReadDto(string Status, int? UpstreamStatusCode = null,
    DateTimeOffset? RetryAt = null, int? ReturnedCount = null, bool PossiblyTruncated = false,
    IReadOnlyList<string>? AvailableFieldNames = null, bool FieldNamesTruncated = false,
    IReadOnlyList<QuoteTimingFieldDto>? RecognizedFields = null,
    QuoteTimingDatesDto? ProjectedDates = null, bool HasMappingWarnings = false);
