using System.Collections.Concurrent;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistoryReviewStore
{
    private readonly ConcurrentDictionary<Guid, EstimatingHistoryImportReview> reviews = new();

    internal EstimatingHistoryImportReview Add(EstimatingHistoryImportReview review)
    {
        Cleanup();
        reviews[review.Id] = review;
        return review;
    }

    internal EstimatingHistoryImportReview Get(Guid id, string actor)
    {
        Cleanup();
        if (!reviews.TryGetValue(id, out var review)
            || !string.Equals(review.Actor, actor, StringComparison.OrdinalIgnoreCase))
            throw new EstimatingHistoryReviewNotFoundException();
        return review;
    }

    internal void Remove(Guid id) => reviews.TryRemove(id, out _);

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var review in reviews.Where(pair => pair.Value.ExpiresAt <= now))
            reviews.TryRemove(review.Key, out _);
    }
}

internal sealed record EstimatingHistoryImportReview(
    Guid Id,
    string Actor,
    DateTimeOffset ExpiresAt,
    string FileName,
    string FileHash,
    int TotalRows,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedRecords,
    IReadOnlyList<EstimatingHistoryImportIssueDto> Errors,
    IReadOnlyList<EstimatingHistoryImportChangeDto> Changes,
    IReadOnlyList<EstimatingHistoryImportRow> Rows,
    IReadOnlySet<int> InvalidRows,
    IReadOnlyDictionary<int, int> ExpectedVersions);

internal sealed record EstimatingHistoryImportRow(
    int RowNumber,
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

public sealed class EstimatingHistoryReviewNotFoundException : Exception
{
    public EstimatingHistoryReviewNotFoundException()
        : base("This import review is missing, expired, or belongs to another user.") { }
}

public sealed class EstimatingHistoryImportValidationException(string message) : Exception(message);
public sealed class EstimatingHistoryImportConflictException(string message) : Exception(message);
