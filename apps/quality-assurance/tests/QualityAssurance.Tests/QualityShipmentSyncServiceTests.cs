using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Integrations;

namespace QualityAssurance.Tests;

public sealed class QualityShipmentSyncServiceTests
{
    [Fact]
    public async Task Exact_provider_match_updates_values_parts_link_and_shipped_status()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new QualityAssuranceDbContext(
            new DbContextOptionsBuilder<QualityAssuranceDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "SHIP-100",
            PartNumber = "OLD-PART",
            Customer = "Old Customer",
            TaskType = "General",
            Status = "Ready to Ship",
            AssignedGroupId = 20,
            AssignedGroupName = "Shipping",
            CreatedByAccountName = "TEST\\admin",
            CreatedByDisplayName = "Admin",
            UpdatedByAccountName = "TEST\\admin",
            UpdatedByDisplayName = "Admin",
            Version = 1
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        var external = new QualityExternalShipment(
            "fulcrum-id-100",
            "SHIP-100",
            "shipped",
            new DateOnly(2026, 9, 10),
            new DateTimeOffset(2026, 9, 9, 15, 0, 0, TimeSpan.Zero),
            "Synced Customer",
            "PO-100",
            "https://fulcrum.example/shipments/fulcrum-id-100",
            [
                new QualityExternalShipmentPart("PART-A", 2, 12.50m, "line-a"),
                new QualityExternalShipmentPart("PART-B", 3, 20m, "line-b")
            ]);
        var service = new QualityShipmentSyncService(
            db,
            new StubProviderSource(),
            [new StubProvider(external)],
            Options.Create(new QualityIntegrationOptions { Enabled = true }),
            TimeProvider.System,
            NullLogger<QualityShipmentSyncService>.Instance);

        Assert.True(await service.TrySyncShipmentAsync(shipment.Id, default));

        var saved = await db.Shipments.Include(candidate => candidate.Parts).SingleAsync();
        Assert.True(saved.IsShipped);
        Assert.Equal("Shipped", saved.Status);
        Assert.Equal("fulcrum-id-100", saved.ExternalShipmentId);
        Assert.Equal("https://fulcrum.example/shipments/fulcrum-id-100", saved.ExternalShipmentUrl);
        Assert.Equal(new DateOnly(2026, 9, 10), saved.ShipDate);
        Assert.Equal("Synced Customer", saved.Customer);
        Assert.Equal("PO-100", saved.PurchaseOrderNumber);
        Assert.Equal(5m, saved.Quantity);
        Assert.Equal(85m, saved.DollarValue);
        Assert.Equal(2, saved.Parts.Count);
        Assert.Contains(await db.ShipmentAuditEntries.ToListAsync(), entry => entry.EventType == "ExternalShipped");
    }

    private sealed class StubProviderSource : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) =>
            Task.FromResult(EnterpriseProviderNames.Fulcrum);
    }

    private sealed class StubProvider(QualityExternalShipment shipment) : IQualityShipmentProvider
    {
        public string ProviderName => EnterpriseProviderNames.Fulcrum;
        public string RouteName => EnterpriseDataRoutes.QualityRecords;

        public Task<IReadOnlyDictionary<string, QualityExternalShipment>> FindByShipperNumbersAsync(
            IReadOnlyCollection<string> shipperNumbers,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, QualityExternalShipment>>(
                new Dictionary<string, QualityExternalShipment>(StringComparer.OrdinalIgnoreCase)
                {
                    [shipment.ShipperNumber] = shipment
                });
    }
}
