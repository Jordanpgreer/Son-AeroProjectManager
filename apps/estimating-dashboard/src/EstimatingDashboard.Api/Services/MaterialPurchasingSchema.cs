using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

internal static class MaterialPurchasingSchema
{
    public static Task InitializeAsync(EstimatingAccessDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(CreateSql(db.Database.IsSqlite()), ct);

    internal static string CreateSql(bool sqlite)
    {
        var fields = sqlite ? """
            [QuoteHistoryId] INTEGER NOT NULL PRIMARY KEY,
            [QuantitiesJson] TEXT NOT NULL, [DraftsJson] TEXT NOT NULL,
            [Version] INTEGER NOT NULL, [UpdatedAt] TEXT NOT NULL, [UpdatedBy] TEXT NOT NULL,
            FOREIGN KEY ([QuoteHistoryId]) REFERENCES [EstimatingQuoteHistory] ([Id]) ON DELETE NO ACTION
            """ : """
            [QuoteHistoryId] int NOT NULL PRIMARY KEY,
            [QuantitiesJson] nvarchar(max) NOT NULL, [DraftsJson] nvarchar(max) NOT NULL,
            [Version] int NOT NULL, [UpdatedAt] datetimeoffset NOT NULL, [UpdatedBy] nvarchar(160) NOT NULL,
            FOREIGN KEY ([QuoteHistoryId]) REFERENCES [EstimatingQuoteHistory] ([Id]) ON DELETE NO ACTION
            """;
        return sqlite ? $"CREATE TABLE IF NOT EXISTS [EstimatingMaterialPurchasing] ({fields});"
            : $"IF OBJECT_ID(N'[EstimatingMaterialPurchasing]', N'U') IS NULL BEGIN CREATE TABLE [EstimatingMaterialPurchasing] ({fields}); END;";
    }
}
