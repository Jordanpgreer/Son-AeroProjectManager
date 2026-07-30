using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringRoleDbContext(DbContextOptions<EngineeringRoleDbContext> options) : DbContext(options)
{
    public DbSet<EngineeringUserRecord> Users => Set<EngineeringUserRecord>();
    public DbSet<EngineeringModuleAccessRecord> UserModuleAccess => Set<EngineeringModuleAccessRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EngineeringUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
        });

        modelBuilder.Entity<EngineeringModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(40);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccessAssignments)
                .HasForeignKey(access => access.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class EngineeringUserRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public ICollection<EngineeringModuleAccessRecord> ModuleAccessAssignments { get; set; } = [];
}

public sealed class EngineeringModuleAccessRecord
{
    public int AppUserId { get; set; }
    public EngineeringUserRecord User { get; set; } = null!;
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
