using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class ProjectMessagePersistenceTests
{
    [Fact]
    public async Task ProjectMessage_PersistsAndIsDeletedWithProject()
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
            ProgramName = "Chat persistence test",
            PriorityRank = 1,
            Messages =
            [
                new ProjectMessage
                {
                    AuthorAccountName = "SON-AERO\\jgreer",
                    AuthorDisplayName = "Jordan Greer",
                    Body = "@asmith Schedule review is ready."
                }
            ]
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var saved = await db.ProjectMessages.AsNoTracking().SingleAsync();
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal("Jordan Greer", saved.AuthorDisplayName);
        Assert.Contains("@asmith", saved.Body);

        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        Assert.Empty(await db.ProjectMessages.AsNoTracking().ToListAsync());
    }
}
