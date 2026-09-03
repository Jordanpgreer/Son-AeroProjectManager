using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using SonAero.Platform.Integrations;

namespace QualityAssurance.Api.Services;

public interface IQualityShipmentSyncService
{
    Task<bool> TrySyncShipmentAsync(int shipmentId, CancellationToken cancellationToken);
    Task<int> SyncAllAsync(CancellationToken cancellationToken);
}

public sealed class QualityShipmentSyncService(
    QualityAssuranceDbContext db,
    IEnterpriseProviderSource providerSource,
    IEnumerable<IQualityShipmentProvider> providers,
    IOptions<QualityIntegrationOptions> options,
    TimeProvider timeProvider,
    ILogger<QualityShipmentSyncService> logger) : IQualityShipmentSyncService
{
    public async Task<bool> TrySyncShipmentAsync(
        int shipmentId,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return false;
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == shipmentId, cancellationToken);
        if (shipment is null || string.IsNullOrWhiteSpace(shipment.SalesOrderNumber)) return false;

        try
        {
            var provider = await SelectProviderAsync(cancellationToken);
            var results = await provider.FindByShipperNumbersAsync(
                [shipment.SalesOrderNumber],
                cancellationToken);
            if (!results.TryGetValue(shipment.SalesOrderNumber, out var external))
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
        if (!options.Value.Enabled) return 0;
        var shipments = await db.Shipments
            .Include(shipment => shipment.Parts)
            .Where(shipment => shipment.SalesOrderNumber != "")
            .ToListAsync(cancellationToken);
        if (shipments.Count == 0) return 0;

        var provider = await SelectProviderAsync(cancellationToken);
        var results = await provider.FindByShipperNumbersAsync(
            shipments.Select(shipment => shipment.SalesOrderNumber).ToArray(),
            cancellationToken);
        var changed = 0;
        foreach (var shipment in shipments)
        {
            if (!results.TryGetValue(shipment.SalesOrderNumber, out var external))
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
                DisplayOrder = index
            };
        }).ToList();
        var oldValue = FormatParts(shipment.Parts);
        var newValue = FormatParts(normalized);
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return false;

        db.ShipmentParts.RemoveRange(shipment.Parts);
        shipment.Parts.Clear();
        foreach (var part in normalized) shipment.Parts.Add(part);
        shipment.PartNumber = normalized[0].PartNumber;
        shipment.Quantity = normalized.Any(part => part.Quantity.HasValue)
            ? normalized.Sum(part => (decimal?)(part.Quantity ?? 0))
            : null;
        shipment.DollarValue = normalized.Any(part => part.TotalValue.HasValue)
            ? normalized.Sum(part => part.TotalValue ?? 0)
            : null;
        AddAudit(shipment, "ExternalPartsSynced", "parts", oldValue, newValue, providerName, now);
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
            $"{part.PartNumber}|{part.Quantity?.ToString(CultureInfo.InvariantCulture) ?? ""}|{part.UnitPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? ""}"));

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
    IOptions<QualityIntegrationOptions> options,
    TimeProvider timeProvider,
    ILogger<QualityShipmentSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Clamp(options.Value.SyncIntervalMinutes, 1, 1440)),
                    timeProvider,
                    stoppingToken);
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<IQualityShipmentSyncService>();
                var changed = await sync.SyncAllAsync(stoppingToken);
                if (changed > 0)
                    logger.LogInformation("Synchronized {ShipmentCount} Quality shipments from the active ERP provider.", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The scheduled Quality shipment synchronization failed.");
            }
        }
    }
}
