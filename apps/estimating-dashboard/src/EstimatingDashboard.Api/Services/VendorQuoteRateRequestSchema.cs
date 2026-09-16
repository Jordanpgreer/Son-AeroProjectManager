using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace EstimatingDashboard.Api.Services;

internal static class VendorQuoteRateRequestSchema
{
    internal static async Task InitializeAsync(EstimatingAccessDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH(N'EstimatingVendorMessages', N'IsRateRequest') IS NULL
                ALTER TABLE [EstimatingVendorMessages] ADD [IsRateRequest] bit NOT NULL
                CONSTRAINT [DF_EstimatingVendorMessages_IsRateRequest] DEFAULT(0);
                """, ct);
            return;
        }
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
                    if (reader.GetString(1).Equals("IsRateRequest", StringComparison.OrdinalIgnoreCase)) exists = true;
            if (!exists) await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [EstimatingVendorMessages] ADD COLUMN [IsRateRequest] INTEGER NOT NULL DEFAULT 0;", ct);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
