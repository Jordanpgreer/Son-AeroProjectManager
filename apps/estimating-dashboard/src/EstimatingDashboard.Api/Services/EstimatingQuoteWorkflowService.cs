using System.Globalization;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingQuoteWorkflowService(
    EstimatingAccessDbContext db,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<EstimatingPersonalQuoteDto>> GetMineAsync(
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var assigned = await db.QuoteHistory
            .AsNoTracking()
            .Where(record => !record.IsCompleted)
            .ToListAsync(cancellationToken);

        return assigned
            .Where(record => EstimatingEstimatorIdentity.MatchesUnambiguously(
                record.EstimatingRep,
                assigned.Select(candidate => candidate.EstimatingRep),
                access))
            .OrderBy(record => record.EstimatingDueDateOverride
                ?? EstimatingDueDates.AutomaticFromRfq(record.RfqDueDate)
                ?? DateTime.MaxValue)
            .ThenByDescending(record => record.QuoteNumber)
            .Select(ToDto)
            .ToList();
    }

    public async Task<EstimatingPersonalQuoteDto> UpdateAsync(
        int quoteHistoryId,
        UpdateEstimatingQuoteWorkflowDto request,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var record = await db.QuoteHistory.SingleOrDefaultAsync(
            quote => quote.Id == quoteHistoryId,
            cancellationToken)
            ?? throw new EstimatingQuoteWorkflowNotFoundException();

        var canManageTeam = access.Permissions.Contains(
            EstimatingPermissions.ManageHistory,
            StringComparer.OrdinalIgnoreCase);
        if (!canManageTeam)
        {
            var knownEstimators = await db.QuoteHistory
                .AsNoTracking()
                .Select(candidate => candidate.EstimatingRep)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (!EstimatingEstimatorIdentity.MatchesUnambiguously(
                record.EstimatingRep,
                knownEstimators,
                access))
                throw new EstimatingQuoteWorkflowForbiddenException();
        }

        if (request.ExpectedVersion != record.Version)
            throw new EstimatingQuoteWorkflowConflictException();

        var requestedStatus = Clean(request.ArdaStatus);
        var normalizedStatus = EstimatingArdaStatuses.Normalize(requestedStatus);
        if (requestedStatus is not null && normalizedStatus is null)
            throw new EstimatingQuoteWorkflowValidationException(
                "Choose one of the available Arda statuses.");

        var notes = Clean(request.Notes);
        if (notes?.Length > 2000)
            throw new EstimatingQuoteWorkflowValidationException(
                "Arda status notes cannot exceed 2,000 characters.");

        var dueDateOverride = request.EstimatingDueDateOverride?.Date;
        if (dueDateOverride is not null
            && (dueDateOverride.Value.Year < 2000 || dueDateOverride.Value.Year > 2200))
            throw new EstimatingQuoteWorkflowValidationException(
                "Choose an estimating due date between 2000 and 2200.");

        var now = timeProvider.GetUtcNow();
        var statusChanged = !string.Equals(
            record.ArdaStatus,
            normalizedStatus,
            StringComparison.OrdinalIgnoreCase);
        var changes = new List<(string Field, string? OldValue, string? NewValue)>();
        AddChange(changes, "Arda status", record.ArdaStatus, normalizedStatus);
        AddChange(changes, "Arda status notes", record.ArdaStatusNotes, notes);
        AddChange(
            changes,
            "Estimating due date override",
            AuditDate(record.EstimatingDueDateOverride),
            AuditDate(dueDateOverride));

        if (changes.Count == 0)
            return ToDto(record);

        record.ArdaStatus = normalizedStatus;
        record.ArdaStatusNotes = notes;
        record.EstimatingDueDateOverride = dueDateOverride;
        if (statusChanged)
        {
            record.ArdaStatusChangedAt = normalizedStatus is null ? null : now;
            record.ArdaStatusChangedBy = normalizedStatus is null ? null : access.AccountName;
        }
        record.Version++;

        foreach (var change in changes)
        {
            record.AuditHistory.Add(new EstimatingQuoteHistoryAuditRecord
            {
                QuoteHistory = record,
                QuoteNumber = record.QuoteNumber,
                ImportBatchId = Guid.Empty,
                Action = EstimatingQuoteAuditActions.WorkflowUpdated,
                FieldName = change.Field,
                OldValue = change.OldValue,
                NewValue = change.NewValue,
                ChangedBy = access.AccountName,
                ChangedAt = now
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new EstimatingQuoteWorkflowConflictException();
        }

        return ToDto(record);
    }

    private static EstimatingPersonalQuoteDto ToDto(EstimatingQuoteHistoryRecord record)
    {
        var automaticDueDate = EstimatingDueDates.AutomaticFromRfq(record.RfqDueDate);
        return new EstimatingPersonalQuoteDto(
            record.Id,
            record.QuoteNumber,
            record.Customer,
            record.QuoteStatus,
            record.EstimatingRep,
            record.TotalValue,
            record.RfqDueDate,
            automaticDueDate,
            record.EstimatingDueDateOverride ?? automaticDueDate,
            record.EstimatingDueDateOverride.HasValue,
            record.ArdaStatus,
            record.ArdaStatusNotes,
            record.ArdaStatusChangedAt,
            record.ArdaStatusChangedBy,
            record.Version);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? AuditDate(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void AddChange(
        ICollection<(string Field, string? OldValue, string? NewValue)> changes,
        string field,
        string? oldValue,
        string? newValue)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            changes.Add((field, oldValue, newValue));
    }
}

public static class EstimatingEstimatorIdentity
{
    public static bool Matches(string estimator, EstimatingAccessProfile access)
    {
        var normalizedEstimator = estimator.Trim();
        var displayName = access.DisplayName.Trim();
        var accountName = access.AccountName.Split('\\').Last().Split('@').First();
        if (string.Equals(normalizedEstimator, displayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedEstimator, accountName, StringComparison.OrdinalIgnoreCase))
            return true;

        var displayFirstName = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var estimatorFirstName = normalizedEstimator.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return displayFirstName is not null
            && string.Equals(estimatorFirstName, displayFirstName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesUnambiguously(
        string estimator,
        IEnumerable<string> knownEstimators,
        EstimatingAccessProfile access)
    {
        var normalizedEstimator = estimator.Trim();
        var displayName = access.DisplayName.Trim();
        var accountName = access.AccountName.Split('\\').Last().Split('@').First();
        if (string.Equals(normalizedEstimator, displayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedEstimator, accountName, StringComparison.OrdinalIgnoreCase))
            return true;

        var displayFirstName = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var estimatorFirstName = normalizedEstimator.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (displayFirstName is null
            || !string.Equals(estimatorFirstName, displayFirstName, StringComparison.OrdinalIgnoreCase))
            return false;

        var matchingNames = knownEstimators
            .Select(value => value.Trim())
            .Where(value => string.Equals(
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                displayFirstName,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matchingNames.Count == 1
            && string.Equals(matchingNames[0], normalizedEstimator, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class EstimatingQuoteWorkflowNotFoundException : Exception;
public sealed class EstimatingQuoteWorkflowForbiddenException : Exception;
public sealed class EstimatingQuoteWorkflowConflictException : Exception;

public sealed class EstimatingQuoteWorkflowValidationException(string message)
    : Exception(message);
