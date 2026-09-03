using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
                    "QualityShipmentParts",
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
    public async Task Parts_migration_backfills_existing_single_part_shipments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<QualityAssuranceDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new QualityAssuranceDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260901133114_AddLegacyQualityAssigneeTags");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO QualityShipments
                (Status, SalesOrderNumber, PartNumber, Customer, TaskType, Quantity, DollarValue,
                 IsShipped, CreatedAt, CreatedByAccountName, CreatedByDisplayName, UpdatedAt,
                 UpdatedByAccountName, UpdatedByDisplayName, Version)
            VALUES
                ('WIP', 'SHIP-LEGACY', 'PART-42', 'Legacy Customer', 'General', 4, 50.00,
                 0, '2026-09-03T00:00:00+00:00', 'TEST\\admin', 'Admin',
                 '2026-09-03T00:00:00+00:00', 'TEST\\admin', 'Admin', 1);
            """);

        await migrator.MigrateAsync();

        var part = await db.ShipmentParts.SingleAsync();
        Assert.Equal("PART-42", part.PartNumber);
        Assert.Equal(4, part.Quantity);
        Assert.Equal(12.50m, part.UnitPrice);
        Assert.Equal(50.00m, part.TotalValue);
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
