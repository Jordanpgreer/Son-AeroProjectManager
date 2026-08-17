using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;

namespace QualityAssurance.Api.Services;

public sealed class QualityShippingLayoutService(QualityAssuranceDbContext db)
{
    private sealed record ColumnDefinition(string Key, int DefaultWidth, int MinimumWidth, int MaximumWidth, bool Required);

    private static readonly IReadOnlyList<ColumnDefinition> Definitions =
    [
        Column("status", 150, 110, 240, required: true),
        Column("salesOrderNumber", 140, 100, 240),
        Column("qaArrivalDate", 115, 90, 180),
        Column("partNumber", 145, 105, 260, required: true),
        Column("purchaseOrderNumber", 120, 90, 220),
        Column("customer", 180, 120, 320),
        Column("taskType", 150, 110, 260),
        Column("quantity", 90, 70, 150),
        Column("dollarValue", 125, 95, 190),
        Column("shipDate", 135, 105, 210),
        Column("holdReason", 235, 130, 420),
        Column("sourceRequestedDate", 130, 100, 210),
        Column("nextAction", 255, 150, 480, required: true),
        Column("lastWorkedAt", 125, 95, 210),
        Column("comments", 255, 150, 480),
        Column("assignment", 175, 120, 300),
        Column("queueAge", 90, 70, 150)
    ];

    private static readonly IReadOnlyDictionary<string, ColumnDefinition> DefinitionByKey =
        Definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    public async Task<QualityShippingLayoutDto> GetAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var preference = await db.ShippingLayoutPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AppUserId == access.UserId, cancellationToken);
        if (preference is null) return Default();
        try
        {
            var stored = JsonSerializer.Deserialize<List<QualityShippingColumnDto>>(preference.LayoutJson);
            return new QualityShippingLayoutDto(
                Normalize(stored ?? []),
                preference.Version,
                preference.UpdatedAt);
        }
        catch (JsonException)
        {
            var defaults = Default();
            return defaults with { Version = preference.Version, UpdatedAt = preference.UpdatedAt };
        }
    }

    public async Task<QualityShippingLayoutDto> SaveAsync(
        QualityShippingLayoutUpdateDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        Validate(dto.Columns);
        var columns = Normalize(dto.Columns);
        var preference = await db.ShippingLayoutPreferences
            .SingleOrDefaultAsync(candidate => candidate.AppUserId == access.UserId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (preference is null)
        {
            if (dto.Version != 0)
                throw new DbUpdateConcurrencyException("The saved layout changed. Reload before saving.");
            preference = new QualityShippingLayoutPreference
            {
                AppUserId = access.UserId,
                AccountName = access.AccountName,
                Version = 1
            };
            db.ShippingLayoutPreferences.Add(preference);
        }
        else
        {
            if (preference.Version != dto.Version)
                throw new DbUpdateConcurrencyException("The saved layout changed. Reload before saving.");
            db.Entry(preference).Property(candidate => candidate.Version).OriginalValue = dto.Version;
            preference.Version++;
        }
        preference.AccountName = access.AccountName;
        preference.LayoutJson = JsonSerializer.Serialize(columns);
        preference.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new QualityShippingLayoutDto(columns, preference.Version, preference.UpdatedAt);
    }

    public async Task<QualityShippingLayoutDto> ResetAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var preference = await db.ShippingLayoutPreferences
            .SingleOrDefaultAsync(candidate => candidate.AppUserId == access.UserId, cancellationToken);
        if (preference is not null)
        {
            db.ShippingLayoutPreferences.Remove(preference);
            await db.SaveChangesAsync(cancellationToken);
        }
        return Default();
    }

    private static QualityShippingLayoutDto Default() =>
        new(Definitions.Select(definition => new QualityShippingColumnDto(
            definition.Key,
            definition.DefaultWidth,
            true)).ToList(), 0, null);

    private static IReadOnlyList<QualityShippingColumnDto> Normalize(
        IEnumerable<QualityShippingColumnDto> source)
    {
        var normalized = new List<QualityShippingColumnDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in source)
        {
            if (!DefinitionByKey.TryGetValue(column.Key, out var definition) || !seen.Add(column.Key)) continue;
            normalized.Add(new QualityShippingColumnDto(
                column.Key,
                Math.Clamp(column.Width, definition.MinimumWidth, definition.MaximumWidth),
                definition.Required || column.IsVisible));
        }
        foreach (var definition in Definitions.Where(definition => !seen.Contains(definition.Key)))
        {
            normalized.Add(new QualityShippingColumnDto(
                definition.Key,
                definition.DefaultWidth,
                true));
        }
        return normalized;
    }

    private static void Validate(IReadOnlyList<QualityShippingColumnDto> columns)
    {
        if (columns.Count != Definitions.Count)
            throw new ArgumentException("The layout must contain every Shipping Status column exactly once.");
        var keys = columns.Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != Definitions.Count || keys.Any(key => !DefinitionByKey.ContainsKey(key)))
            throw new ArgumentException("The layout contains an unknown or duplicate Shipping Status column.");
        if (columns.Any(column => DefinitionByKey[column.Key].Required && !column.IsVisible))
            throw new ArgumentException("Status, Part Number, and Action must remain visible.");
        if (columns.Any(column => column.Width < DefinitionByKey[column.Key].MinimumWidth
            || column.Width > DefinitionByKey[column.Key].MaximumWidth))
            throw new ArgumentException("One or more column widths are outside the supported range.");
    }

    private static ColumnDefinition Column(
        string key,
        int defaultWidth,
        int minimumWidth,
        int maximumWidth,
        bool required = false) =>
        new(key, defaultWidth, minimumWidth, maximumWidth, required);
}
