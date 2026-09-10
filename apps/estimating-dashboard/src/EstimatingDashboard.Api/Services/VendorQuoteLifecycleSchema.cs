using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace EstimatingDashboard.Api.Services;

internal static class VendorQuoteLifecycleSchema
{
    private static readonly Dictionary<string, (string Name, bool Date)[]> Columns = new()
    {
        ["EstimatingVendorMessages"] = [("RemovedAt", true), ("RemovedBy", false), ("MovedAt", true)],
        ["EstimatingVendorActivities"] = [("EditedAt", true), ("EditedBy", false), ("RemovedAt", true), ("RemovedBy", false)],
        ["EstimatingQuoteStatusActivities"] = [("EditedAt", true), ("EditedBy", false), ("RemovedAt", true), ("RemovedBy", false)]
    };
    internal static async Task InitializeAsync(EstimatingAccessDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlServer()) { await db.Database.ExecuteSqlRawAsync(SqlServerSql(), ct); return; }
        if (!db.Database.IsSqlite()) return;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var table in Columns)
            {
                using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info([{table.Key}]);";
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(1));
                foreach (var column in table.Value.Where(x => !existing.Contains(x.Name)))
                {
                    // Both identifiers come exclusively from the fixed schema catalog above.
                    var ddl = $"ALTER TABLE [{table.Key}] ADD COLUMN [{column.Name}] TEXT NULL;";
                    await db.Database.ExecuteSqlRawAsync(ddl, ct);
                }
            }
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
    internal static string SqlServerSql() => string.Join("\n", Columns.SelectMany(table => table.Value.Select(column =>
        $"IF COL_LENGTH(N'{table.Key}', N'{column.Name}') IS NULL ALTER TABLE [{table.Key}] ADD [{column.Name}] {(column.Date ? "datetimeoffset" : "nvarchar(160)")} NULL;")))
        + "\n" + string.Join("\n", new[] { "EstimatingVendorActivities", "EstimatingQuoteStatusActivities" }.SelectMany(table => new[] { "OldValue", "NewValue" }.Select(column =>
            $"IF COL_LENGTH(N'{table}', N'{column}') < 8000 ALTER TABLE [{table}] ALTER COLUMN [{column}] nvarchar(4000) NULL;")));
}
