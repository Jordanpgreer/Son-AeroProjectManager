using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Data;

namespace QualityAssurance.Tests;

public sealed class QualitySqliteMigrationTests
{
    [Fact]
    public async Task Precreated_readwrite_file_accepts_the_full_quality_migration_chain()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"quality-migration-{Guid.NewGuid():N}.db");
        File.Create(databasePath).Dispose();

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                DefaultTimeout = 30,
                ForeignKeys = true,
                Pooling = true
            }.ToString();
            var options = new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using (var db = new QualityAssuranceDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.OpenConnectionAsync();

                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText =
                    "SELECT name FROM sqlite_master " +
                    "WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync();
                var actualTables = new List<string>();
                while (await reader.ReadAsync())
                    actualTables.Add(reader.GetString(0));

                var expectedTables = new[]
                {
                    "QualityAssignmentRules",
                    "QualityMentionNotifications",
                    "QualityShipmentAuditEntries",
                    "QualityShipmentComments",
                    "QualityShipments",
                    "QualityShippingLayoutPreferences",
                    "__EFMigrationsHistory"
                };
                Assert.Equal(expectedTables, actualTables);
            }

            Assert.True(new FileInfo(databasePath).Length > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public void Readwrite_mode_does_not_recreate_a_missing_database()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"quality-missing-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Pooling = true
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        Assert.Throws<SqliteException>(() => connection.Open());
        Assert.False(File.Exists(databasePath));
    }
}
