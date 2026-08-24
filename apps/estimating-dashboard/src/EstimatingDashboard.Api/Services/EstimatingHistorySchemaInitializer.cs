using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistorySchemaInitializer(EstimatingAccessDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
            await EnsureSqliteHistoryColumnsAsync(cancellationToken);
            return;
        }

        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
    }

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "EstimatingHistoryImportBatches" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_EstimatingHistoryImportBatches" PRIMARY KEY,
            "FileName" TEXT NOT NULL,
            "FileHash" TEXT NOT NULL,
            "ImportedBy" TEXT NOT NULL,
            "ImportedAt" TEXT NOT NULL,
            "TotalRows" INTEGER NOT NULL,
            "NewRecords" INTEGER NOT NULL,
            "UpdatedRecords" INTEGER NOT NULL,
            "UnchangedRecords" INTEGER NOT NULL,
            "SkippedRows" INTEGER NOT NULL,
            "ErrorRows" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_EstimatingHistoryImportBatches_ImportedAt"
            ON "EstimatingHistoryImportBatches" ("ImportedAt");

        CREATE TABLE IF NOT EXISTS "EstimatingQuoteHistory" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EstimatingQuoteHistory" PRIMARY KEY AUTOINCREMENT,
            "SourceId" TEXT NOT NULL,
            "QuoteNumber" INTEGER NOT NULL,
            "Customer" TEXT NOT NULL,
            "CustomerContact" TEXT NULL,
            "SalesPerson" TEXT NOT NULL,
            "QuoteStatus" TEXT NOT NULL,
            "RfqReferenceNumber" TEXT NULL,
            "EstimatingRep" TEXT NOT NULL,
            "TotalValue" TEXT NOT NULL,
            "RfqDueDate" TEXT NULL,
            "DateToEstimating" TEXT NULL,
            "Issues" TEXT NULL,
            "QuoteOnTrack" TEXT NULL,
            "QuoteComplexity" TEXT NULL,
            "NumberOfParts" INTEGER NOT NULL,
            "EstimatingStatus" TEXT NULL,
            "EstimatingCompletionDate" TEXT NULL,
            "OnTimeStatus" TEXT NOT NULL,
            "DaysLate" INTEGER NOT NULL,
            "Workdays" INTEGER NULL,
            "CompletedMonth" TEXT NULL,
            "CompletedYear" INTEGER NULL,
            "CompletedWeekOfMonth" INTEGER NULL,
            "CompletedMonthAndWeek" TEXT NULL,
            "IsCompleted" INTEGER NOT NULL,
            "CompletedWeekOfYear" INTEGER NULL,
            "IsOnTime" INTEGER NOT NULL,
            "OnTimeRatio" TEXT NULL,
            "LastImportBatchId" TEXT NOT NULL,
            "FirstImportedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            "Version" INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistory_SourceId"
            ON "EstimatingQuoteHistory" ("SourceId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistory_QuoteNumber"
            ON "EstimatingQuoteHistory" ("QuoteNumber");
        CREATE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistory_EstimatingRep"
            ON "EstimatingQuoteHistory" ("EstimatingRep");
        CREATE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistory_EstimatingCompletionDate"
            ON "EstimatingQuoteHistory" ("EstimatingCompletionDate");
        CREATE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistory_IsCompleted"
            ON "EstimatingQuoteHistory" ("IsCompleted");

        CREATE TABLE IF NOT EXISTS "EstimatingQuoteHistoryAudits" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EstimatingQuoteHistoryAudits" PRIMARY KEY AUTOINCREMENT,
            "QuoteHistoryId" INTEGER NOT NULL,
            "QuoteNumber" INTEGER NOT NULL,
            "ImportBatchId" TEXT NOT NULL,
            "Action" TEXT NOT NULL,
            "FieldName" TEXT NOT NULL,
            "OldValue" TEXT NULL,
            "NewValue" TEXT NULL,
            "ChangedBy" TEXT NOT NULL,
            "ChangedAt" TEXT NOT NULL,
            CONSTRAINT "FK_EstimatingQuoteHistoryAudits_EstimatingQuoteHistory_QuoteHistoryId"
                FOREIGN KEY ("QuoteHistoryId") REFERENCES "EstimatingQuoteHistory" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistoryAudits_QuoteHistoryId_ChangedAt"
            ON "EstimatingQuoteHistoryAudits" ("QuoteHistoryId", "ChangedAt");
        CREATE INDEX IF NOT EXISTS "IX_EstimatingQuoteHistoryAudits_ImportBatchId"
            ON "EstimatingQuoteHistoryAudits" ("ImportBatchId");

        INSERT OR IGNORE INTO "GroupPermissions" ("AppGroupId", "PermissionKey", "CreatedAt")
        SELECT DISTINCT source."AppGroupId", 'estimating.history.view', CURRENT_TIMESTAMP
        FROM "GroupPermissions" source
        WHERE source."PermissionKey" = 'estimating.view';

        INSERT OR IGNORE INTO "GroupPermissions" ("AppGroupId", "PermissionKey", "CreatedAt")
        SELECT DISTINCT source."AppGroupId", 'estimating.history.import', CURRENT_TIMESTAMP
        FROM "GroupPermissions" source
        WHERE source."PermissionKey" IN (
            'estimating.quotes.manage',
            'estimating.inputs.manage',
            'estimating.rates.admin',
            'estimating.settings.admin');
        """;

    private const string SqlServerSchema = """
        IF OBJECT_ID(N'[EstimatingHistoryImportBatches]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingHistoryImportBatches] (
                [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_EstimatingHistoryImportBatches] PRIMARY KEY,
                [FileName] nvarchar(240) NOT NULL,
                [FileHash] nvarchar(64) NOT NULL,
                [ImportedBy] nvarchar(160) NOT NULL,
                [ImportedAt] datetimeoffset NOT NULL,
                [TotalRows] int NOT NULL,
                [NewRecords] int NOT NULL,
                [UpdatedRecords] int NOT NULL,
                [UnchangedRecords] int NOT NULL,
                [SkippedRows] int NOT NULL,
                [ErrorRows] int NOT NULL
            );
            CREATE INDEX [IX_EstimatingHistoryImportBatches_ImportedAt]
                ON [EstimatingHistoryImportBatches] ([ImportedAt]);
        END;

        IF OBJECT_ID(N'[EstimatingQuoteHistory]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingQuoteHistory] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EstimatingQuoteHistory] PRIMARY KEY,
                [SourceId] nvarchar(80) NOT NULL,
                [QuoteNumber] int NOT NULL,
                [Customer] nvarchar(240) NOT NULL,
                [CustomerContact] nvarchar(240) NULL,
                [SalesPerson] nvarchar(160) NOT NULL,
                [QuoteStatus] nvarchar(80) NOT NULL,
                [RfqReferenceNumber] nvarchar(500) NULL,
                [EstimatingRep] nvarchar(160) NOT NULL,
                [TotalValue] decimal(18,2) NOT NULL,
                [RfqDueDate] datetime2 NULL,
                [DateToEstimating] datetime2 NULL,
                [Issues] nvarchar(240) NULL,
                [QuoteOnTrack] nvarchar(40) NULL,
                [QuoteComplexity] nvarchar(80) NULL,
                [NumberOfParts] int NOT NULL,
                [EstimatingStatus] nvarchar(160) NULL,
                [EstimatingCompletionDate] datetime2 NULL,
                [OnTimeStatus] nvarchar(24) NOT NULL,
                [DaysLate] int NOT NULL,
                [Workdays] int NULL,
                [CompletedMonth] nvarchar(16) NULL,
                [CompletedYear] int NULL,
                [CompletedWeekOfMonth] int NULL,
                [CompletedMonthAndWeek] nvarchar(40) NULL,
                [IsCompleted] bit NOT NULL,
                [CompletedWeekOfYear] int NULL,
                [IsOnTime] bit NOT NULL,
                [OnTimeRatio] decimal(8,4) NULL,
                [LastImportBatchId] uniqueidentifier NOT NULL,
                [FirstImportedAt] datetimeoffset NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                [Version] int NOT NULL CONSTRAINT [DF_EstimatingQuoteHistory_Version] DEFAULT 0
            );
            CREATE UNIQUE INDEX [IX_EstimatingQuoteHistory_SourceId]
                ON [EstimatingQuoteHistory] ([SourceId]);
            CREATE UNIQUE INDEX [IX_EstimatingQuoteHistory_QuoteNumber]
                ON [EstimatingQuoteHistory] ([QuoteNumber]);
            CREATE INDEX [IX_EstimatingQuoteHistory_EstimatingRep]
                ON [EstimatingQuoteHistory] ([EstimatingRep]);
            CREATE INDEX [IX_EstimatingQuoteHistory_EstimatingCompletionDate]
                ON [EstimatingQuoteHistory] ([EstimatingCompletionDate]);
            CREATE INDEX [IX_EstimatingQuoteHistory_IsCompleted]
                ON [EstimatingQuoteHistory] ([IsCompleted]);
        END;

        IF COL_LENGTH(N'EstimatingQuoteHistory', N'CustomerContact') IS NULL
            ALTER TABLE [EstimatingQuoteHistory] ADD [CustomerContact] nvarchar(240) NULL;
        IF COL_LENGTH(N'EstimatingQuoteHistory', N'RfqReferenceNumber') IS NULL
            ALTER TABLE [EstimatingQuoteHistory] ADD [RfqReferenceNumber] nvarchar(500) NULL;
        IF COL_LENGTH(N'EstimatingQuoteHistory', N'QuoteOnTrack') IS NULL
            ALTER TABLE [EstimatingQuoteHistory] ADD [QuoteOnTrack] nvarchar(40) NULL;

        IF EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[EstimatingQuoteHistory]')
              AND [name] = N'IX_EstimatingQuoteHistory_QuoteNumber'
              AND [is_unique] = 0)
          AND NOT EXISTS (
            SELECT [QuoteNumber]
            FROM [EstimatingQuoteHistory]
            GROUP BY [QuoteNumber]
            HAVING COUNT(*) > 1)
        BEGIN
            DROP INDEX [IX_EstimatingQuoteHistory_QuoteNumber] ON [EstimatingQuoteHistory];
            CREATE UNIQUE INDEX [IX_EstimatingQuoteHistory_QuoteNumber]
                ON [EstimatingQuoteHistory] ([QuoteNumber]);
        END;

        IF OBJECT_ID(N'[EstimatingQuoteHistoryAudits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingQuoteHistoryAudits] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EstimatingQuoteHistoryAudits] PRIMARY KEY,
                [QuoteHistoryId] int NOT NULL,
                [QuoteNumber] int NOT NULL,
                [ImportBatchId] uniqueidentifier NOT NULL,
                [Action] nvarchar(24) NOT NULL,
                [FieldName] nvarchar(120) NOT NULL,
                [OldValue] nvarchar(1000) NULL,
                [NewValue] nvarchar(1000) NULL,
                [ChangedBy] nvarchar(160) NOT NULL,
                [ChangedAt] datetimeoffset NOT NULL,
                CONSTRAINT [FK_EstimatingQuoteHistoryAudits_EstimatingQuoteHistory_QuoteHistoryId]
                    FOREIGN KEY ([QuoteHistoryId]) REFERENCES [EstimatingQuoteHistory] ([Id]) ON DELETE NO ACTION
            );
            CREATE INDEX [IX_EstimatingQuoteHistoryAudits_QuoteHistoryId_ChangedAt]
                ON [EstimatingQuoteHistoryAudits] ([QuoteHistoryId], [ChangedAt]);
            CREATE INDEX [IX_EstimatingQuoteHistoryAudits_ImportBatchId]
                ON [EstimatingQuoteHistoryAudits] ([ImportBatchId]);
        END;

        INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
        SELECT DISTINCT source.[AppGroupId], 'estimating.history.view', SYSDATETIMEOFFSET()
        FROM [GroupPermissions] source
        WHERE source.[PermissionKey] = 'estimating.view'
          AND NOT EXISTS (
              SELECT 1 FROM [GroupPermissions] existing
              WHERE existing.[AppGroupId] = source.[AppGroupId]
                AND existing.[PermissionKey] = 'estimating.history.view');

        INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
        SELECT DISTINCT source.[AppGroupId], 'estimating.history.import', SYSDATETIMEOFFSET()
        FROM [GroupPermissions] source
        WHERE source.[PermissionKey] IN (
            'estimating.quotes.manage',
            'estimating.inputs.manage',
            'estimating.rates.admin',
            'estimating.settings.admin')
          AND NOT EXISTS (
              SELECT 1 FROM [GroupPermissions] existing
              WHERE existing.[AppGroupId] = source.[AppGroupId]
                AND existing.[PermissionKey] = 'estimating.history.import');
        """;

    private async Task EnsureSqliteHistoryColumnsAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenFinished = connection.State == ConnectionState.Closed;
        if (closeWhenFinished)
            await connection.OpenAsync(cancellationToken);

        try
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"EstimatingQuoteHistory\")";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }

            if (!columns.Contains("CustomerContact"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"EstimatingQuoteHistory\" ADD COLUMN \"CustomerContact\" TEXT NULL", cancellationToken);
            if (!columns.Contains("RfqReferenceNumber"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"EstimatingQuoteHistory\" ADD COLUMN \"RfqReferenceNumber\" TEXT NULL", cancellationToken);
            if (!columns.Contains("QuoteOnTrack"))
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"EstimatingQuoteHistory\" ADD COLUMN \"QuoteOnTrack\" TEXT NULL", cancellationToken);

            await using var duplicateCommand = connection.CreateCommand();
            duplicateCommand.CommandText = """
                SELECT COUNT(*)
                FROM (
                    SELECT "QuoteNumber"
                    FROM "EstimatingQuoteHistory"
                    GROUP BY "QuoteNumber"
                    HAVING COUNT(*) > 1
                )
                """;
            var duplicateCount = Convert.ToInt32(await duplicateCommand.ExecuteScalarAsync(cancellationToken));
            if (duplicateCount == 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP INDEX IF EXISTS \"IX_EstimatingQuoteHistory_QuoteNumber\"",
                    cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX \"IX_EstimatingQuoteHistory_QuoteNumber\" ON \"EstimatingQuoteHistory\" (\"QuoteNumber\")",
                    cancellationToken);
            }
        }
        finally
        {
            if (closeWhenFinished)
                await connection.CloseAsync();
        }
    }
}
