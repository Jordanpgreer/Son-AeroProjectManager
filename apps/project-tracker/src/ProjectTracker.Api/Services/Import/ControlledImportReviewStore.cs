using System.Collections.Concurrent;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Import;

public sealed class ControlledImportReviewStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);
    private readonly ConcurrentDictionary<string, ControlledImportReview> reviews = new(StringComparer.Ordinal);

    public ControlledImportReview Save(ControlledImportReview review)
    {
        RemoveExpired();
        reviews[review.Id] = review;
        return review;
    }

    public ControlledImportReview? Find(string id, string accountName)
    {
        RemoveExpired();
        return reviews.TryGetValue(id, out var review)
            && string.Equals(review.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
            ? review
            : null;
    }

    public void Remove(string id) => reviews.TryRemove(id, out _);

    public void RemoveForProject(int projectId)
    {
        RemoveExpired();
        foreach (var pair in reviews.Where(pair => pair.Value.ProjectVersions.ContainsKey(projectId)))
            reviews.TryRemove(pair.Key, out _);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in reviews.Where(pair => pair.Value.ExpiresAt <= now))
            reviews.TryRemove(pair.Key, out _);
    }

    public static ControlledImportReview Create(
        string accountName,
        string fileName,
        byte[] workbook,
        ControlledImportPayload payload,
        IReadOnlyList<ImportIssueDto> errors,
        IReadOnlyList<ImportChangeDto> changes,
        IReadOnlyDictionary<int, long> projectVersions,
        IReadOnlyDictionary<int, long> operationVersions,
        int? projectScopeId = null)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new ControlledImportReview(
            Guid.NewGuid().ToString("N"),
            accountName,
            fileName,
            createdAt,
            createdAt.Add(Lifetime),
            workbook,
            payload,
            errors,
            changes,
            projectVersions,
            operationVersions,
            projectScopeId);
    }
}

public sealed record ControlledImportReview(
    string Id,
    string AccountName,
    string FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    byte[] OriginalWorkbook,
    ControlledImportPayload Payload,
    IReadOnlyList<ImportIssueDto> Errors,
    IReadOnlyList<ImportChangeDto> Changes,
    IReadOnlyDictionary<int, long> ProjectVersions,
    IReadOnlyDictionary<int, long> OperationVersions,
    int? ProjectScopeId);

public sealed record ControlledImportPayload(
    IReadOnlyList<ControlledProjectRow> Projects,
    IReadOnlyList<ControlledOperationRow> Operations,
    string SourceFormat = "Controlled Project Tracker template",
    bool UsesPortableIdentifiers = false);

public sealed record ControlledProjectRow(
    int Row,
    string Key,
    int? ExistingId,
    string ProgramName,
    string CustomerName,
    string? ProgramManager,
    string? Engineer,
    string? SalesOrderNumber,
    string? JobNumber,
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    int? PriorityRank,
    DateOnly? CompletedOn,
    bool RequiresCompletion = false);

public sealed record ControlledOperationRow(
    int Row,
    string ProjectKey,
    string Key,
    int? ExistingId,
    int Sequence,
    string Title,
    string? Phase,
    string? WorkStation,
    string? DependencyKey,
    bool StartDateLocked,
    DateOnly? StartDate,
    DateOnly? OriginalStartDate,
    DateOnly? EndDate,
    DateOnly? OriginalEndDate,
    int? EstimatedDuration,
    int? ActualDuration,
    decimal PercentComplete,
    string? Notes,
    string? ExternalTaskId);

public sealed class ControlledImportValidationException(string message) : Exception(message);

public sealed class ControlledImportConflictException(string message) : Exception(message);
