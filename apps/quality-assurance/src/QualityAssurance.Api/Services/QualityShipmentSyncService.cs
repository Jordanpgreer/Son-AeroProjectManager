using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using SonAero.Platform.Integrations;

namespace QualityAssurance.Api.Services;

public interface IQualityShipmentSyncService
{
    Task<QualityExternalShipment?> LookupAsync(string shipperNumber, CancellationToken cancellationToken);
    Task<bool> TrySyncShipmentAsync(int shipmentId, CancellationToken cancellationToken);
    Task<int> SyncAllAsync(CancellationToken cancellationToken);
}

public sealed class QualityShipmentSyncService(
    QualityAssuranceDbContext db,
    IEnterpriseProviderSource providerSource,
    IEnumerable<IQualityShipmentProvider> providers,
    TimeProvider timeProvider,
    ILogger<QualityShipmentSyncService> logger) : IQualityShipmentSyncService
{
    public async Task<QualityExternalShipment?> LookupAsync(
        string shipperNumber,
        CancellationToken cancellationToken)
    {
        var normalized = shipperNumber.Trim();
        if (normalized.Length == 0) throw new ArgumentException("Shipper number is required.");
        var provider = await SelectProviderAsync(cancellationToken);
        var results = await provider.FindByShipperNumbersAsync([normalized], cancellationToken);
        return results.GetValueOrDefault(normalized);
    }

    public async Task<bool> TrySyncShipmentAsync(
        int shipmentId,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == shipmentId, cancellationToken);
        if (shipment is null) return false;
        var shipperNumber = ShipperNumber(shipment);
        if (shipperNumber is null) return false;

        try
        {
            var provider = await SelectProviderAsync(cancellationToken);
            var results = await provider.FindByShipperNumbersAsync(
                [shipperNumber],
                cancellationToken);
            if (!results.TryGetValue(shipperNumber, out var external))
            {
                await SaveSyncMessageAsync(
                    shipment,
                    provider.ProviderName,
                    $"No matching shipper was found in {provider.ProviderName}.",
                    cancellationToken);
                return false;
            }

            Apply(shipment, external, provider.ProviderName);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Quality shipment {ShipmentId} could not be synchronized.", shipmentId);
            await SaveSyncMessageAsync(shipment, null, Limit(exception.Message, 1000), cancellationToken);
            return false;
        }
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken)
    {
        var shipments = await db.Shipments
            .Include(shipment => shipment.Parts)
            .Where(shipment => shipment.SalesOrderNumber != ""
                || (shipment.ShipperNumber != null && shipment.ShipperNumber != ""))
            .ToListAsync(cancellationToken);
        if (shipments.Count == 0) return 0;

        var provider = await SelectProviderAsync(cancellationToken);
        var shipperNumbers = shipments
            .Select(ShipperNumber)
            .Where(number => number is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = await provider.FindByShipperNumbersAsync(
            shipperNumbers,
            cancellationToken);
        var changed = 0;
        foreach (var shipment in shipments)
        {
            var shipperNumber = ShipperNumber(shipment);
            if (shipperNumber is null || !results.TryGetValue(shipperNumber, out var external))
            {
                shipment.ExternalSyncProvider = provider.ProviderName;
                shipment.ExternalSyncError = $"No matching shipper was found in {provider.ProviderName}.";
                shipment.ExternalSyncedAt = timeProvider.GetUtcNow();
                continue;
            }

            if (Apply(shipment, external, provider.ProviderName)) changed++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private static string? ShipperNumber(QualityShipment shipment) =>
        string.IsNullOrWhiteSpace(shipment.SalesOrderNumber)
            ? string.IsNullOrWhiteSpace(shipment.ShipperNumber) ? null : shipment.ShipperNumber.Trim()
            : shipment.SalesOrderNumber.Trim();

    private async Task<IQualityShipmentProvider> SelectProviderAsync(CancellationToken cancellationToken)
    {
        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        return EnterpriseAdapterSelector.Select(
            providers,
            activeProvider,
            EnterpriseDataRoutes.QualityRecords);
    }

    private bool Apply(
        QualityShipment shipment,
        QualityExternalShipment external,
        string providerName)
    {
        var now = timeProvider.GetUtcNow();
        var changed = false;
        changed |= Change(shipment, "salesOrderNumber", shipment.SalesOrderNumber, external.ShipperNumber, value => shipment.SalesOrderNumber = value, providerName, now);
        changed |= Change(shipment, "shipperNumber", shipment.ShipperNumber, external.ShipperNumber, value => shipment.ShipperNumber = value, providerName, now);
        changed |= Change(shipment, "externalShipmentId", shipment.ExternalShipmentId, external.ExternalId, value => shipment.ExternalShipmentId = value, providerName, now);
        changed |= Change(shipment, "externalShipmentUrl", shipment.ExternalShipmentUrl, external.RecordUrl, value => shipment.ExternalShipmentUrl = value, providerName, now);
        changed |= Change(shipment, "externalShipmentStatus", shipment.ExternalShipmentStatus, external.Status, value => shipment.ExternalShipmentStatus = value, providerName, now);
        if (external.ShipByDate.HasValue)
            changed |= Change(shipment, "shipDate", shipment.ShipDate, external.ShipByDate, value => shipment.ShipDate = value, providerName, now);
        if (!string.IsNullOrWhiteSpace(external.Customer))
            changed |= Change(shipment, "customer", shipment.Customer, external.Customer.Trim(), value => shipment.Customer = value, providerName, now);
        if (!string.IsNullOrWhiteSpace(external.PurchaseOrderNumber))
            changed |= Change(shipment, "purchaseOrderNumber", shipment.PurchaseOrderNumber, external.PurchaseOrderNumber.Trim(), value => shipment.PurchaseOrderNumber = value, providerName, now);
        if (external.Parts.Count > 0)
            changed |= ApplyParts(shipment, external.Parts, providerName, now);

        if (string.Equals(external.Status, "shipped", StringComparison.OrdinalIgnoreCase)
            && !shipment.IsShipped)
        {
            var previous = shipment.Status;
            shipment.IsShipped = true;
            shipment.Status = "Shipped";
            shipment.ShippedAt = external.ShippedAt ?? now;
            shipment.ShippedByAccountName = $"SYSTEM\\{providerName}";
            shipment.ShippedByDisplayName = $"{providerName} Sync";
            AddAudit(shipment, "ExternalShipped", "status", previous, shipment.Status, providerName, now);
            changed = true;
        }

        shipment.ExternalSyncProvider = providerName;
        shipment.ExternalSyncError = null;
        shipment.ExternalSyncedAt = now;
        if (changed)
        {
            shipment.LastWorkedAt = now;
            shipment.UpdatedAt = now;
            shipment.UpdatedByAccountName = $"SYSTEM\\{providerName}";
            shipment.UpdatedByDisplayName = $"{providerName} Sync";
            shipment.Version++;
        }
        return changed;
    }

    private bool ApplyParts(
        QualityShipment shipment,
        IReadOnlyList<QualityExternalShipmentPart> externalParts,
        string providerName,
        DateTimeOffset now)
    {
        var normalized = externalParts.Select((part, index) =>
        {
            var quantity = ToWholeQuantity(part.Quantity, part.PartNumber);
            decimal? unitPrice = part.UnitPrice.HasValue
                ? decimal.Round(part.UnitPrice.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            decimal? total = quantity.HasValue && unitPrice.HasValue
                ? decimal.Round(quantity.Value * unitPrice.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            return new QualityShipmentPart
            {
                PartNumber = Limit(part.PartNumber.Trim(), 160),
                Quantity = quantity,
                UnitPrice = unitPrice,
                TotalValue = total,
                ExternalItemId = LimitOptional(part.ExternalItemId, 80),
                LineItemNumber = part.LineItemNumber,
                DisplayOrder = index
            };
        }).ToList();
        var oldValue = FormatParts(shipment.Parts);
        var existing = shipment.Parts.OrderBy(part => part.DisplayOrder).ThenBy(part => part.Id).ToList();
        var claimed = new HashSet<QualityShipmentPart>();
        var changed = false;
        foreach (var external in normalized)
        {
            var match = !string.IsNullOrWhiteSpace(external.ExternalItemId)
                ? existing.FirstOrDefault(part => !claimed.Contains(part)
                    && string.Equals(part.ExternalItemId, external.ExternalItemId, StringComparison.OrdinalIgnoreCase))
                : null;
            match ??= !string.IsNullOrWhiteSpace(external.LineItemNumber)
                ? existing.FirstOrDefault(part => !claimed.Contains(part)
                    && string.Equals(part.LineItemNumber, external.LineItemNumber, StringComparison.OrdinalIgnoreCase))
                : null;
            match ??= existing.FirstOrDefault(part => !claimed.Contains(part)
                && string.Equals(part.PartNumber, external.PartNumber, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                shipment.Parts.Add(external);
                claimed.Add(external);
                changed = true;
                continue;
            }

            claimed.Add(match);
            changed |= Set(match.PartNumber, external.PartNumber, value => match.PartNumber = value);
            changed |= Set(match.Quantity, external.Quantity, value => match.Quantity = value);
            changed |= Set(match.UnitPrice, external.UnitPrice, value => match.UnitPrice = value);
            changed |= Set(match.TotalValue, external.TotalValue, value => match.TotalValue = value);
            changed |= Set(match.ExternalItemId, external.ExternalItemId, value => match.ExternalItemId = value);
            if (!string.IsNullOrWhiteSpace(external.LineItemNumber))
                changed |= Set(match.LineItemNumber, external.LineItemNumber, value => match.LineItemNumber = value);
            changed |= Set(match.DisplayOrder, external.DisplayOrder, value => match.DisplayOrder = value);
        }

        var nextDisplayOrder = normalized.Count;
        foreach (var retained in existing.Where(part => !claimed.Contains(part)).OrderBy(part => part.DisplayOrder))
            changed |= Set(retained.DisplayOrder, nextDisplayOrder++, value => retained.DisplayOrder = value);

        var ordered = shipment.Parts.OrderBy(part => part.DisplayOrder).ThenBy(part => part.Id).ToList();
        var newValue = FormatParts(ordered);
        if (!changed && string.Equals(oldValue, newValue, StringComparison.Ordinal)) return false;

        shipment.PartNumber = ordered[0].PartNumber;
        shipment.Quantity = ordered.Any(part => part.Quantity.HasValue)
            ? ordered.Sum(part => (decimal?)(part.Quantity ?? 0))
            : null;
        shipment.DollarValue = ordered.Any(part => part.TotalValue.HasValue)
            ? ordered.Sum(part => part.TotalValue ?? 0)
            : null;
        AddAudit(shipment, "ExternalPartsSynced", "parts", oldValue, newValue, providerName, now);
        return true;
    }

    private static bool Set<T>(T current, T next, Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return false;
        setter(next);
        return true;
    }

    private bool Change<T>(
        QualityShipment shipment,
        string field,
        T oldValue,
        T newValue,
        Action<T> setter,
        string providerName,
        DateTimeOffset now)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return false;
        setter(newValue);
        AddAudit(shipment, "ExternalFieldChanged", field, Format(oldValue), Format(newValue), providerName, now);
        return true;
    }

    private async Task SaveSyncMessageAsync(
        QualityShipment shipment,
        string? providerName,
        string message,
        CancellationToken cancellationToken)
    {
        shipment.ExternalSyncProvider = providerName ?? shipment.ExternalSyncProvider;
        shipment.ExternalSyncError = Limit(message, 1000);
        shipment.ExternalSyncedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static int? ToWholeQuantity(decimal? value, string partNumber)
    {
        if (!value.HasValue) return null;
        if (value.Value < 0 || decimal.Truncate(value.Value) != value.Value || value.Value > int.MaxValue)
            throw new InvalidOperationException(
                $"Fulcrum returned a non-whole or invalid quantity for part {partNumber}.");
        return decimal.ToInt32(value.Value);
    }

    private static void AddAudit(
        QualityShipment shipment,
        string eventType,
        string field,
        string? oldValue,
        string? newValue,
        string providerName,
        DateTimeOffset occurredAt) => shipment.AuditEntries.Add(new QualityShipmentAuditEntry
    {
        EventType = eventType,
        FieldName = field,
        OldValue = oldValue,
        NewValue = newValue,
        AccountName = $"SYSTEM\\{providerName}",
        DisplayName = $"{providerName} Sync",
        OccurredAt = occurredAt
    });

    private static string FormatParts(IEnumerable<QualityShipmentPart> parts) => string.Join(
        "; ",
        parts.OrderBy(part => part.DisplayOrder).Select(part =>
            $"{part.LineItemNumber ?? ""}|{part.PartNumber}|{part.Quantity?.ToString(CultureInfo.InvariantCulture) ?? ""}|{part.UnitPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? ""}"));

    private static string? Format<T>(T value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? LimitOptional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), maxLength);
}

public sealed class QualityShipmentSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<QualityIntegrationOptions> moduleOptions,
    IOptions<UniversalFulcrumSyncOptions> options,
    FulcrumScheduledRunCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<QualityShipmentSyncWorker> logger)
    : UniversalFulcrumSyncWorker(options, coordinator, timeProvider, logger)
{
    protected override string ModuleName => "quality-shipments";
    protected override bool IsEnabled => moduleOptions.Value.Enabled;

    protected override async Task PullAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var changed = await scope.ServiceProvider.GetRequiredService<IQualityShipmentSyncService>()
            .SyncAllAsync(cancellationToken);
        if (changed > 0) Log.LogInformation("Synchronized {ShipmentCount} Quality shipments.", changed);
    }
}
