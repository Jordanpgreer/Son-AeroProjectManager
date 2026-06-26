using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Data;

public sealed class ProjectTrackerDbContext(DbContextOptions<ProjectTrackerDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> Tasks => Set<ProjectTask>();
    public DbSet<Phase> Phases => Set<Phase>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(project => project.ProgramName).IsUnique();
            entity.Property(project => project.ProgramName).HasMaxLength(160);
            entity.Property(project => project.ProgramManager).HasMaxLength(120);
            entity.Property(project => project.Progress).HasPrecision(5, 4);
            entity.Property(project => project.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(project => project.CurrentTask).HasMaxLength(240);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.HasIndex(task => new { task.ProjectId, task.Sequence });
            entity.Property(task => task.ExternalTaskId).HasMaxLength(32);
            entity.Property(task => task.Title).HasMaxLength(240);
            entity.Property(task => task.Phase).HasMaxLength(120);
            entity.Property(task => task.WorkStation).HasMaxLength(120);
            entity.Property(task => task.PercentComplete).HasPrecision(5, 4);
            entity.Property(task => task.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(task => task.Notes).HasMaxLength(2000);
            entity.HasOne(task => task.Project)
                .WithMany(project => project.Tasks)
                .HasForeignKey(task => task.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Phase>(entity =>
        {
            entity.HasIndex(phase => phase.Name).IsUnique();
            entity.Property(phase => phase.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<Holiday>(entity =>
        {
            entity.HasIndex(holiday => holiday.Date).IsUnique();
            entity.Property(holiday => holiday.Name).HasMaxLength(160);
        });

        modelBuilder.Entity<WorkCenter>(entity =>
        {
            entity.HasIndex(workCenter => workCenter.Name).IsUnique();
            entity.Property(workCenter => workCenter.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(user => user.AccountName).IsUnique();
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
        });

        modelBuilder.Entity<StatusHistory>(entity =>
        {
            entity.Property(history => history.EntityName).HasMaxLength(240);
            entity.Property(history => history.OldStatus).HasMaxLength(32);
            entity.Property(history => history.NewStatus).HasMaxLength(32);
            entity.Property(history => history.ChangedBy).HasMaxLength(160);
            entity.HasOne(history => history.Project)
                .WithMany()
                .HasForeignKey(history => history.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(history => history.ProjectTask)
                .WithMany()
                .HasForeignKey(history => history.ProjectTaskId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}

