using System.Text.RegularExpressions;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public static partial class EstimatingOperationNames
{
    public static string Clean(string? value) => Whitespace().Replace(value?.Trim() ?? string.Empty, " ");
    public static string Normalize(string? value) => Clean(value).ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed class EstimatingOperationMappingService(EstimatingAccessDbContext db)
{
    public async Task<EstimatingOperationMappingCatalogDto> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var references = await db.EstimatingRateReferences.AsNoTracking()
            .Where(reference => reference.IsActive
                && reference.SourceRow >= 7
                && reference.SourceRow <= 44)
            .OrderBy(reference => reference.SourceRow)
            .ThenBy(reference => reference.Key)
            .Select(reference => new EstimatingRateReferenceDto(
                reference.Key,
                reference.Category,
                reference.SourceRow,
                reference.OperationName))
            .ToListAsync(cancellationToken);
        var rules = await db.EstimatingOperationMappings.AsNoTracking()
            .Include(mapping => mapping.RateReference)
            .OrderByDescending(mapping => mapping.IsActive)
            .ThenBy(mapping => mapping.FulcrumOperation)
            .Select(mapping => ToDto(mapping))
            .ToListAsync(cancellationToken);
        return new(references, rules);
    }

    public async Task<EstimatingOperationMappingDto> CreateAsync(
        CreateEstimatingOperationMappingDto request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateSource(request.FulcrumOperation);
        var sourceKey = EstimatingOperationNames.Normalize(source);
        var reference = await ActiveReferenceAsync(request.RateReferenceKey, cancellationToken);
        if (await db.EstimatingOperationMappings.AnyAsync(
            mapping => mapping.FulcrumOperationKey == sourceKey,
            cancellationToken))
            throw new EstimatingOperationMappingConflictException(
                $"A rule for Fulcrum operation '{source}' already exists.");

        var now = DateTimeOffset.UtcNow;
        var mapping = new EstimatingOperationMappingRecord
        {
            FulcrumOperation = source,
            FulcrumOperationKey = sourceKey,
            RateReferenceKey = reference.Key,
            RateReference = reference,
            IsActive = true,
            Version = 0,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor
        };
        mapping.AuditHistory.Add(Audit(
            mapping,
            EstimatingOperationMappingAuditActions.Created,
            null,
            source,
            null,
            reference.Key,
            null,
            true,
            actor,
            now));
        db.EstimatingOperationMappings.Add(mapping);
        await SaveAsync(cancellationToken);
        return ToDto(mapping);
    }

    public async Task<EstimatingOperationMappingDto> UpdateAsync(
        int id,
        UpdateEstimatingOperationMappingDto request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var mapping = await db.EstimatingOperationMappings
            .Include(item => item.RateReference)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new EstimatingOperationMappingNotFoundException();
        EnsureVersion(mapping, request.Version);
        if (!mapping.IsActive)
            throw new EstimatingOperationMappingValidationException("A deactivated rule cannot be edited.");

        var source = ValidateSource(request.FulcrumOperation);
        var sourceKey = EstimatingOperationNames.Normalize(source);
        var reference = await ActiveReferenceAsync(request.RateReferenceKey, cancellationToken);
        if (await db.EstimatingOperationMappings.AnyAsync(
            item => item.Id != id && item.FulcrumOperationKey == sourceKey,
            cancellationToken))
            throw new EstimatingOperationMappingConflictException(
                $"A rule for Fulcrum operation '{source}' already exists.");

        var oldSource = mapping.FulcrumOperation;
        var oldReference = mapping.RateReferenceKey;
        var now = DateTimeOffset.UtcNow;
        mapping.FulcrumOperation = source;
        mapping.FulcrumOperationKey = sourceKey;
        mapping.RateReferenceKey = reference.Key;
        mapping.RateReference = reference;
        mapping.Version++;
        mapping.UpdatedAt = now;
        mapping.UpdatedBy = actor;
        db.EstimatingOperationMappingAudits.Add(Audit(
            mapping,
            EstimatingOperationMappingAuditActions.Updated,
            oldSource,
            source,
            oldReference,
            reference.Key,
            true,
            true,
            actor,
            now));
        await SaveAsync(cancellationToken);
        return ToDto(mapping);
    }

    public async Task<EstimatingOperationMappingDto> DeactivateAsync(
        int id,
        int version,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var mapping = await db.EstimatingOperationMappings
            .Include(item => item.RateReference)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new EstimatingOperationMappingNotFoundException();
        EnsureVersion(mapping, version);
        if (!mapping.IsActive) return ToDto(mapping);
        var now = DateTimeOffset.UtcNow;
        mapping.IsActive = false;
        mapping.Version++;
        mapping.UpdatedAt = now;
        mapping.UpdatedBy = actor;
        db.EstimatingOperationMappingAudits.Add(Audit(
            mapping,
            EstimatingOperationMappingAuditActions.Deactivated,
            mapping.FulcrumOperation,
            mapping.FulcrumOperation,
            mapping.RateReferenceKey,
            mapping.RateReferenceKey,
            true,
            false,
            actor,
            now));
        await SaveAsync(cancellationToken);
        return ToDto(mapping);
    }

    private async Task<EstimatingRateReferenceRecord> ActiveReferenceAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var cleaned = key?.Trim() ?? string.Empty;
        return await db.EstimatingRateReferences.SingleOrDefaultAsync(
            reference => reference.Key == cleaned
                && reference.IsActive
                && reference.SourceRow >= 7
                && reference.SourceRow <= 44,
            cancellationToken)
            ?? throw new EstimatingOperationMappingValidationException(
                "Choose an active operation from the Estimating Rates reference.");
    }

    private static string ValidateSource(string? source)
    {
        var cleaned = EstimatingOperationNames.Clean(source);
        if (cleaned.Length == 0)
            throw new EstimatingOperationMappingValidationException("Fulcrum operation is required.");
        if (cleaned.Length > 160)
            throw new EstimatingOperationMappingValidationException("Fulcrum operation cannot exceed 160 characters.");
        return cleaned;
    }

    private static void EnsureVersion(EstimatingOperationMappingRecord mapping, int expected)
    {
        if (mapping.Version != expected)
            throw new EstimatingOperationMappingConflictException(
                "This rule changed after it was loaded. Refresh the rules and try again.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new EstimatingOperationMappingConflictException(
                "The rule conflicts with another saved rule.", exception);
        }
    }

    private static EstimatingOperationMappingAuditRecord Audit(
        EstimatingOperationMappingRecord mapping,
        string action,
        string? oldSource,
        string? newSource,
        string? oldReference,
        string? newReference,
        bool? oldActive,
        bool? newActive,
        string actor,
        DateTimeOffset now) => new()
    {
        OperationMapping = mapping,
        Action = action,
        OldFulcrumOperation = oldSource,
        NewFulcrumOperation = newSource,
        OldRateReferenceKey = oldReference,
        NewRateReferenceKey = newReference,
        OldIsActive = oldActive,
        NewIsActive = newActive,
        ChangedAt = now,
        ChangedBy = actor
    };

    private static EstimatingOperationMappingDto ToDto(EstimatingOperationMappingRecord mapping) => new(
        mapping.Id,
        mapping.FulcrumOperation,
        mapping.RateReferenceKey,
        mapping.RateReference.OperationName,
        mapping.IsActive,
        mapping.Version,
        mapping.UpdatedAt,
        mapping.UpdatedBy);
}

public sealed class EstimatingOperationMappingValidationException(string message) : Exception(message);
public sealed class EstimatingOperationMappingNotFoundException : Exception;
public sealed class EstimatingOperationMappingConflictException : Exception
{
    public EstimatingOperationMappingConflictException(string message) : base(message) { }
    public EstimatingOperationMappingConflictException(string message, Exception inner) : base(message, inner) { }
}
