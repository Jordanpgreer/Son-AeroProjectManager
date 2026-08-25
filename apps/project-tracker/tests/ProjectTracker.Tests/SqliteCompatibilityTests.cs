using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class SqliteCompatibilityTests
{
    [Fact]
    public async Task LegacyRequiredUserRoleColumn_UsesViewerDefaultForNewUsers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "Users" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                    "AccountName" TEXT NOT NULL,
                    "DisplayName" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "IsActive" INTEGER NOT NULL DEFAULT 1,
                    "LastSeenAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_Users_AccountName" ON "Users" ("AccountName");
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new ProjectTrackerDbContext(options))
        {
            db.Users.Add(new AppUser
            {
                AccountName = @"SON-AERO\new.user",
                DisplayName = "New User",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        await using var roleCommand = connection.CreateCommand();
        roleCommand.CommandText = "SELECT \"Role\" FROM \"Users\" WHERE \"AccountName\" = 'SON-AERO\\new.user';";
        Assert.Equal("Viewer", Convert.ToString(await roleCommand.ExecuteScalarAsync()));
    }

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

    [Fact]
    public async Task EnsureAccessControlTables_CreatesUserModuleAccessForLegacyDatabases()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"project-tracker-module-access-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "Users" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                        "AccountName" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "Role" TEXT NOT NULL,
                        "IsActive" INTEGER NOT NULL DEFAULT 1,
                        "LastSeenAt" TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var db = new ProjectTrackerDbContext(options))
            {
                await SqliteCompatibility.EnsureAccessControlTablesAsync(
                    db,
                    CancellationToken.None);
            }

            await using var checkConnection = new SqliteConnection($"Data Source={databasePath}");
            await checkConnection.OpenAsync();
            await using var tableCheck = checkConnection.CreateCommand();
            tableCheck.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE type = 'table' AND name = 'UserModuleAccess';
                """;
            Assert.Equal(1, Convert.ToInt32(await tableCheck.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task EnsureAccessControlTables_CreatesPushSubscriptionsForLegacyDatabases()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"project-tracker-push-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "Users" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                        "AccountName" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "Role" TEXT NOT NULL,
                        "IsActive" INTEGER NOT NULL DEFAULT 1,
                        "LastSeenAt" TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite($"Data Source={databasePath}").Options;
            await using (var db = new ProjectTrackerDbContext(options))
            {
                await SqliteCompatibility.EnsureAccessControlTablesAsync(db, CancellationToken.None);
            }

            await using var check = new SqliteConnection($"Data Source={databasePath}");
            await check.OpenAsync();
            await using var tableCheck = check.CreateCommand();
            tableCheck.CommandText = """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE (type = 'table' AND name = 'PushSubscriptions')
                   OR (type = 'index' AND name IN ('IX_PushSubscriptions_Endpoint', 'IX_PushSubscriptions_AppUserId'));
                """;
            Assert.Equal(3, Convert.ToInt32(await tableCheck.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task EnsureLegacyTables_CreatesWalkthroughFeatureSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);

        await SqliteCompatibility.EnsureLegacyTablesAsync(db, CancellationToken.None);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'FeatureSettings';";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task EnsureFeatureSettingsColumns_UpgradesExistingWalkthroughSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FeatureSettings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FeatureSettings" PRIMARY KEY,
                    "WalkthroughEnabled" INTEGER NOT NULL DEFAULT 1,
                    "UpdatedAt" TEXT NOT NULL
                );
                INSERT INTO "FeatureSettings" ("Id", "WalkthroughEnabled", "UpdatedAt")
                VALUES (1, 1, CURRENT_TIMESTAMP);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);

        await SqliteCompatibility.EnsureFeatureSettingsColumnsAsync(db, CancellationToken.None);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT \"AssistantEnabled\", \"AssistantName\" FROM \"FeatureSettings\" WHERE \"Id\" = 1;";
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("Benny", reader.GetString(1));
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
