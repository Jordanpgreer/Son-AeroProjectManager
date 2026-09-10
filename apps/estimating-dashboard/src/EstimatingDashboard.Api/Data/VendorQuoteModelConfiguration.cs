using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Data;

internal static class VendorQuoteModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<VendorQuoteRequest>(e =>
        {
            e.ToTable("EstimatingVendorRequests");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.QuoteHistoryId, x.ThreadKey }).IsUnique();
            e.Property(x => x.ThreadKey).HasMaxLength(64);
            e.Property(x => x.PartNumber).HasMaxLength(160);
            e.Property(x => x.VendorEmail).HasMaxLength(254);
            e.Property(x => x.VendorName).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(240);
            e.Property(x => x.Status).HasMaxLength(40);
            e.Property(x => x.StatusChangedBy).HasMaxLength(160);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.QuoteHistory).WithMany().HasForeignKey(x => x.QuoteHistoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<VendorQuoteMessage>(e =>
        {
            e.ToTable("EstimatingVendorMessages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeduplicationKey).IsUnique();
            e.Property(x => x.DeduplicationKey).HasMaxLength(64);
            e.Property(x => x.SourceMessageId).HasMaxLength(1024);
            e.Property(x => x.Mailbox).HasMaxLength(254);
            e.Property(x => x.Direction).HasMaxLength(16);
            e.Property(x => x.Subject).HasMaxLength(998);
            e.Property(x => x.FromAddress).HasMaxLength(254);
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.ImportedBy).HasMaxLength(160);
            e.Property(x => x.RemovedBy).HasMaxLength(160);
            e.Property(x => x.VendorEmail).HasMaxLength(254);
            e.Property(x => x.ConversationId).HasMaxLength(512);
            e.HasOne(x => x.Request).WithMany(x => x.Messages).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.QuoteHistory).WithMany().HasForeignKey(x => x.QuoteHistoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<VendorQuoteAttachment>(e =>
        {
            e.ToTable("EstimatingVendorAttachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(180);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasOne(x => x.Message).WithMany(x => x.Attachments).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<VendorQuoteActivity>(e =>
        {
            e.ToTable("EstimatingVendorActivities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.OldValue).HasMaxLength(4000);
            e.Property(x => x.NewValue).HasMaxLength(4000);
            e.Property(x => x.AccountName).HasMaxLength(160);
            e.Property(x => x.DisplayName).HasMaxLength(160);
            e.Property(x => x.EditedBy).HasMaxLength(160);
            e.Property(x => x.RemovedBy).HasMaxLength(160);
            e.HasOne(x => x.Request).WithMany(x => x.Activity).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<VendorQuoteSyncState>(e =>
        {
            e.ToTable("EstimatingVendorSyncStates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AccountName, x.Mailbox, x.ClientName }).IsUnique();
            e.Property(x => x.AccountName).HasMaxLength(160);
            e.Property(x => x.Mailbox).HasMaxLength(254);
            e.Property(x => x.ClientName).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(500);
        });
        model.Entity<QuoteStatusMetadata>(e =>
        {
            e.ToTable("EstimatingQuoteStatusMetadata");
            e.HasKey(x => x.QuoteHistoryId);
            e.HasOne(x => x.QuoteHistory).WithOne().HasForeignKey<QuoteStatusMetadata>(x => x.QuoteHistoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<QuoteStatusActivity>(e =>
        {
            e.ToTable("EstimatingQuoteStatusActivities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.OldValue).HasMaxLength(4000);
            e.Property(x => x.NewValue).HasMaxLength(4000);
            e.Property(x => x.AccountName).HasMaxLength(160);
            e.Property(x => x.DisplayName).HasMaxLength(160);
            e.Property(x => x.EditedBy).HasMaxLength(160);
            e.Property(x => x.RemovedBy).HasMaxLength(160);
            e.HasOne(x => x.QuoteHistory).WithMany().HasForeignKey(x => x.QuoteHistoryId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
