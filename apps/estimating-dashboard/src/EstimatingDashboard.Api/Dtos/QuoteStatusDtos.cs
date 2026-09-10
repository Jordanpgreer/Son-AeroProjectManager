namespace EstimatingDashboard.Api.Dtos;

public sealed record QuoteStatusSummaryDto(int QuoteHistoryId, int QuoteNumber, string Customer,
    string EstimatingRep, string Status, DateTimeOffset? StatusChangedAt, string? StatusChangedBy,
    DateTime? FollowUpDate, DateTimeOffset UpdatedAt, DateTimeOffset? LastMessageAt,
    int ThreadCount, int MessageCount, int UnassignedMessageCount, int Version, bool CanEdit, bool CanRemove = false);
public sealed record QuoteStatusPageDto(IReadOnlyList<QuoteStatusSummaryDto> Items, int TotalCount, int Page, int PageSize);
public sealed record QuoteStatusDetailDto(QuoteStatusSummaryDto Quote,
    IReadOnlyList<QuoteStatusActivityDto> Activity, IReadOnlyList<VendorQuoteDetailDto> Threads,
    IReadOnlyList<VendorQuoteMessageDto> UnassignedMessages, EstimatingPersonalQuoteDto Workflow,
    IReadOnlyList<RemovedQuoteEmailDto> RemovedMessages, IReadOnlyList<QuoteStatusActivityDto> RemovedNotes);
public sealed record QuoteStatusActivityDto(string Id, string Kind, string Text, string? OldValue,
    string? NewValue, DateTimeOffset OccurredAt, string AccountName, string DisplayName,
    int? RequestId, string? VendorName, string? PartNumber, DateTimeOffset? EditedAt = null,
    string? EditedBy = null, DateTimeOffset? RemovedAt = null, string? RemovedBy = null);
public sealed record UpdateQuoteStatusDto(int ExpectedVersion, string Status, DateTime? FollowUpDate = null, string? Note = null);
public sealed record AddQuoteStatusNoteDto(int ExpectedVersion, string Text);
public sealed record AssignQuoteMessageDto(int ExpectedVersion, int RequestId);
public sealed record QuoteStatusOptionsDto(IReadOnlyList<string> Statuses, IReadOnlyList<string> ThreadStatuses);
public sealed record QuoteItemVersionDto(int ExpectedVersion);
public sealed record EditQuoteNoteDto(int ExpectedVersion, string Text);
public sealed record MoveQuoteEmailDto(int ExpectedVersion, int TargetQuoteHistoryId, int TargetExpectedVersion, int? RequestId = null);
public sealed record RemovedQuoteEmailDto(long Id, string Subject, string Direction, string FromAddress,
    string? FromName, string VendorEmail, DateTimeOffset SentAt, DateTimeOffset RemovedAt, string? RemovedBy,
    int? RequestId, string? VendorName, string? PartNumber);
