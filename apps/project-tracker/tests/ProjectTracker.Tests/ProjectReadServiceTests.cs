using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectReadServiceTests
{
    [Fact]
    public async Task DashboardRead_DoesNotPersistComputedValuesOrIncrementVersions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var project = new Project
        {
            ProgramName = "READ-ONLY-TEST",
            ProgramStart = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)),
            Progress = 0m,
            Version = 7,
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Current operation",
                    StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)),
                    EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2)),
                    EstimatedDuration = 4,
                    PercentComplete = 0m,
                    Version = 11
                }
            ]
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var projectId = project.Id;
        var taskId = project.Tasks.Single().Id;
        db.ChangeTracker.Clear();

        var service = new ProjectReadService(db, new ProjectMetricsService(new ScheduleCalculator()));
        var dashboard = await service.DashboardAsync();

        Assert.Single(dashboard.Projects);
        Assert.Equal(7, dashboard.Projects[0].Version);
        db.ChangeTracker.Clear();
        Assert.Equal(7, await db.Projects.Where(item => item.Id == projectId).Select(item => item.Version).SingleAsync());
        Assert.Equal(11, await db.Tasks.Where(item => item.Id == taskId).Select(item => item.Version).SingleAsync());
    }

    [Fact]
    public async Task SoftDeleteFilters_HideArchivedGraphButRetainItForAdministration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var archived = new Project
        {
            ProgramName = "ARCHIVED-TEST",
            DeletedAt = DateTimeOffset.UtcNow,
            Tasks = [new ProjectTask { Sequence = 1, Title = "Retained operation" }],
            AuditEntries = [new ProjectAuditEntry { Action = "ProjectArchived", Summary = "Archived project" }]
        };
        db.Projects.AddRange(archived, new Project { ProgramName = "ACTIVE-TEST" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Single(await db.Projects.ToListAsync());
        Assert.Empty(await db.Tasks.ToListAsync());
        Assert.Empty(await db.ProjectAuditEntries.ToListAsync());
        Assert.Equal(2, await db.Projects.IgnoreQueryFilters().CountAsync());
        Assert.Single(await db.Tasks.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await db.ProjectAuditEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PreviewRead_ReturnsCompactOrderedActiveSnapshot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Projects.AddRange(
            new Project
            {
                ProgramName = "BRAVO",
                PriorityRank = 2,
                Tasks =
                [
                    new ProjectTask
                    {
                        Sequence = 1,
                        Title = "Late task",
                        StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-4)),
                        EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
                        OriginalEndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)),
                        EstimatedDuration = 4,
                        PercentComplete = 0.2m,
                        PercentCompleteManual = true
                    }
                ]
            },
            new Project
            {
                ProgramName = "ALPHA",
                PriorityRank = 1,
                Tasks =
                [
                    new ProjectTask
                    {
                        Sequence = 1,
                        Title = "Active task",
                        StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
                        EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(7)),
                        EstimatedDuration = 6,
                        PercentComplete = 0.6m,
                        PercentCompleteManual = true
                    }
                ]
            },
            new Project
            {
                ProgramName = "COMPLETE",
                PriorityRank = 3,
                CompletedOn = DateOnly.FromDateTime(DateTime.Today),
                Tasks =
                [
                    new ProjectTask
                    {
                        Sequence = 1,
                        Title = "Done task",
                        StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-3)),
                        EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
                        EstimatedDuration = 3,
                        PercentComplete = 1m
                    }
                ]
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ProjectReadService(db, new ProjectMetricsService(new ScheduleCalculator()));
        var preview = await service.PreviewAsync();

        Assert.Equal(2, preview.ActiveProjects);
        Assert.Equal(1, preview.OnTrack);
        Assert.Equal(1, preview.Behind);
        Assert.Equal(new[] { "ALPHA", "BRAVO" }, preview.Programs.Select(program => program.Name).ToArray());
        Assert.Equal(new[] { "onTrack", "behind" }, preview.Programs.Select(program => program.Status).ToArray());
    }
}
