namespace EstimatingDashboard.Api.Dtos;

public sealed record VendorQuoteSummaryDto(
    int Id, int QuoteHistoryId, int QuoteNumber, string Customer, string EstimatingRep,
    string VendorName, string VendorEmail, string Title, string Status,
    DateTimeOffset StatusChangedAt, string StatusChangedBy, DateTime? FollowUpDate,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastMessageAt,
    int MessageCount, int NoteCount, int Version, bool CanEdit, string? PartNumber);

public sealed record VendorQuotePageDto(
    IReadOnlyList<VendorQuoteSummaryDto> Items, int TotalCount, int Page, int PageSize);

public sealed record VendorQuoteDetailDto(
    VendorQuoteSummaryDto Request, IReadOnlyList<VendorQuoteMessageDto> Messages,
    IReadOnlyList<VendorQuoteActivityDto> Activity);

public sealed record VendorQuoteActivityDto(
    long Id, string Kind, string Text, string? OldValue, string? NewValue,
    DateTimeOffset OccurredAt, string AccountName, string DisplayName,
    DateTimeOffset? EditedAt = null, string? EditedBy = null, string? ActivityId = null);

public sealed record VendorQuoteMessageDto(
    long Id, string Direction, string Subject, string FromAddress, string? FromName,
    IReadOnlyList<string> ToAddresses, DateTimeOffset SentAt, DateTimeOffset? ReceivedAt,
    DateTimeOffset ImportedAt, string BodyText, IReadOnlyList<VendorQuoteAttachmentDto> Attachments, string VendorEmail);

public sealed record VendorQuoteAttachmentDto(long Id, string FileName, string ContentType, int SizeBytes);
public sealed record VendorQuoteOptionDto(int Id, int QuoteNumber, string Customer, string EstimatingRep);
public sealed record VendorQuoteOptionsDto(IReadOnlyList<string> Statuses, IReadOnlyList<VendorQuoteOptionDto> Quotes);

public sealed record CreateVendorQuoteDto(int QuoteHistoryId, string VendorName, string VendorEmail,
    string Title, string? Status = null, DateTime? FollowUpDate = null, string? Note = null, string? PartNumber = null);
public sealed record UpdateVendorQuoteDto(int ExpectedVersion, string VendorName, string Title,
    string Status, DateTime? FollowUpDate = null, string? Note = null, string? PartNumber = null, string? VendorEmail = null);
public sealed record AddVendorQuoteNoteDto(int ExpectedVersion, string Text);

// One vendor and one message per request; mailbox/message IDs are deduplication evidence,
// never authorization. The authenticated caller must have access to the matched quote.
public sealed record ImportVendorQuoteMessageDto(
    string SourceMessageId, string Mailbox, string Direction, string Subject,
    string FromAddress, string? FromName, IReadOnlyList<string> ToAddresses,
    string VendorEmail, string? VendorName, DateTimeOffset SentAt,
    DateTimeOffset? ReceivedAt, string BodyText,
    IReadOnlyList<ImportVendorQuoteAttachmentDto> Attachments, string? ConversationId = null);
public sealed record ImportVendorQuoteAttachmentDto(string FileName, string? ContentType, string ContentBase64);
public sealed record VendorQuoteImportResultDto(string Outcome, int? RequestId, int? QuoteNumber, string Message);

public sealed record VendorQuoteSyncHeartbeatDto(string Mailbox, string ClientName, int ImportedCount,
    int DuplicateCount, int DeferredCount, string? Error = null);
public sealed record VendorQuoteSyncStatusDto(string Mailbox, string ClientName, DateTimeOffset LastCheckedAt,
    DateTimeOffset? LastSuccessAt, int ImportedCount, int DuplicateCount, int DeferredCount, string? Error);
