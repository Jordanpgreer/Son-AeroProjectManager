using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Models;

namespace QualityAssurance.Api.Data;

public sealed class QualityAssuranceDbContext(
    DbContextOptions<QualityAssuranceDbContext> options) : DbContext(options)
{
    public DbSet<QualityShipment> Shipments => Set<QualityShipment>();
    public DbSet<QualityShipmentAuditEntry> ShipmentAuditEntries => Set<QualityShipmentAuditEntry>();
    public DbSet<QualityAssignmentRule> AssignmentRules => Set<QualityAssignmentRule>();
    public DbSet<QualityShippingLayoutPreference> ShippingLayoutPreferences => Set<QualityShippingLayoutPreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QualityShipment>(entity =>
        {
            entity.ToTable("QualityShipments");
            entity.HasKey(shipment => shipment.Id);
            entity.Property(shipment => shipment.Status).HasMaxLength(80);
            entity.Property(shipment => shipment.SalesOrderNumber).HasMaxLength(80);
            entity.Property(shipment => shipment.PartNumber).HasMaxLength(160);
            entity.Property(shipment => shipment.PurchaseOrderNumber).HasMaxLength(160);
            entity.Property(shipment => shipment.Customer).HasMaxLength(240);
            entity.Property(shipment => shipment.TaskType).HasMaxLength(120);
            entity.Property(shipment => shipment.Quantity).HasPrecision(18, 3);
            entity.Property(shipment => shipment.DollarValue).HasPrecision(18, 2);
            entity.Property(shipment => shipment.AssignedGroupName).HasMaxLength(160);
            entity.Property(shipment => shipment.AssignedAccountName).HasMaxLength(160);
            entity.Property(shipment => shipment.AssignedDisplayName).HasMaxLength(160);
            entity.Property(shipment => shipment.Version).IsConcurrencyToken();
            entity.HasIndex(shipment => new { shipment.IsShipped, shipment.AssignedUserId, shipment.CreatedAt });
            entity.HasIndex(shipment => new { shipment.IsShipped, shipment.AssignedGroupId, shipment.ShipDate });
            entity.HasIndex(shipment => shipment.Customer);
            entity.HasIndex(shipment => shipment.TaskType);
            entity.HasMany(shipment => shipment.AuditEntries)
                .WithOne(entry => entry.Shipment)
                .HasForeignKey(entry => entry.ShipmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QualityShipmentAuditEntry>(entity =>
        {
            entity.ToTable("QualityShipmentAuditEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.EventType).HasMaxLength(80);
            entity.Property(entry => entry.FieldName).HasMaxLength(120);
            entity.Property(entry => entry.AccountName).HasMaxLength(160);
            entity.Property(entry => entry.DisplayName).HasMaxLength(160);
            entity.HasIndex(entry => new { entry.ShipmentId, entry.OccurredAt });
        });

        modelBuilder.Entity<QualityAssignmentRule>(entity =>
        {
            entity.ToTable("QualityAssignmentRules");
            entity.HasKey(rule => rule.Id);
            entity.Property(rule => rule.Name).HasMaxLength(160);
            entity.Property(rule => rule.MatchField).HasMaxLength(40);
            entity.Property(rule => rule.MatchOperator).HasMaxLength(40);
            entity.Property(rule => rule.MatchValue).HasMaxLength(240);
            entity.Property(rule => rule.TargetGroupName).HasMaxLength(160);
            entity.Property(rule => rule.AssignmentMode).HasMaxLength(40);
            entity.Property(rule => rule.TargetAccountName).HasMaxLength(160);
            entity.Property(rule => rule.TargetDisplayName).HasMaxLength(160);
            entity.Property(rule => rule.Version).IsConcurrencyToken();
            entity.HasIndex(rule => new { rule.IsEnabled, rule.Priority });
        });

        modelBuilder.Entity<QualityShippingLayoutPreference>(entity =>
        {
            entity.ToTable("QualityShippingLayoutPreferences");
            entity.HasKey(preference => preference.Id);
            entity.Property(preference => preference.AccountName).HasMaxLength(160);
            entity.Property(preference => preference.LayoutJson).HasMaxLength(12000);
            entity.Property(preference => preference.Version).IsConcurrencyToken();
            entity.HasIndex(preference => preference.AppUserId).IsUnique();
        });
    }
}
