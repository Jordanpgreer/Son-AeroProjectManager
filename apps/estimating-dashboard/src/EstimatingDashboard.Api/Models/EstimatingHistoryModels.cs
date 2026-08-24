namespace EstimatingDashboard.Api.Models;

public sealed class EstimatingQuoteHistoryRecord
{
    public int Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public int QuoteNumber { get; set; }
    public string Customer { get; set; } = string.Empty;
    public string? CustomerContact { get; set; }
    public string SalesPerson { get; set; } = string.Empty;
    public string QuoteStatus { get; set; } = string.Empty;
    public string? RfqReferenceNumber { get; set; }
    public string EstimatingRep { get; set; } = string.Empty;
    public decimal TotalValue { get; set; }
    public DateTime? RfqDueDate { get; set; }
    public DateTime? DateToEstimating { get; set; }
    public string? Issues { get; set; }
    public string? QuoteOnTrack { get; set; }
    public string? QuoteComplexity { get; set; }
    public int NumberOfParts { get; set; }
    public string? EstimatingStatus { get; set; }
    public DateTime? EstimatingCompletionDate { get; set; }
    public string OnTimeStatus { get; set; } = EstimatingOnTimeStatuses.NoData;
    public int DaysLate { get; set; }
    public int? Workdays { get; set; }
    public string? CompletedMonth { get; set; }
    public int? CompletedYear { get; set; }
    public int? CompletedWeekOfMonth { get; set; }
    public string? CompletedMonthAndWeek { get; set; }
    public bool IsCompleted { get; set; }
    public int? CompletedWeekOfYear { get; set; }
    public bool IsOnTime { get; set; }
    public decimal? OnTimeRatio { get; set; }
    public Guid LastImportBatchId { get; set; }
    public DateTimeOffset FirstImportedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class EstimatingHistoryImportBatch
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string ImportedBy { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; }
    public int TotalRows { get; set; }
    public int NewRecords { get; set; }
    public int UpdatedRecords { get; set; }
    public int UnchangedRecords { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
}

public static class EstimatingOnTimeStatuses
{
    public const string OnTime = "OnTime";
    public const string Late = "Late";
    public const string NoData = "NoData";
}
