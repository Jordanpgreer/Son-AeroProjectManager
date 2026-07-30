using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Data;

public sealed class EstimatingAccessDbContext(
    DbContextOptions<EstimatingAccessDbContext> options) : DbContext(options)
{
    public DbSet<EstimatingUserRecord> Users => Set<EstimatingUserRecord>();
    public DbSet<EstimatingModuleAccessRecord> UserModuleAccess =>
        Set<EstimatingModuleAccessRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EstimatingUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
        });

        modelBuilder.Entity<EstimatingModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(64);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccesses)
                .HasForeignKey(access => access.AppUserId);
        });
    }
}

public sealed class EstimatingUserRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public ICollection<EstimatingModuleAccessRecord> ModuleAccesses { get; set; } = [];
}

public sealed class EstimatingModuleAccessRecord
{
    public int AppUserId { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public EstimatingUserRecord User { get; set; } = null!;
}
