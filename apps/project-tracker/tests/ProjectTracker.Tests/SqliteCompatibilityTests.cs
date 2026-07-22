using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;

namespace ProjectTracker.Tests;

public sealed class SqliteCompatibilityTests
{
    [Fact]
    public async Task RepairsOrphanedStatusHistoryIndexesAndRecreatesTheTable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"project-tracker-{Guid.NewGuid():N}.db");
        try
        {
            await CreateMalformedLegacyDatabaseAsync(databasePath);
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var db = new ProjectTrackerDbContext(options))
            {
                await SqliteCompatibility.RepairLegacySchemaAsync(db, CancellationToken.None);
                await SqliteCompatibility.EnsureLegacyTablesAsync(db, CancellationToken.None);
            }

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE (type = 'table' AND name = 'StatusHistory')
                       OR (type = 'index' AND name IN ('IX_StatusHistory_ProjectId', 'IX_StatusHistory_ProjectTaskId'));
                    """;

                Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static async Task CreateMalformedLegacyDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "Projects" ("Id" INTEGER NOT NULL PRIMARY KEY);
            CREATE TABLE "Tasks" ("Id" INTEGER NOT NULL PRIMARY KEY);
            PRAGMA writable_schema=ON;
            INSERT INTO sqlite_schema (type, name, tbl_name, rootpage, sql)
            VALUES
                ('index', 'IX_StatusHistory_ProjectId', 'StatusHistory', 0, 'CREATE INDEX "IX_StatusHistory_ProjectId" ON "StatusHistory" ("ProjectId")'),
                ('index', 'IX_StatusHistory_ProjectTaskId', 'StatusHistory', 0, 'CREATE INDEX "IX_StatusHistory_ProjectTaskId" ON "StatusHistory" ("ProjectTaskId")');
            PRAGMA schema_version=2;
            PRAGMA writable_schema=OFF;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
