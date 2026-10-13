using System.Data;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

internal static class MaterialDraftTokenSchema
{
    internal const string SqlServer = """
        IF COL_LENGTH(N'EstimatingVendorMessages', N'MaterialDraftToken') IS NULL
        ALTER TABLE [EstimatingVendorMessages] ADD [MaterialDraftToken] nvarchar(32) NULL;
        """;
    public static async Task InitializeAsync(EstimatingAccessDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlServer()) { await db.Database.ExecuteSqlRawAsync(SqlServer, ct); return; }
        if (!db.Database.IsSqlite()) return;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info([EstimatingVendorMessages]);";
            var exists = false;
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    if (reader.GetString(1).Equals("MaterialDraftToken", StringComparison.OrdinalIgnoreCase)) exists = true;
            if (!exists) await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [EstimatingVendorMessages] ADD COLUMN [MaterialDraftToken] TEXT NULL;", ct);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
