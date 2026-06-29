using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectAuditServiceTests
{
    [Fact]
    public void Diff_ReturnsOnlyChangedFields()
    {
        var before = new Dictionary<string, string?>
        {
            ["Operation"] = "Machining",
            ["Work station"] = "CNC Mill",
            ["Completion"] = "25%"
        };
        var after = new Dictionary<string, string?>
        {
            ["Operation"] = "Machining",
            ["Work station"] = "CNC Mill 2",
            ["Completion"] = "50%"
        };

        var changes = ProjectAuditService.Diff(before, after);

        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal("Work station", change.Field);
                Assert.Equal("CNC Mill", change.OldValue);
                Assert.Equal("CNC Mill 2", change.NewValue);
            },
            change =>
            {
                Assert.Equal("Completion", change.Field);
                Assert.Equal("25%", change.OldValue);
                Assert.Equal("50%", change.NewValue);
            });
    }

    [Fact]
    public async Task AuditEntry_PersistsAndIsDeletedWithProject()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var project = new Project
        {
            ProgramName = "Audit persistence test",
            AuditEntries =
            [
                new ProjectAuditEntry
                {
                    Action = "ProjectUpdated",
                    Summary = "Updated project details",
                    ChangesJson = "[{\"field\":\"Manager\",\"oldValue\":\"A\",\"newValue\":\"B\"}]",
                    ChangedByAccountName = "SON-AERO\\jgreer",
                    ChangedByDisplayName = "Jordan Greer"
                }
            ]
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var saved = await db.ProjectAuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal("ProjectUpdated", saved.Action);
        Assert.Single(ProjectAuditService.ReadChanges(saved.ChangesJson));

        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        Assert.Empty(await db.ProjectAuditEntries.AsNoTracking().ToListAsync());
    }
}
