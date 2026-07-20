using Microsoft.EntityFrameworkCore;

namespace Portal.Api.Data;

public sealed class PortalRoleDbContext(DbContextOptions<PortalRoleDbContext> options) : DbContext(options)
{
    public DbSet<PortalRoleRecord> Users => Set<PortalRoleRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortalRoleRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
        });
    }
}

public sealed class PortalRoleRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
}
