using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmallBusinessSubcontracting.Api;

public static class SubcontractingDatabaseInitializer
{
    public const string InitialMigration = "20261001192605_InitialSubcontracting";

    public static async Task InitializeAsync(SubcontractingDbContext db, bool isDevelopment,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlite())
        {
            if (!isDevelopment) throw new InvalidOperationException("SQLite is only supported in Development.");
            await BaselineLegacySqliteAsync(db, cancellationToken);
        }
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BaselineLegacySqliteAsync(SubcontractingDbContext db, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            if ((await db.Database.GetAppliedMigrationsAsync(token)).Any()) return;
            var actual = await ReadSchemaAsync(db.Database.GetDbConnection(), token);
            if (actual.Count == 0) return;

            // The original development build used EnsureCreated. Compare its entire schema
            // with the immutable initial migration before recording that migration. Never
            // infer a baseline from one table, recreate a database, or discard local rows.
            await using var referenceConnection = new SqliteConnection("Data Source=:memory:");
            await referenceConnection.OpenAsync(token);
            await using var reference = new SubcontractingDbContext(
                new DbContextOptionsBuilder<SubcontractingDbContext>().UseSqlite(referenceConnection).Options);
            await reference.GetService<IMigrator>().MigrateAsync(InitialMigration, token);
            var expected = await ReadSchemaAsync(referenceConnection, token);
            if (!actual.SequenceEqual(expected))
                throw new InvalidOperationException(
                    "The existing development SQLite schema does not match the initial migration. " +
                    "The database has been preserved; back it up and reconcile its schema before continuing.");

            var history = db.GetService<IHistoryRepository>();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), token);
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(
                new HistoryRow(InitialMigration, ProductInfo.GetVersion())), token);
            await transaction.CommitAsync(token);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<List<string>> ReadSchemaAsync(DbConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, sql FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%' AND tbl_name <> '__EFMigrationsHistory'
            ORDER BY type, name
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var schema = new List<string>();
        while (await reader.ReadAsync(token))
            schema.Add(string.Join('|', Enumerable.Range(0, 4).Select(index =>
                reader.IsDBNull(index) ? "" : Regex.Replace(reader.GetString(index), @"\s+", " ").Trim())));
        return schema;
    }
}
