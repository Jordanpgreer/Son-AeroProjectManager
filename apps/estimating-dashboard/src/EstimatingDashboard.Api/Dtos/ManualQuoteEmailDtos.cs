namespace EstimatingDashboard.Api.Dtos;

public sealed record ManualQuoteEmailPreviewDto(string FileName, string Subject, string FromAddress,
    string? FromName, IReadOnlyList<string> ToAddresses, DateTimeOffset SentAt, DateTimeOffset? ReceivedAt,
    int AttachmentCount);
public sealed record ManualQuoteEmailImportOptions(int ExpectedVersion, int? RequestId, string Direction,
    string? VendorEmail = null, string? Note = null);
public sealed record ManualQuoteEmailImportResultDto(string Outcome, int? RequestId, int QuoteNumber,
    string Message, QuoteStatusDetailDto Detail);
