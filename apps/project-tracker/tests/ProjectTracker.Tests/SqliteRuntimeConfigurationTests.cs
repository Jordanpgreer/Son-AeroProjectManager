using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;

namespace ProjectTracker.Tests;

public sealed class SqliteRuntimeConfigurationTests
{
    [Theory]
    [InlineData("3.41.2", false)]
    [InlineData("3.51.2", false)]
    [InlineData("unrecognized", false)]
    [InlineData("3.51.3", true)]
    [InlineData("3.53.3", true)]
    [InlineData("3.44.6", true)]
    [InlineData("3.50.7", true)]
    public async Task SharedDatabase_RejectsUnpatchedNativeLibraryBeforeEnablingWal(string version, bool patched)
    {
        var path = Path.Combine(Path.GetTempPath(), $"arda-wal-version-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            connection.CreateFunction("sqlite_version", () => version);
            await using var db = new ProjectTrackerDbContext(
                new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options);
            if (patched)
                await SqliteRuntimeConfiguration.EnableConcurrentReadsAsync(db, CancellationToken.None);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SqliteRuntimeConfiguration.EnableConcurrentReadsAsync(db, CancellationToken.None));

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal(patched ? "wal" : "delete", await command.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task SharedDatabase_AllowsCommitWhileAnotherModuleKeepsReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arda-concurrent-modules-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await using var db = new ProjectTrackerDbContext(
                new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connectionString).Options);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE SharedState (Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO SharedState VALUES (1, 'before');");
            await SqliteRuntimeConfiguration.EnableConcurrentReadsAsync(db, CancellationToken.None);
            await SqliteRuntimeConfiguration.EnableConcurrentReadsAsync(db, CancellationToken.None);

            await using var readerConnection = new SqliteConnection(connectionString);
            await readerConnection.OpenAsync();
            await using var readTransaction = readerConnection.BeginTransaction(deferred: true);
            await using var readCommand = readerConnection.CreateCommand();
            readCommand.Transaction = readTransaction;
            readCommand.CommandText = "SELECT Value FROM SharedState;";
            Assert.Equal("before", await readCommand.ExecuteScalarAsync());

            // Keep that module's read snapshot open while a different context commits a real edit.
            db.Database.SetCommandTimeout(1);
            await db.Database.ExecuteSqlRawAsync("UPDATE SharedState SET Value = 'after' WHERE Id = 1;");
            Assert.Equal("before", await readCommand.ExecuteScalarAsync());
            await readTransaction.CommitAsync();
            readCommand.Transaction = null;
            Assert.Equal("after", await readCommand.ExecuteScalarAsync());
            readCommand.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await readCommand.ExecuteScalarAsync());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=arda-test;Mode=Memory;Cache=Shared")]
    public async Task InMemoryDatabases_RemainSupported(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var db = new ProjectTrackerDbContext(
            new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options);

        await SqliteRuntimeConfiguration.EnableConcurrentReadsAsync(db, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("memory", await command.ExecuteScalarAsync());
    }
}
