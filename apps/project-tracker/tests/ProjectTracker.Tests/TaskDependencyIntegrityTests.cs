using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class TaskDependencyIntegrityTests
{
    [Fact]
    public async Task DatabasePreventsDeletingOperationWhileDependentsStillReferenceIt()
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
            ProgramName = "Dependency protection",
            Tasks =
            [
                new ProjectTask { Sequence = 1, Title = "First" },
                new ProjectTask { Sequence = 2, Title = "Second" }
            ]
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        project.Tasks[1].DependencyTaskId = project.Tasks[0].Id;
        await db.SaveChangesAsync();

        var dependencyId = project.Tasks[0].Id;
        db.ChangeTracker.Clear();
        db.Tasks.Remove(await db.Tasks.SingleAsync(task => task.Id == dependencyId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ClearingDependentReferenceAllowsOperationDeletion()
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
            ProgramName = "Confirmed dependency reset",
            Tasks =
            [
                new ProjectTask { Sequence = 1, Title = "First" },
                new ProjectTask { Sequence = 2, Title = "Second" }
            ]
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var removed = project.Tasks[0];
        project.Tasks[1].DependencyTaskId = removed.Id;
        await db.SaveChangesAsync();
        project.Tasks[1].DependencyTaskId = null;
        db.Tasks.Remove(removed);
        await db.SaveChangesAsync();

        Assert.Single(await db.Tasks.ToListAsync());
        Assert.Null((await db.Tasks.SingleAsync()).DependencyTaskId);
    }
}
