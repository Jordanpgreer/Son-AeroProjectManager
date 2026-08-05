using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringDbContext(DbContextOptions<EngineeringDbContext> options) : DbContext(options)
{
    public bool AllowControlledDraftRevisionDeletion { get; set; }
    public bool AllowControlledHistoricalRevisionDeletion { get; set; }
    public bool AllowControlledHistoricalRevisionActivation { get; set; }
    public bool AllowControlledRevisionReopen { get; set; }
    public bool AllowControlledEmptyDraftDrawingDeletion { get; set; }
    public bool AllowLegacyMylarBackfill { get; set; }
    public DbSet<Drawing> Drawings => Set<Drawing>();
    public DbSet<DrawingRevision> DrawingRevisions => Set<DrawingRevision>();
    public DbSet<DrawingPart> DrawingParts => Set<DrawingPart>();
    public DbSet<DrawingDocumentLink> DrawingDocumentLinks => Set<DrawingDocumentLink>();
    public DbSet<DrawingValidation> DrawingValidations => Set<DrawingValidation>();
    public DbSet<DrawingMylar> DrawingMylars => Set<DrawingMylar>();
    public DbSet<MylarTransaction> MylarTransactions => Set<MylarTransaction>();
    public DbSet<DrawingAuditEntry> DrawingAuditEntries => Set<DrawingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Drawing>(entity =>
        {
            entity.HasIndex(x => new { x.NormalizedCustomer, x.NormalizedDrawingNumber }).IsUnique();
            entity.Property(x => x.DrawingNumber).HasMaxLength(100);
            entity.Property(x => x.NormalizedDrawingNumber).HasMaxLength(100);
            entity.Property(x => x.Customer).HasMaxLength(200);
            entity.Property(x => x.NormalizedCustomer).HasMaxLength(200);
            entity.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.CurrentApprovedRevision).WithMany().HasForeignKey(x => x.CurrentApprovedRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DrawingRevision>(entity =>
        {
            entity.HasIndex(x => new { x.DrawingId, x.RevisionNumber }).IsUnique();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.Drawing).WithMany(x => x.Revisions).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DrawingPart>().HasIndex(x => new { x.DrawingId, x.PartNumber }).IsUnique();
        modelBuilder.Entity<DrawingPart>().HasOne(x => x.Drawing).WithMany(x => x.Parts).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DrawingDocumentLink>(entity =>
        {
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(x => x.DrawingRevisionId);
            entity.HasOne(x => x.Drawing).WithMany(x => x.DocumentLinks).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.DrawingRevision).WithMany(x => x.DocumentLinks).HasForeignKey(x => x.DrawingRevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DrawingValidation>().HasOne(x => x.Drawing).WithMany(x => x.Validations).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DrawingMylar>(entity =>
        {
            entity.HasIndex(x => x.DrawingId).IsUnique();
            entity.Property(x => x.MylarNumber).HasMaxLength(100);
            entity.Property(x => x.NormalizedMylarNumber).HasMaxLength(100);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.Drawing).WithMany(x => x.Mylars).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MylarTransaction>(entity =>
        {
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(x => x.Drawing).WithMany(x => x.MylarTransactions).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Mylar).WithMany(x => x.Transactions).HasForeignKey(x => x.DrawingMylarId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DrawingAuditEntry>().HasOne(x => x.Drawing).WithMany(x => x.AuditEntries).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforcePermanentRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforcePermanentRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforcePermanentRecords()
    {
        foreach (var entry in ChangeTracker.Entries<Drawing>().Where(x => x.State == EntityState.Deleted))
        {
            var originalStatus = entry.OriginalValues.GetValue<DrawingApprovalStatus>(nameof(Drawing.ApprovalStatus));
            if (!AllowControlledEmptyDraftDrawingDeletion || originalStatus != DrawingApprovalStatus.Draft)
                throw new InvalidOperationException("Only empty draft drawings may be deleted through the controlled deletion workflow.");
        }

        foreach (var entry in ChangeTracker.Entries<DrawingRevision>())
        {
            if (entry.State == EntityState.Deleted)
            {
                var originalStatus = entry.OriginalValues.GetValue<DrawingRevisionStatus>(nameof(DrawingRevision.Status));
                var draftDeletion = AllowControlledDraftRevisionDeletion &&
                    originalStatus is DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview;
                var historicalDeletion = AllowControlledHistoricalRevisionDeletion &&
                    originalStatus is DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete;
                if (!draftDeletion && !historicalDeletion)
                    throw new InvalidOperationException("Only non-current revisions may be deleted through the controlled deletion workflow.");
            }

            if (entry.State == EntityState.Modified)
            {
                var originalStatus = entry.OriginalValues.GetValue<DrawingRevisionStatus>(nameof(DrawingRevision.Status));
                var controlledReopen = AllowControlledRevisionReopen &&
                    entry.Entity.Status == DrawingRevisionStatus.Draft;
                if (originalStatus is DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete)
                {
                    var allowed = new[] { nameof(DrawingRevision.Status), nameof(DrawingRevision.SupersededOrObsoleteAt) };
                    if (!controlledReopen &&
                        (!AllowControlledHistoricalRevisionActivation ||
                        entry.Entity.Status != DrawingRevisionStatus.Approved ||
                        entry.Properties.Any(p => p.IsModified && !allowed.Contains(p.Metadata.Name))))
                        throw new InvalidOperationException("Historical drawing revisions are immutable outside the controlled activation workflow.");
                }
                if (originalStatus == DrawingRevisionStatus.Approved)
                {
                    var allowed = new[] { nameof(DrawingRevision.Status), nameof(DrawingRevision.SupersededOrObsoleteAt) };
                    if (!controlledReopen &&
                        (entry.Entity.Status is not (DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete) ||
                         entry.Properties.Any(p => p.IsModified && !allowed.Contains(p.Metadata.Name))))
                        throw new InvalidOperationException("An approved revision may only transition to Superseded or Archived.");
                }
            }
        }

        if (ChangeTracker.Entries<DrawingAuditEntry>().Any(x => x.State == EntityState.Modified) ||
            (!AllowControlledEmptyDraftDrawingDeletion && ChangeTracker.Entries<DrawingAuditEntry>().Any(x => x.State == EntityState.Deleted)))
            throw new InvalidOperationException("Audit history is append-only.");

        if (!AllowLegacyMylarBackfill &&
            ChangeTracker.Entries<MylarTransaction>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Mylar custody history is append-only.");
    }
}
