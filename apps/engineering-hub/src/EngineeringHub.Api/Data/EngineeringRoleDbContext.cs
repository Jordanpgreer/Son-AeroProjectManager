using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringRoleDbContext(DbContextOptions<EngineeringRoleDbContext> options) : DbContext(options)
{
    public DbSet<EngineeringRoleRecord> Users => Set<EngineeringRoleRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EngineeringRoleRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
        });
    }
}

public sealed class EngineeringRoleRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
}
