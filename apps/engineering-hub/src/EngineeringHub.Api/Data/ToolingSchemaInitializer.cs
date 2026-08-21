using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class ToolingSchemaInitializer(EngineeringDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
            await EnsureSqliteDocumentDateAsync(cancellationToken);
        }
        else if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
    }

    private async Task EnsureSqliteDocumentDateAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenFinished = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenFinished) await connection.OpenAsync(cancellationToken);
        try
        {
            var hasDocumentDate = false;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"ToolDocuments\");";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), "DocumentDate", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDocumentDate = true;
                        break;
                    }
                }
            }

            if (!hasDocumentDate)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE "ToolDocuments" ADD COLUMN "DocumentDate" TEXT NOT NULL DEFAULT '1970-01-01T00:00:00';
                    UPDATE "ToolDocuments" SET "DocumentDate" = "UploadedAt";
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenFinished) await connection.CloseAsync();
        }
    }

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "ToolLocations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolLocations" PRIMARY KEY AUTOINCREMENT,
            "Code" TEXT NOT NULL,
            "NormalizedCode" TEXT NOT NULL,
            "Description" TEXT NULL,
            "IsActive" INTEGER NOT NULL DEFAULT 1,
            "CreatedBy" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ToolLocations_NormalizedCode" ON "ToolLocations" ("NormalizedCode");

        CREATE TABLE IF NOT EXISTS "Tools" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Tools" PRIMARY KEY AUTOINCREMENT,
            "ToolNumber" TEXT NOT NULL,
            "NormalizedToolNumber" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "ToolType" TEXT NOT NULL,
            "Owner" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Notes" TEXT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0,
            "CustodyStatus" TEXT NOT NULL,
            "CurrentLocationId" INTEGER NULL,
            "CurrentHolder" TEXT NULL,
            "CurrentVendor" TEXT NULL,
            "CheckedOutAt" TEXT NULL,
            "LastAuditDate" TEXT NULL,
            "LastAuditBy" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "Version" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "FK_Tools_ToolLocations_CurrentLocationId" FOREIGN KEY ("CurrentLocationId") REFERENCES "ToolLocations" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tools_NormalizedToolNumber" ON "Tools" ("NormalizedToolNumber");
        CREATE INDEX IF NOT EXISTS "IX_Tools_CurrentLocationId" ON "Tools" ("CurrentLocationId");

        CREATE TABLE IF NOT EXISTS "ToolMovements" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolMovements" PRIMARY KEY AUTOINCREMENT,
            "ToolRecordId" INTEGER NOT NULL,
            "Type" TEXT NOT NULL,
            "LocationId" INTEGER NULL,
            "LocationCode" TEXT NULL,
            "Vendor" TEXT NULL,
            "Person" TEXT NULL,
            "Purpose" TEXT NULL,
            "InspectionConfirmed" INTEGER NULL,
            "InspectionNotes" TEXT NULL,
            "SignedOffBy" TEXT NOT NULL,
            "RecordedAt" TEXT NOT NULL,
            CONSTRAINT "FK_ToolMovements_Tools_ToolRecordId" FOREIGN KEY ("ToolRecordId") REFERENCES "Tools" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_ToolMovements_ToolLocations_LocationId" FOREIGN KEY ("LocationId") REFERENCES "ToolLocations" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ToolMovements_ToolRecordId" ON "ToolMovements" ("ToolRecordId");
        CREATE INDEX IF NOT EXISTS "IX_ToolMovements_LocationId" ON "ToolMovements" ("LocationId");

        CREATE TABLE IF NOT EXISTS "ToolHomeLocations" (
            "ToolRecordId" INTEGER NOT NULL CONSTRAINT "PK_ToolHomeLocations" PRIMARY KEY,
            "LocationId" INTEGER NOT NULL,
            CONSTRAINT "FK_ToolHomeLocations_Tools_ToolRecordId" FOREIGN KEY ("ToolRecordId") REFERENCES "Tools" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_ToolHomeLocations_ToolLocations_LocationId" FOREIGN KEY ("LocationId") REFERENCES "ToolLocations" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ToolHomeLocations_LocationId" ON "ToolHomeLocations" ("LocationId");
        INSERT OR IGNORE INTO "ToolHomeLocations" ("ToolRecordId", "LocationId")
            SELECT "Id", "CurrentLocationId" FROM "Tools" WHERE "CurrentLocationId" IS NOT NULL;
        INSERT OR IGNORE INTO "ToolHomeLocations" ("ToolRecordId", "LocationId")
            SELECT "tool"."Id",
                (SELECT "movement"."LocationId" FROM "ToolMovements" AS "movement"
                 WHERE "movement"."ToolRecordId" = "tool"."Id" AND "movement"."LocationId" IS NOT NULL
                 ORDER BY "movement"."RecordedAt" DESC, "movement"."Id" DESC LIMIT 1)
            FROM "Tools" AS "tool"
            WHERE EXISTS (
                SELECT 1 FROM "ToolMovements" AS "movement"
                WHERE "movement"."ToolRecordId" = "tool"."Id" AND "movement"."LocationId" IS NOT NULL);

        CREATE TABLE IF NOT EXISTS "ToolDocuments" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolDocuments" PRIMARY KEY AUTOINCREMENT,
            "ToolRecordId" INTEGER NOT NULL,
            "Kind" TEXT NOT NULL,
            "DocumentNumber" TEXT NULL,
            "OriginalFileName" TEXT NOT NULL,
            "StoredFilePath" TEXT NOT NULL,
            "FileType" TEXT NOT NULL,
            "FileSize" INTEGER NOT NULL,
            "FileHash" TEXT NOT NULL,
            "Notes" TEXT NULL,
            "DocumentDate" TEXT NOT NULL,
            "UploadedBy" TEXT NOT NULL,
            "UploadedAt" TEXT NOT NULL,
            CONSTRAINT "FK_ToolDocuments_Tools_ToolRecordId" FOREIGN KEY ("ToolRecordId") REFERENCES "Tools" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ToolDocuments_ToolRecordId" ON "ToolDocuments" ("ToolRecordId");

        CREATE TABLE IF NOT EXISTS "ToolPartNumbers" (
            "ToolRecordId" INTEGER NOT NULL,
            "NormalizedPartNumber" TEXT NOT NULL,
            "PartNumber" TEXT NOT NULL,
            CONSTRAINT "PK_ToolPartNumbers" PRIMARY KEY ("ToolRecordId", "NormalizedPartNumber"),
            CONSTRAINT "FK_ToolPartNumbers_Tools_ToolRecordId" FOREIGN KEY ("ToolRecordId") REFERENCES "Tools" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ToolPartNumbers_NormalizedPartNumber" ON "ToolPartNumbers" ("NormalizedPartNumber");

        CREATE TABLE IF NOT EXISTS "ToolAuditEntries" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolAuditEntries" PRIMARY KEY AUTOINCREMENT,
            "ToolRecordId" INTEGER NOT NULL,
            "Action" TEXT NOT NULL,
            "Details" TEXT NOT NULL,
            "Actor" TEXT NOT NULL,
            "OccurredAt" TEXT NOT NULL,
            CONSTRAINT "FK_ToolAuditEntries_Tools_ToolRecordId" FOREIGN KEY ("ToolRecordId") REFERENCES "Tools" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS "IX_ToolAuditEntries_ToolRecordId" ON "ToolAuditEntries" ("ToolRecordId");
        """;

    private const string SqlServerSchema = """
        IF OBJECT_ID(N'[ToolLocations]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolLocations] (
                [Id] int NOT NULL IDENTITY,
                [Code] nvarchar(60) NOT NULL,
                [NormalizedCode] nvarchar(60) NOT NULL,
                [Description] nvarchar(max) NULL,
                [IsActive] bit NOT NULL CONSTRAINT [DF_ToolLocations_IsActive] DEFAULT 1,
                [CreatedBy] nvarchar(max) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_ToolLocations] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_ToolLocations_NormalizedCode] ON [ToolLocations] ([NormalizedCode]);
        END;

        IF OBJECT_ID(N'[Tools]', N'U') IS NULL
        BEGIN
            CREATE TABLE [Tools] (
                [Id] int NOT NULL IDENTITY,
                [ToolNumber] nvarchar(100) NOT NULL,
                [NormalizedToolNumber] nvarchar(100) NOT NULL,
                [Name] nvarchar(max) NOT NULL,
                [ToolType] nvarchar(max) NOT NULL,
                [Owner] nvarchar(max) NOT NULL,
                [Description] nvarchar(max) NULL,
                [Notes] nvarchar(max) NULL,
                [IsArchived] bit NOT NULL CONSTRAINT [DF_Tools_IsArchived] DEFAULT 0,
                [CustodyStatus] nvarchar(32) NOT NULL,
                [CurrentLocationId] int NULL,
                [CurrentHolder] nvarchar(max) NULL,
                [CurrentVendor] nvarchar(max) NULL,
                [CheckedOutAt] datetime2 NULL,
                [LastAuditDate] datetime2 NULL,
                [LastAuditBy] nvarchar(max) NULL,
                [CreatedBy] nvarchar(max) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedBy] nvarchar(max) NOT NULL,
                [UpdatedAt] datetime2 NOT NULL,
                [Version] bigint NOT NULL CONSTRAINT [DF_Tools_Version] DEFAULT 0,
                CONSTRAINT [PK_Tools] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Tools_ToolLocations_CurrentLocationId] FOREIGN KEY ([CurrentLocationId]) REFERENCES [ToolLocations] ([Id])
            );
            CREATE UNIQUE INDEX [IX_Tools_NormalizedToolNumber] ON [Tools] ([NormalizedToolNumber]);
            CREATE INDEX [IX_Tools_CurrentLocationId] ON [Tools] ([CurrentLocationId]);
        END;

        IF OBJECT_ID(N'[ToolMovements]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolMovements] (
                [Id] bigint NOT NULL IDENTITY,
                [ToolRecordId] int NOT NULL,
                [Type] nvarchar(32) NOT NULL,
                [LocationId] int NULL,
                [LocationCode] nvarchar(max) NULL,
                [Vendor] nvarchar(max) NULL,
                [Person] nvarchar(max) NULL,
                [Purpose] nvarchar(max) NULL,
                [InspectionConfirmed] bit NULL,
                [InspectionNotes] nvarchar(max) NULL,
                [SignedOffBy] nvarchar(max) NOT NULL,
                [RecordedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_ToolMovements] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ToolMovements_Tools_ToolRecordId] FOREIGN KEY ([ToolRecordId]) REFERENCES [Tools] ([Id]),
                CONSTRAINT [FK_ToolMovements_ToolLocations_LocationId] FOREIGN KEY ([LocationId]) REFERENCES [ToolLocations] ([Id])
            );
            CREATE INDEX [IX_ToolMovements_ToolRecordId] ON [ToolMovements] ([ToolRecordId]);
            CREATE INDEX [IX_ToolMovements_LocationId] ON [ToolMovements] ([LocationId]);
        END;

        IF OBJECT_ID(N'[ToolHomeLocations]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolHomeLocations] (
                [ToolRecordId] int NOT NULL,
                [LocationId] int NOT NULL,
                CONSTRAINT [PK_ToolHomeLocations] PRIMARY KEY ([ToolRecordId]),
                CONSTRAINT [FK_ToolHomeLocations_Tools_ToolRecordId] FOREIGN KEY ([ToolRecordId]) REFERENCES [Tools] ([Id]),
                CONSTRAINT [FK_ToolHomeLocations_ToolLocations_LocationId] FOREIGN KEY ([LocationId]) REFERENCES [ToolLocations] ([Id])
            );
            CREATE INDEX [IX_ToolHomeLocations_LocationId] ON [ToolHomeLocations] ([LocationId]);
        END;
        INSERT INTO [ToolHomeLocations] ([ToolRecordId], [LocationId])
            SELECT [tool].[Id], COALESCE(
                [tool].[CurrentLocationId],
                (SELECT TOP 1 [movement].[LocationId] FROM [ToolMovements] AS [movement]
                 WHERE [movement].[ToolRecordId] = [tool].[Id] AND [movement].[LocationId] IS NOT NULL
                 ORDER BY [movement].[RecordedAt] DESC, [movement].[Id] DESC))
            FROM [Tools] AS [tool]
            WHERE NOT EXISTS (SELECT 1 FROM [ToolHomeLocations] AS [home] WHERE [home].[ToolRecordId] = [tool].[Id])
              AND COALESCE(
                [tool].[CurrentLocationId],
                (SELECT TOP 1 [movement].[LocationId] FROM [ToolMovements] AS [movement]
                 WHERE [movement].[ToolRecordId] = [tool].[Id] AND [movement].[LocationId] IS NOT NULL
                 ORDER BY [movement].[RecordedAt] DESC, [movement].[Id] DESC)) IS NOT NULL;

        IF OBJECT_ID(N'[ToolDocuments]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolDocuments] (
                [Id] bigint NOT NULL IDENTITY,
                [ToolRecordId] int NOT NULL,
                [Kind] nvarchar(24) NOT NULL,
                [DocumentNumber] nvarchar(max) NULL,
                [OriginalFileName] nvarchar(max) NOT NULL,
                [StoredFilePath] nvarchar(max) NOT NULL,
                [FileType] nvarchar(max) NOT NULL,
                [FileSize] bigint NOT NULL,
                [FileHash] nvarchar(max) NOT NULL,
                [Notes] nvarchar(max) NULL,
                [DocumentDate] datetime2 NOT NULL,
                [UploadedBy] nvarchar(max) NOT NULL,
                [UploadedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_ToolDocuments] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ToolDocuments_Tools_ToolRecordId] FOREIGN KEY ([ToolRecordId]) REFERENCES [Tools] ([Id])
            );
            CREATE INDEX [IX_ToolDocuments_ToolRecordId] ON [ToolDocuments] ([ToolRecordId]);
        END;

        IF COL_LENGTH(N'[ToolDocuments]', N'DocumentDate') IS NULL
        BEGIN
            ALTER TABLE [ToolDocuments] ADD [DocumentDate] datetime2 NULL;
            UPDATE [ToolDocuments] SET [DocumentDate] = [UploadedAt];
            ALTER TABLE [ToolDocuments] ALTER COLUMN [DocumentDate] datetime2 NOT NULL;
        END;

        IF OBJECT_ID(N'[ToolPartNumbers]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolPartNumbers] (
                [ToolRecordId] int NOT NULL,
                [NormalizedPartNumber] nvarchar(100) NOT NULL,
                [PartNumber] nvarchar(100) NOT NULL,
                CONSTRAINT [PK_ToolPartNumbers] PRIMARY KEY ([ToolRecordId], [NormalizedPartNumber]),
                CONSTRAINT [FK_ToolPartNumbers_Tools_ToolRecordId] FOREIGN KEY ([ToolRecordId]) REFERENCES [Tools] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_ToolPartNumbers_NormalizedPartNumber] ON [ToolPartNumbers] ([NormalizedPartNumber]);
        END;

        IF OBJECT_ID(N'[ToolAuditEntries]', N'U') IS NULL
        BEGIN
            CREATE TABLE [ToolAuditEntries] (
                [Id] bigint NOT NULL IDENTITY,
                [ToolRecordId] int NOT NULL,
                [Action] nvarchar(max) NOT NULL,
                [Details] nvarchar(max) NOT NULL,
                [Actor] nvarchar(max) NOT NULL,
                [OccurredAt] datetime2 NOT NULL,
                CONSTRAINT [PK_ToolAuditEntries] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_ToolAuditEntries_Tools_ToolRecordId] FOREIGN KEY ([ToolRecordId]) REFERENCES [Tools] ([Id])
            );
            CREATE INDEX [IX_ToolAuditEntries_ToolRecordId] ON [ToolAuditEntries] ([ToolRecordId]);
        END;
        """;
}
