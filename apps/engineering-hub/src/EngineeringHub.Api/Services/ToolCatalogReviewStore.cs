using System.Collections.Concurrent;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;

namespace EngineeringHub.Api.Services;

public sealed class ToolCatalogReviewStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);
    private readonly ConcurrentDictionary<string, ToolCatalogReview> reviews = new(StringComparer.Ordinal);

    public ToolCatalogReview Save(ToolCatalogReview review)
    {
        RemoveExpired();
        reviews[review.Id] = review;
        return review;
    }

    public ToolCatalogReview? Find(string id, string actor)
    {
        RemoveExpired();
        return reviews.TryGetValue(id, out var review)
            && string.Equals(review.Actor, actor, StringComparison.OrdinalIgnoreCase)
            ? review
            : null;
    }

    public void Remove(string id) => reviews.TryRemove(id, out _);

    public static ToolCatalogReview Create(
        string actor,
        string fileName,
        byte[] workbook,
        IReadOnlyList<ToolCatalogRow> rows,
        IReadOnlyList<ToolCatalogIssueDto> errors,
        IReadOnlyList<ToolCatalogChangeDto> changes,
        IReadOnlyDictionary<int, long> versions)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new ToolCatalogReview(
            Guid.NewGuid().ToString("N"), actor, Path.GetFileName(fileName), createdAt,
            createdAt.Add(Lifetime), workbook, rows, errors, changes, versions);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in reviews.Where(pair => pair.Value.ExpiresAt <= now))
            reviews.TryRemove(pair.Key, out _);
    }
}

public sealed record ToolCatalogReview(
    string Id,
    string Actor,
    string FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    byte[] OriginalWorkbook,
    IReadOnlyList<ToolCatalogRow> Rows,
    IReadOnlyList<ToolCatalogIssueDto> Errors,
    IReadOnlyList<ToolCatalogChangeDto> Changes,
    IReadOnlyDictionary<int, long> Versions);

public sealed record ToolCatalogRow(
    int Row,
    int? ExistingId,
    string? ToolNumber,
    string? Name,
    string? ToolType,
    string? Owner,
    ToolCustodyStatus? Status,
    string? PhysicalAssignment,
    string? HomeLocation,
    string? CurrentHolder,
    DateTime? ReferenceAuditDate,
    DateTime? NewAuditDate,
    IReadOnlyList<string> PartNumbers,
    string? Description,
    string? Notes,
    bool? IsArchived);

public sealed class ToolCatalogValidationException(string message) : Exception(message);
public sealed class ToolCatalogConflictException(string message) : Exception(message);
