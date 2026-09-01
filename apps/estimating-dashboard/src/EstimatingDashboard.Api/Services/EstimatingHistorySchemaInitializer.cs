using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;
using SonAero.Platform.Estimating;
using SonAero.Platform.Security;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistorySchemaInitializer(EstimatingAccessDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(EstimatorSettings.SqliteSchema, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(IntegrationCredentialSchema.Sqlite, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(OperationMappingsSqliteSchema, cancellationToken);
            await EnsureSqliteHistoryColumnsAsync(cancellationToken);
        }
        else if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(EstimatorSettings.SqlServerSchema, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(IntegrationCredentialSchema.SqlServer, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(OperationMappingsSqlServerSchema, cancellationToken);
        }

        await SeedOperationMappingsAsync(cancellationToken);
    }

    private const string OperationMappingsSqliteSchema = """
        CREATE TABLE IF NOT EXISTS "EstimatingRateReferences" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_EstimatingRateReferences" PRIMARY KEY,
            "Category" TEXT NOT NULL,
            "SourceRow" INTEGER NOT NULL,
            "OperationName" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_EstimatingRateReferences_Category_SourceRow"
            ON "EstimatingRateReferences" ("Category", "SourceRow");

        CREATE TABLE IF NOT EXISTS "EstimatingOperationMappings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EstimatingOperationMappings" PRIMARY KEY AUTOINCREMENT,
            "FulcrumOperation" TEXT NOT NULL,
            "FulcrumOperationKey" TEXT NOT NULL,
            "RateReferenceKey" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL,
            "Version" INTEGER NOT NULL DEFAULT 0,
            "CreatedAt" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            CONSTRAINT "FK_EstimatingOperationMappings_EstimatingRateReferences_RateReferenceKey"
                FOREIGN KEY ("RateReferenceKey") REFERENCES "EstimatingRateReferences" ("Key") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_EstimatingOperationMappings_FulcrumOperationKey"
            ON "EstimatingOperationMappings" ("FulcrumOperationKey");
        CREATE INDEX IF NOT EXISTS "IX_EstimatingOperationMappings_RateReferenceKey"
            ON "EstimatingOperationMappings" ("RateReferenceKey");

        CREATE TABLE IF NOT EXISTS "EstimatingOperationMappingAudits" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EstimatingOperationMappingAudits" PRIMARY KEY AUTOINCREMENT,
            "OperationMappingId" INTEGER NOT NULL,
            "Action" TEXT NOT NULL,
            "OldFulcrumOperation" TEXT NULL,
            "NewFulcrumOperation" TEXT NULL,
            "OldRateReferenceKey" TEXT NULL,
            "NewRateReferenceKey" TEXT NULL,
            "OldIsActive" INTEGER NULL,
            "NewIsActive" INTEGER NULL,
            "ChangedAt" TEXT NOT NULL,
            "ChangedBy" TEXT NOT NULL,
            CONSTRAINT "FK_EstimatingOperationMappingAudits_EstimatingOperationMappings_OperationMappingId"
                FOREIGN KEY ("OperationMappingId") REFERENCES "EstimatingOperationMappings" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_EstimatingOperationMappingAudits_OperationMappingId_ChangedAt"
            ON "EstimatingOperationMappingAudits" ("OperationMappingId", "ChangedAt");
        """;

    private const string OperationMappingsSqlServerSchema = """
        IF OBJECT_ID(N'[EstimatingRateReferences]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingRateReferences] (
                [Key] nvarchar(64) NOT NULL CONSTRAINT [PK_EstimatingRateReferences] PRIMARY KEY,
                [Category] nvarchar(40) NOT NULL,
                [SourceRow] int NOT NULL,
                [OperationName] nvarchar(160) NOT NULL,
                [IsActive] bit NOT NULL
            );
            CREATE UNIQUE INDEX [IX_EstimatingRateReferences_Category_SourceRow]
                ON [EstimatingRateReferences] ([Category], [SourceRow]);
        END;

        IF OBJECT_ID(N'[EstimatingOperationMappings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingOperationMappings] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EstimatingOperationMappings] PRIMARY KEY,
                [FulcrumOperation] nvarchar(160) NOT NULL,
                [FulcrumOperationKey] nvarchar(160) NOT NULL,
                [RateReferenceKey] nvarchar(64) NOT NULL,
                [IsActive] bit NOT NULL,
                [Version] int NOT NULL CONSTRAINT [DF_EstimatingOperationMappings_Version] DEFAULT 0,
                [CreatedAt] datetimeoffset NOT NULL,
                [CreatedBy] nvarchar(160) NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                CONSTRAINT [FK_EstimatingOperationMappings_EstimatingRateReferences_RateReferenceKey]
                    FOREIGN KEY ([RateReferenceKey]) REFERENCES [EstimatingRateReferences] ([Key]) ON DELETE NO ACTION
            );
            CREATE UNIQUE INDEX [IX_EstimatingOperationMappings_FulcrumOperationKey]
                ON [EstimatingOperationMappings] ([FulcrumOperationKey]);
            CREATE INDEX [IX_EstimatingOperationMappings_RateReferenceKey]
                ON [EstimatingOperationMappings] ([RateReferenceKey]);
        END;

        IF OBJECT_ID(N'[EstimatingOperationMappingAudits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingOperationMappingAudits] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EstimatingOperationMappingAudits] PRIMARY KEY,
                [OperationMappingId] int NOT NULL,
                [Action] nvarchar(24) NOT NULL,
                [OldFulcrumOperation] nvarchar(160) NULL,
                [NewFulcrumOperation] nvarchar(160) NULL,
                [OldRateReferenceKey] nvarchar(64) NULL,
                [NewRateReferenceKey] nvarchar(64) NULL,
                [OldIsActive] bit NULL,
                [NewIsActive] bit NULL,
                [ChangedAt] datetimeoffset NOT NULL,
                [ChangedBy] nvarchar(160) NOT NULL,
                CONSTRAINT [FK_EstimatingOperationMappingAudits_EstimatingOperationMappings_OperationMappingId]
                    FOREIGN KEY ([OperationMappingId]) REFERENCES [EstimatingOperationMappings] ([Id]) ON DELETE NO ACTION
            );
            CREATE INDEX [IX_EstimatingOperationMappingAudits_OperationMappingId_ChangedAt]
                ON [EstimatingOperationMappingAudits] ([OperationMappingId], [ChangedAt]);
        END;
        """;

    private async Task SeedOperationMappingsAsync(CancellationToken cancellationToken)
    {
        var references = await db.EstimatingRateReferences.ToDictionaryAsync(
            reference => reference.Key,
            StringComparer.OrdinalIgnoreCase,
            cancellationToken);
        foreach (var seed in EstimatingRateReferenceCatalog.References)
        {
            if (!references.TryGetValue(seed.Key, out var reference))
            {
                reference = new EstimatingRateReferenceRecord { Key = seed.Key };
                references.Add(seed.Key, reference);
                db.EstimatingRateReferences.Add(reference);
            }
            reference.Category = seed.Category;
            reference.SourceRow = seed.SourceRow;
            reference.OperationName = seed.Operation;
            reference.IsActive = true;
        }
        await db.SaveChangesAsync(cancellationToken);

        var mappings = await db.EstimatingOperationMappings
            .Select(mapping => mapping.FulcrumOperationKey)
            .ToListAsync(cancellationToken);
        var existing = mappings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        foreach (var seed in EstimatingRateReferenceCatalog.DefaultMappings)
        {
            var key = EstimatingOperationNames.Normalize(seed.FulcrumOperation);
            if (existing.Contains(key)) continue;
            var mapping = new EstimatingOperationMappingRecord
            {
                FulcrumOperation = seed.FulcrumOperation,
                FulcrumOperationKey = key,
                RateReferenceKey = seed.RateReferenceKey,
                IsActive = true,
                Version = 0,
                CreatedAt = now,
                CreatedBy = "System seed",
                UpdatedAt = now,
                UpdatedBy = "System seed"
            };
            db.EstimatingOperationMappings.Add(mapping);
            mapping.AuditHistory.Add(new EstimatingOperationMappingAuditRecord
            {
                OperationMapping = mapping,
                Action = EstimatingOperationMappingAuditActions.Created,
                NewFulcrumOperation = seed.FulcrumOperation,
                NewRateReferenceKey = seed.RateReferenceKey,
                NewIsActive = true,
                ChangedAt = now,
                ChangedBy = "System seed"
            });
        }
        await db.SaveChangesAsync(cancellationToken);
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

        CREATE TABLE IF NOT EXISTS "FulcrumQuoteSyncRuns" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_FulcrumQuoteSyncRuns" PRIMARY KEY,
            "ScheduledForUtc" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT NULL,
            "Status" TEXT NOT NULL,
            "QuotesReceived" INTEGER NOT NULL,
            "NewRecords" INTEGER NOT NULL,
            "UpdatedRecords" INTEGER NOT NULL,
            "UnchangedRecords" INTEGER NOT NULL,
            "ErrorMessage" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_FulcrumQuoteSyncRuns_ScheduledForUtc"
            ON "FulcrumQuoteSyncRuns" ("ScheduledForUtc");

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

        IF OBJECT_ID(N'[FulcrumQuoteSyncRuns]', N'U') IS NULL
        BEGIN
            CREATE TABLE [FulcrumQuoteSyncRuns] (
                [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_FulcrumQuoteSyncRuns] PRIMARY KEY,
                [ScheduledForUtc] datetimeoffset NOT NULL,
                [StartedAt] datetimeoffset NOT NULL,
                [CompletedAt] datetimeoffset NULL,
                [Status] nvarchar(24) NOT NULL,
                [QuotesReceived] int NOT NULL,
                [NewRecords] int NOT NULL,
                [UpdatedRecords] int NOT NULL,
                [UnchangedRecords] int NOT NULL,
                [ErrorMessage] nvarchar(2000) NULL
            );
            CREATE UNIQUE INDEX [IX_FulcrumQuoteSyncRuns_ScheduledForUtc]
                ON [FulcrumQuoteSyncRuns] ([ScheduledForUtc]);
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
