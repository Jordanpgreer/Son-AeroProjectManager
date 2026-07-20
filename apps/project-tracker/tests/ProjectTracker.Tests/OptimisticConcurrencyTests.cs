using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class OptimisticConcurrencyTests
{
    [Fact]
    public async Task ProjectSave_RejectsAStaleVersion()
    {
        await using var database = await ConcurrencyDatabase.CreateAsync();
        var projectId = await database.AddProjectAsync();

        await using var firstEditor = database.CreateContext();
        await using var secondEditor = database.CreateContext();
        var firstCopy = await firstEditor.Projects.SingleAsync(project => project.Id == projectId);
        var staleCopy = await secondEditor.Projects.SingleAsync(project => project.Id == projectId);

        firstCopy.ProgramManager = "First editor";
        firstCopy.Version++;
        await firstEditor.SaveChangesAsync();

        staleCopy.ProgramManager = "Second editor";
        staleCopy.Version++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondEditor.SaveChangesAsync());
    }

    [Fact]
    public async Task OperationSave_RejectsAStaleVersion()
    {
        await using var database = await ConcurrencyDatabase.CreateAsync();
        var projectId = await database.AddProjectAsync();

        await using var firstEditor = database.CreateContext();
        await using var secondEditor = database.CreateContext();
        var firstCopy = await firstEditor.Tasks.SingleAsync(task => task.ProjectId == projectId);
        var staleCopy = await secondEditor.Tasks.SingleAsync(task => task.ProjectId == projectId);

        firstCopy.Notes = "First editor's note";
        firstCopy.Version++;
        await firstEditor.SaveChangesAsync();

        staleCopy.Notes = "Second editor's note";
        staleCopy.Version++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondEditor.SaveChangesAsync());
    }

    private sealed class ConcurrencyDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ProjectTrackerDbContext> options;

        private ConcurrencyDatabase(SqliteConnection connection, DbContextOptions<ProjectTrackerDbContext> options)
        {
            this.connection = connection;
            this.options = options;
        }

        public static async Task<ConcurrencyDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new ConcurrencyDatabase(connection, options);
        }

        public ProjectTrackerDbContext CreateContext() => new(options);

        public async Task<int> AddProjectAsync()
        {
            await using var db = CreateContext();
            var project = new Project
            {
                ProgramName = $"Concurrency test {Guid.NewGuid():N}",
                Tasks = [new ProjectTask { Sequence = 1, Title = "Operation 1" }]
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return project.Id;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
