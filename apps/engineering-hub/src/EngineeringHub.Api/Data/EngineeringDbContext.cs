using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringDbContext(DbContextOptions<EngineeringDbContext> options) : DbContext(options)
{
    public bool AllowControlledDraftRevisionDeletion { get; set; }
    public bool AllowControlledEmptyDraftDrawingDeletion { get; set; }
    public DbSet<Drawing> Drawings => Set<Drawing>();
    public DbSet<DrawingRevision> DrawingRevisions => Set<DrawingRevision>();
    public DbSet<DrawingPart> DrawingParts => Set<DrawingPart>();
    public DbSet<DrawingDocumentLink> DrawingDocumentLinks => Set<DrawingDocumentLink>();
    public DbSet<DrawingValidation> DrawingValidations => Set<DrawingValidation>();
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
        modelBuilder.Entity<DrawingDocumentLink>().Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
        modelBuilder.Entity<DrawingDocumentLink>().HasOne(x => x.Drawing).WithMany(x => x.DocumentLinks).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DrawingValidation>().HasOne(x => x.Drawing).WithMany(x => x.Validations).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MylarTransaction>().Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        modelBuilder.Entity<MylarTransaction>().HasOne(x => x.Drawing).WithMany(x => x.MylarTransactions).HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Restrict);
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
                if (!AllowControlledDraftRevisionDeletion || originalStatus is not (DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview))
                    throw new InvalidOperationException("Only draft or under-review revisions may be deleted through the controlled deletion workflow.");
            }

            if (entry.State == EntityState.Modified)
            {
                var originalStatus = entry.OriginalValues.GetValue<DrawingRevisionStatus>(nameof(DrawingRevision.Status));
                if (originalStatus is DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete)
                    throw new InvalidOperationException("Historical drawing revisions are immutable.");
                if (originalStatus == DrawingRevisionStatus.Approved)
                {
                    var allowed = new[] { nameof(DrawingRevision.Status), nameof(DrawingRevision.SupersededOrObsoleteAt) };
                    if (entry.Entity.Status != DrawingRevisionStatus.Superseded ||
                        entry.Properties.Any(p => p.IsModified && !allowed.Contains(p.Metadata.Name)))
                        throw new InvalidOperationException("An approved revision may only transition to Superseded.");
                }
            }
        }

        if (ChangeTracker.Entries<DrawingAuditEntry>().Any(x => x.State == EntityState.Modified) ||
            (!AllowControlledEmptyDraftDrawingDeletion && ChangeTracker.Entries<DrawingAuditEntry>().Any(x => x.State == EntityState.Deleted)))
            throw new InvalidOperationException("Audit history is append-only.");
    }
}
