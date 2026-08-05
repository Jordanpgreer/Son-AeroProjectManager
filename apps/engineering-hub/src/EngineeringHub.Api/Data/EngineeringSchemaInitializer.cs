using System.Data;
using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringSchemaInitializer(EngineeringDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (db.Database.IsSqlite())
            await EnsureSqliteSchemaAsync(cancellationToken);
        else if (db.Database.IsSqlServer())
            await EnsureSqlServerSchemaAsync(cancellationToken);

        await BackfillLegacyRevisionDocumentsAsync(cancellationToken);
        await EnsureSingleMylarConstraintAsync(cancellationToken);
        await BackfillLegacyMylarAsync(cancellationToken);
    }

    private async Task EnsureSqliteSchemaAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "DrawingMylars" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DrawingMylars" PRIMARY KEY AUTOINCREMENT,
                "DrawingId" INTEGER NOT NULL,
                "MylarNumber" TEXT NOT NULL,
                "NormalizedMylarNumber" TEXT NOT NULL,
                "IsCheckedOut" INTEGER NOT NULL DEFAULT 0,
                "CurrentLocation" TEXT NULL,
                "CheckedOutBy" TEXT NULL,
                "CheckedOutAt" TEXT NULL,
                "Version" INTEGER NOT NULL DEFAULT 0,
                "CreatedBy" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_DrawingMylars_Drawings_DrawingId"
                    FOREIGN KEY ("DrawingId") REFERENCES "Drawings" ("Id") ON DELETE RESTRICT
            );
            """,
            cancellationToken);

        if (!await SqliteColumnExistsAsync("MylarTransactions", "DrawingMylarId", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "MylarTransactions" ADD COLUMN "DrawingMylarId" INTEGER NULL;""",
                cancellationToken);
        if (!await SqliteColumnExistsAsync("DrawingMylars", "Version", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "DrawingMylars" ADD COLUMN "Version" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken);
        if (!await SqliteColumnExistsAsync("DrawingDocumentLinks", "DrawingRevisionId", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "DrawingDocumentLinks" ADD COLUMN "DrawingRevisionId" INTEGER NULL;""",
                cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_MylarTransactions_DrawingMylarId"
                ON "MylarTransactions" ("DrawingMylarId");
            CREATE INDEX IF NOT EXISTS "IX_DrawingDocumentLinks_DrawingRevisionId"
                ON "DrawingDocumentLinks" ("DrawingRevisionId");
            CREATE TRIGGER IF NOT EXISTS "TR_DrawingDocumentLinks_Revision_Insert"
            BEFORE INSERT ON "DrawingDocumentLinks"
            WHEN NEW."DrawingRevisionId" IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM "DrawingRevisions"
                    WHERE "Id" = NEW."DrawingRevisionId" AND "DrawingId" = NEW."DrawingId")
            BEGIN
                SELECT RAISE(ABORT, 'The supporting document revision does not belong to this drawing.');
            END;
            CREATE TRIGGER IF NOT EXISTS "TR_DrawingDocumentLinks_Revision_Update"
            BEFORE UPDATE OF "DrawingRevisionId", "DrawingId" ON "DrawingDocumentLinks"
            WHEN NEW."DrawingRevisionId" IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM "DrawingRevisions"
                    WHERE "Id" = NEW."DrawingRevisionId" AND "DrawingId" = NEW."DrawingId")
            BEGIN
                SELECT RAISE(ABORT, 'The supporting document revision does not belong to this drawing.');
            END;
            CREATE TRIGGER IF NOT EXISTS "TR_MylarTransactions_NumberedMylar_Insert"
            BEFORE INSERT ON "MylarTransactions"
            WHEN NEW."DrawingMylarId" IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM "DrawingMylars"
                    WHERE "Id" = NEW."DrawingMylarId" AND "DrawingId" = NEW."DrawingId")
            BEGIN
                SELECT RAISE(ABORT, 'The numbered Mylar does not belong to this drawing.');
            END;
            CREATE TRIGGER IF NOT EXISTS "TR_MylarTransactions_NumberedMylar_Update"
            BEFORE UPDATE OF "DrawingMylarId", "DrawingId" ON "MylarTransactions"
            WHEN NEW."DrawingMylarId" IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM "DrawingMylars"
                    WHERE "Id" = NEW."DrawingMylarId" AND "DrawingId" = NEW."DrawingId")
            BEGIN
                SELECT RAISE(ABORT, 'The numbered Mylar does not belong to this drawing.');
            END;
            """,
            cancellationToken);
    }

    private async Task<bool> SqliteColumnExistsAsync(string table, string column, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task EnsureSqlServerSchemaAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'[DrawingMylars]', N'U') IS NULL
            BEGIN
                CREATE TABLE [DrawingMylars] (
                    [Id] int NOT NULL IDENTITY,
                    [DrawingId] int NOT NULL,
                    [MylarNumber] nvarchar(100) NOT NULL,
                    [NormalizedMylarNumber] nvarchar(100) NOT NULL,
                    [IsCheckedOut] bit NOT NULL CONSTRAINT [DF_DrawingMylars_IsCheckedOut] DEFAULT 0,
                    [CurrentLocation] nvarchar(max) NULL,
                    [CheckedOutBy] nvarchar(max) NULL,
                    [CheckedOutAt] datetime2 NULL,
                    [Version] bigint NOT NULL CONSTRAINT [DF_DrawingMylars_Version] DEFAULT 0,
                    [CreatedBy] nvarchar(max) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_DrawingMylars] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_DrawingMylars_Drawings_DrawingId]
                        FOREIGN KEY ([DrawingId]) REFERENCES [Drawings] ([Id]) ON DELETE NO ACTION
                );
            END;

            IF COL_LENGTH(N'MylarTransactions', N'DrawingMylarId') IS NULL
                ALTER TABLE [MylarTransactions] ADD [DrawingMylarId] int NULL;

            IF COL_LENGTH(N'DrawingMylars', N'Version') IS NULL
                ALTER TABLE [DrawingMylars] ADD [Version] bigint NOT NULL CONSTRAINT [DF_DrawingMylars_Version_Upgrade] DEFAULT 0;

            IF COL_LENGTH(N'DrawingDocumentLinks', N'DrawingRevisionId') IS NULL
                ALTER TABLE [DrawingDocumentLinks] ADD [DrawingRevisionId] int NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE [name] = N'FK_DrawingDocumentLinks_DrawingRevisions_DrawingRevisionId')
                ALTER TABLE [DrawingDocumentLinks] WITH CHECK
                    ADD CONSTRAINT [FK_DrawingDocumentLinks_DrawingRevisions_DrawingRevisionId]
                    FOREIGN KEY ([DrawingRevisionId]) REFERENCES [DrawingRevisions] ([Id]);

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_DrawingDocumentLinks_DrawingRevisionId'
                    AND [object_id] = OBJECT_ID(N'[DrawingDocumentLinks]'))
                CREATE INDEX [IX_DrawingDocumentLinks_DrawingRevisionId]
                    ON [DrawingDocumentLinks] ([DrawingRevisionId]);

            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE [name] = N'FK_MylarTransactions_DrawingMylars_DrawingMylarId')
                ALTER TABLE [MylarTransactions] WITH CHECK
                    ADD CONSTRAINT [FK_MylarTransactions_DrawingMylars_DrawingMylarId]
                    FOREIGN KEY ([DrawingMylarId]) REFERENCES [DrawingMylars] ([Id]);

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_MylarTransactions_DrawingMylarId'
                    AND [object_id] = OBJECT_ID(N'[MylarTransactions]'))
                CREATE INDEX [IX_MylarTransactions_DrawingMylarId]
                    ON [MylarTransactions] ([DrawingMylarId]);
            """,
            cancellationToken);
    }

    private async Task BackfillLegacyRevisionDocumentsAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "DrawingDocumentLinks"
                SET "DrawingRevisionId" = COALESCE(
                    (SELECT "CurrentApprovedRevisionId" FROM "Drawings"
                     WHERE "Drawings"."Id" = "DrawingDocumentLinks"."DrawingId"),
                    (SELECT "Id" FROM "DrawingRevisions"
                     WHERE "DrawingRevisions"."DrawingId" = "DrawingDocumentLinks"."DrawingId"
                     ORDER BY "UploadedAt" DESC, "Id" DESC LIMIT 1))
                WHERE "Kind" = 'SupplementalDocument' AND "DrawingRevisionId" IS NULL;
                """,
                cancellationToken);
        }
        else if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE links
                SET [DrawingRevisionId] = COALESCE(
                    drawings.[CurrentApprovedRevisionId],
                    (SELECT TOP (1) revisions.[Id]
                     FROM [DrawingRevisions] revisions
                     WHERE revisions.[DrawingId] = links.[DrawingId]
                     ORDER BY revisions.[UploadedAt] DESC, revisions.[Id] DESC))
                FROM [DrawingDocumentLinks] links
                INNER JOIN [Drawings] drawings ON drawings.[Id] = links.[DrawingId]
                WHERE links.[Kind] = N'SupplementalDocument' AND links.[DrawingRevisionId] IS NULL;
                """,
                cancellationToken);
        }
    }

    private async Task EnsureSingleMylarConstraintAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite() && !db.Database.IsSqlServer())
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var duplicateDrawingIds = await db.DrawingMylars
            .AsNoTracking()
            .GroupBy(x => x.DrawingId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        if (duplicateDrawingIds.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "Cannot enforce one registered Mylar per drawing because legacy duplicate Mylar rows exist " +
                $"for drawing IDs: {string.Join(", ", duplicateDrawingIds)}. " +
                "Resolve those records manually before restarting Engineering Hub; no duplicate Mylar rows were modified.");
        }

        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP INDEX IF EXISTS "IX_DrawingMylars_DrawingId_NormalizedMylarNumber";
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_DrawingMylars_DrawingId"
                    ON "DrawingMylars" ("DrawingId");
                """,
                cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_DrawingMylars_DrawingId_NormalizedMylarNumber'
                        AND [object_id] = OBJECT_ID(N'[DrawingMylars]'))
                    DROP INDEX [IX_DrawingMylars_DrawingId_NormalizedMylarNumber] ON [DrawingMylars];

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE [name] = N'IX_DrawingMylars_DrawingId'
                        AND [object_id] = OBJECT_ID(N'[DrawingMylars]'))
                    CREATE UNIQUE INDEX [IX_DrawingMylars_DrawingId]
                        ON [DrawingMylars] ([DrawingId]);
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task BackfillLegacyMylarAsync(CancellationToken cancellationToken)
    {
        var drawings = await db.Drawings
            .Include(x => x.Mylars)
            .Include(x => x.MylarTransactions)
            .AsSplitQuery()
            .Where(x => x.Mylars.Count == 0 &&
                (x.PhysicalMylarLocation != null || x.IsMylarCheckedOut || x.MylarTransactions.Count > 0))
            .ToListAsync(cancellationToken);

        foreach (var drawing in drawings)
        {
            var firstTransaction = drawing.MylarTransactions.OrderBy(x => x.RecordedAt).FirstOrDefault();
            var latestTransaction = drawing.MylarTransactions.OrderByDescending(x => x.RecordedAt).FirstOrDefault();
            var mylar = new DrawingMylar
            {
                MylarNumber = "MYLAR-1",
                NormalizedMylarNumber = "MYLAR1",
                IsCheckedOut = drawing.IsMylarCheckedOut,
                CurrentLocation = drawing.PhysicalMylarLocation ?? latestTransaction?.Location,
                CheckedOutBy = drawing.IsMylarCheckedOut ? drawing.MylarCheckedOutBy ?? latestTransaction?.Person : null,
                CheckedOutAt = drawing.IsMylarCheckedOut ? drawing.MylarCheckedOutAt ?? latestTransaction?.RecordedAt : null,
                CreatedBy = firstTransaction?.RecordedBy ?? drawing.CreatedBy,
                CreatedAt = firstTransaction?.RecordedAt ?? drawing.CreatedAt
            };
            drawing.Mylars.Add(mylar);
            foreach (var transaction in drawing.MylarTransactions)
                transaction.Mylar = mylar;
        }

        if (drawings.Count > 0)
        {
            db.AllowLegacyMylarBackfill = true;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                db.AllowLegacyMylarBackfill = false;
            }
        }
    }
}
