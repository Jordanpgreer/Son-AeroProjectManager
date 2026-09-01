using System.Collections.Concurrent;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed class FulcrumEstimateReviewStore(TimeProvider timeProvider)
{
    public const int MaximumReviews = 256;
    public const int MaximumReviewsPerActor = 8;
    private readonly ConcurrentDictionary<Guid, FulcrumEstimateReview> reviews = new();
    private readonly object gate = new();

    internal FulcrumEstimateReview Add(FulcrumEstimateReview review)
    {
        lock (gate)
        {
            CleanupLocked();
            TrimLocked(
                reviews.Where(item => string.Equals(
                    item.Value.Actor,
                    review.Actor,
                    StringComparison.OrdinalIgnoreCase)),
                MaximumReviewsPerActor - 1);
            TrimLocked(reviews, MaximumReviews - 1);
            reviews[review.Id] = review;
            return review;
        }
    }

    internal FulcrumEstimateReview Get(Guid id, string actor)
    {
        lock (gate)
        {
            CleanupLocked();
            if (!reviews.TryGetValue(id, out var review)
                || !string.Equals(review.Actor, actor, StringComparison.OrdinalIgnoreCase))
                throw new FulcrumEstimateReviewNotFoundException();
            return review;
        }
    }

    private void CleanupLocked()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in reviews.Where(item => item.Value.ExpiresAt <= now))
            reviews.TryRemove(item.Key, out _);
    }

    private void TrimLocked(
        IEnumerable<KeyValuePair<Guid, FulcrumEstimateReview>> candidates,
        int maximumToKeep)
    {
        var ordered = candidates
            .OrderBy(item => item.Value.ExpiresAt)
            .ThenBy(item => item.Key)
            .ToList();
        foreach (var item in ordered.Take(Math.Max(0, ordered.Count - maximumToKeep)))
            reviews.TryRemove(item.Key, out _);
    }
}

internal sealed record FulcrumEstimateReview(
    Guid Id,
    string Actor,
    DateTimeOffset ExpiresAt,
    string SourceFileName,
    string PartNumber,
    string Revision,
    DateOnly EstimateDate,
    string EstimatorInitials,
    int RateYear,
    IReadOnlyList<FulcrumOperationPreviewDto> Operations,
    IReadOnlyList<FulcrumMaterialPreviewDto> Materials,
    IReadOnlyList<FulcrumManualFieldDto> ManualFields,
    IReadOnlyList<FulcrumEstimateIssueDto> Issues);

public sealed class FulcrumEstimateReviewNotFoundException : Exception
{
    public FulcrumEstimateReviewNotFoundException()
        : base("This estimate review is missing, expired, or belongs to another user. Upload the Fulcrum workbook again.") { }
}

public sealed class FulcrumEstimateValidationException(string message) : Exception(message);
public sealed class FulcrumEstimateManualValidationException(string message) : Exception(message);
