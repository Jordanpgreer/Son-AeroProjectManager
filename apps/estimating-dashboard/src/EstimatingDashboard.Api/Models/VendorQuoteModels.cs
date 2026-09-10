namespace EstimatingDashboard.Api.Models;

public sealed class VendorQuoteRequest
{
    public int Id { get; set; }
    public int QuoteHistoryId { get; set; }
    public EstimatingQuoteHistoryRecord QuoteHistory { get; set; } = null!;
    public string VendorName { get; set; } = "";
    public string VendorEmail { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public string? PartNumber { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Untouched";
    public DateTimeOffset StatusChangedAt { get; set; }
    public string StatusChangedBy { get; set; } = "";
    public DateTime? FollowUpDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public int Version { get; set; }
    public ICollection<VendorQuoteMessage> Messages { get; set; } = [];
    public ICollection<VendorQuoteActivity> Activity { get; set; } = [];
}

public sealed class VendorQuoteMessage
{
    public long Id { get; set; }
    public int? RequestId { get; set; }
    public VendorQuoteRequest? Request { get; set; }
    public int QuoteHistoryId { get; set; }
    public EstimatingQuoteHistoryRecord QuoteHistory { get; set; } = null!;
    public string VendorEmail { get; set; } = "";
    public string? ConversationId { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Mailbox { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string ToAddressesJson { get; set; } = "[]";
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string ImportedBy { get; set; } = "";
    public string BodyText { get; set; } = "";
    public ICollection<VendorQuoteAttachment> Attachments { get; set; } = [];
}

public sealed class VendorQuoteAttachment
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public VendorQuoteMessage Message { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public int SizeBytes { get; set; }
    public byte[] Content { get; set; } = [];
}

public sealed class VendorQuoteActivity
{
    public long Id { get; set; }
    public int RequestId { get; set; }
    public VendorQuoteRequest Request { get; set; } = null!;
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string AccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class VendorQuoteSyncState
{
    public int Id { get; set; }
    public string AccountName { get; set; } = "";
    public string Mailbox { get; set; } = "";
    public string ClientName { get; set; } = "";
    public DateTimeOffset LastCheckedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int ImportedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int DeferredCount { get; set; }
    public string? Error { get; set; }
}

public static class VendorQuoteStatuses
{
    public static readonly IReadOnlyList<string> All = ["Untouched", "Rates requested", "Waiting on vendor",
        "Reply received", "Quote received", "Under review", "Accepted", "Declined", "Cancelled"];
}

public sealed class QuoteStatusMetadata
{
    public int QuoteHistoryId { get; set; }
    public EstimatingQuoteHistoryRecord QuoteHistory { get; set; } = null!;
    public DateTime? FollowUpDate { get; set; }
}

public sealed class QuoteStatusActivity
{
    public long Id { get; set; }
    public int QuoteHistoryId { get; set; }
    public EstimatingQuoteHistoryRecord QuoteHistory { get; set; } = null!;
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string AccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
