using System.Data;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;

namespace Portal.Api.Services;

public sealed class PortalRaidLogSchemaInitializer(PortalRoleDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
            await EnsureSqliteDependencyColumnsAsync(cancellationToken);
        }
        else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
    }

    private async Task EnsureSqliteDependencyColumnsAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(cancellationToken);
        try
        {
            var hasParentItemId = false;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"RaidLogItems\");";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), "ParentItemId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasParentItemId = true;
                        break;
                    }
                }
            }

            if (!hasParentItemId)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"RaidLogItems\" ADD COLUMN \"ParentItemId\" INTEGER NULL REFERENCES \"RaidLogItems\" (\"Id\") ON DELETE RESTRICT;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_RaidLogItems_ParentItemId_CompletedAt\" ON \"RaidLogItems\" (\"ParentItemId\", \"CompletedAt\");";
            await index.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    internal const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "RaidLogGroups" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RaidLogGroups" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "NormalizedName" TEXT NOT NULL,
            "Description" TEXT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            "Version" INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS "RaidLogItems" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RaidLogItems" PRIMARY KEY AUTOINCREMENT,
            "GroupId" INTEGER NOT NULL,
            "ParentItemId" INTEGER NULL,
            "Title" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Kind" TEXT NOT NULL,
            "Priority" TEXT NOT NULL,
            "AssignedToUserId" INTEGER NULL,
            "CreatedAt" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            "CompletedAt" TEXT NULL,
            "CompletedBy" TEXT NULL,
            "Version" INTEGER NOT NULL,
            CONSTRAINT "FK_RaidLogItems_RaidLogGroups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "RaidLogGroups" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_RaidLogItems_RaidLogItems_ParentItemId" FOREIGN KEY ("ParentItemId") REFERENCES "RaidLogItems" ("Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_RaidLogItems_Users_AssignedToUserId" FOREIGN KEY ("AssignedToUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL
        );
        CREATE TABLE IF NOT EXISTS "RaidLogNotes" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RaidLogNotes" PRIMARY KEY AUTOINCREMENT,
            "ItemId" INTEGER NOT NULL,
            "Body" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            CONSTRAINT "FK_RaidLogNotes_RaidLogItems_ItemId" FOREIGN KEY ("ItemId") REFERENCES "RaidLogItems" ("Id") ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS "RaidLogActivity" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RaidLogActivity" PRIMARY KEY AUTOINCREMENT,
            "ItemId" INTEGER NOT NULL,
            "Action" TEXT NOT NULL,
            "Summary" TEXT NOT NULL,
            "OccurredAt" TEXT NOT NULL,
            "Actor" TEXT NOT NULL,
            CONSTRAINT "FK_RaidLogActivity_RaidLogItems_ItemId" FOREIGN KEY ("ItemId") REFERENCES "RaidLogItems" ("Id") ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS "RaidLogWorkSessions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RaidLogWorkSessions" PRIMARY KEY AUTOINCREMENT,
            "ItemId" INTEGER NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "StartedBy" TEXT NOT NULL,
            "StartNote" TEXT NULL,
            "LastHeartbeatAt" TEXT NOT NULL,
            "StoppedAt" TEXT NULL,
            "StoppedBy" TEXT NULL,
            "StopNote" TEXT NULL,
            "StopReason" TEXT NULL,
            CONSTRAINT "FK_RaidLogWorkSessions_RaidLogItems_ItemId" FOREIGN KEY ("ItemId") REFERENCES "RaidLogItems" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_RaidLogItems_GroupId_CompletedAt_Priority" ON "RaidLogItems" ("GroupId", "CompletedAt", "Priority");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_RaidLogGroups_NormalizedName" ON "RaidLogGroups" ("NormalizedName");
        CREATE INDEX IF NOT EXISTS "IX_RaidLogItems_AssignedToUserId" ON "RaidLogItems" ("AssignedToUserId");
        CREATE INDEX IF NOT EXISTS "IX_RaidLogNotes_ItemId_CreatedAt" ON "RaidLogNotes" ("ItemId", "CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_RaidLogActivity_ItemId_OccurredAt" ON "RaidLogActivity" ("ItemId", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_RaidLogWorkSessions_ItemId_StartedAt" ON "RaidLogWorkSessions" ("ItemId", "StartedAt");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_RaidLogWorkSessions_ItemId_Open" ON "RaidLogWorkSessions" ("ItemId") WHERE "StoppedAt" IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_RaidLogWorkSessions_StartedBy_Open" ON "RaidLogWorkSessions" ("StartedBy") WHERE "StoppedAt" IS NULL;
        """;

    internal const string SqlServerSchema = """
        IF OBJECT_ID(N'[dbo].[RaidLogGroups]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[RaidLogGroups] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RaidLogGroups] PRIMARY KEY,
                [Name] nvarchar(120) NOT NULL,
                [NormalizedName] nvarchar(120) NOT NULL,
                [Description] nvarchar(500) NULL,
                [SortOrder] int NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [CreatedBy] nvarchar(160) NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                [Version] bigint NOT NULL
            );
            CREATE UNIQUE INDEX [IX_RaidLogGroups_NormalizedName] ON [dbo].[RaidLogGroups] ([NormalizedName]);
        END;
        IF OBJECT_ID(N'[dbo].[RaidLogItems]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[RaidLogItems] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RaidLogItems] PRIMARY KEY,
                [GroupId] int NOT NULL,
                [ParentItemId] int NULL,
                [Title] nvarchar(240) NOT NULL,
                [Description] nvarchar(4000) NULL,
                [Kind] nvarchar(24) NOT NULL,
                [Priority] nvarchar(24) NOT NULL,
                [AssignedToUserId] int NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [CreatedBy] nvarchar(160) NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                [CompletedAt] datetimeoffset NULL,
                [CompletedBy] nvarchar(160) NULL,
                [Version] bigint NOT NULL,
                CONSTRAINT [FK_RaidLogItems_RaidLogGroups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[RaidLogGroups] ([Id]),
                CONSTRAINT [FK_RaidLogItems_RaidLogItems_ParentItemId] FOREIGN KEY ([ParentItemId]) REFERENCES [dbo].[RaidLogItems] ([Id]),
                CONSTRAINT [FK_RaidLogItems_Users_AssignedToUserId] FOREIGN KEY ([AssignedToUserId]) REFERENCES [dbo].[Users] ([Id]) ON DELETE SET NULL
            );
            CREATE INDEX [IX_RaidLogItems_GroupId_CompletedAt_Priority] ON [dbo].[RaidLogItems] ([GroupId], [CompletedAt], [Priority]);
            CREATE INDEX [IX_RaidLogItems_AssignedToUserId] ON [dbo].[RaidLogItems] ([AssignedToUserId]);
        END;
        IF COL_LENGTH(N'dbo.RaidLogItems', N'ParentItemId') IS NULL
            ALTER TABLE [dbo].[RaidLogItems] ADD [ParentItemId] int NULL;
        IF OBJECT_ID(N'[dbo].[FK_RaidLogItems_RaidLogItems_ParentItemId]', N'F') IS NULL
            ALTER TABLE [dbo].[RaidLogItems] ADD CONSTRAINT [FK_RaidLogItems_RaidLogItems_ParentItemId] FOREIGN KEY ([ParentItemId]) REFERENCES [dbo].[RaidLogItems] ([Id]);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RaidLogItems_ParentItemId_CompletedAt' AND object_id = OBJECT_ID(N'[dbo].[RaidLogItems]'))
            CREATE INDEX [IX_RaidLogItems_ParentItemId_CompletedAt] ON [dbo].[RaidLogItems] ([ParentItemId], [CompletedAt]);
        IF OBJECT_ID(N'[dbo].[RaidLogNotes]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[RaidLogNotes] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RaidLogNotes] PRIMARY KEY,
                [ItemId] int NOT NULL,
                [Body] nvarchar(4000) NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [CreatedBy] nvarchar(160) NOT NULL,
                CONSTRAINT [FK_RaidLogNotes_RaidLogItems_ItemId] FOREIGN KEY ([ItemId]) REFERENCES [dbo].[RaidLogItems] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_RaidLogNotes_ItemId_CreatedAt] ON [dbo].[RaidLogNotes] ([ItemId], [CreatedAt]);
        END;
        IF OBJECT_ID(N'[dbo].[RaidLogActivity]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[RaidLogActivity] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RaidLogActivity] PRIMARY KEY,
                [ItemId] int NOT NULL,
                [Action] nvarchar(32) NOT NULL,
                [Summary] nvarchar(500) NOT NULL,
                [OccurredAt] datetimeoffset NOT NULL,
                [Actor] nvarchar(160) NOT NULL,
                CONSTRAINT [FK_RaidLogActivity_RaidLogItems_ItemId] FOREIGN KEY ([ItemId]) REFERENCES [dbo].[RaidLogItems] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_RaidLogActivity_ItemId_OccurredAt] ON [dbo].[RaidLogActivity] ([ItemId], [OccurredAt]);
        END;
        IF OBJECT_ID(N'[dbo].[RaidLogWorkSessions]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[RaidLogWorkSessions] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RaidLogWorkSessions] PRIMARY KEY,
                [ItemId] int NOT NULL,
                [StartedAt] datetimeoffset NOT NULL,
                [StartedBy] nvarchar(160) NOT NULL,
                [StartNote] nvarchar(2000) NULL,
                [LastHeartbeatAt] datetimeoffset NOT NULL,
                [StoppedAt] datetimeoffset NULL,
                [StoppedBy] nvarchar(160) NULL,
                [StopNote] nvarchar(2000) NULL,
                [StopReason] nvarchar(32) NULL,
                CONSTRAINT [FK_RaidLogWorkSessions_RaidLogItems_ItemId] FOREIGN KEY ([ItemId]) REFERENCES [dbo].[RaidLogItems] ([Id]) ON DELETE CASCADE
            );
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RaidLogWorkSessions_ItemId_StartedAt' AND object_id = OBJECT_ID(N'[dbo].[RaidLogWorkSessions]'))
            CREATE INDEX [IX_RaidLogWorkSessions_ItemId_StartedAt] ON [dbo].[RaidLogWorkSessions] ([ItemId], [StartedAt]);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RaidLogWorkSessions_ItemId_Open' AND object_id = OBJECT_ID(N'[dbo].[RaidLogWorkSessions]'))
            CREATE UNIQUE INDEX [IX_RaidLogWorkSessions_ItemId_Open] ON [dbo].[RaidLogWorkSessions] ([ItemId]) WHERE [StoppedAt] IS NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RaidLogWorkSessions_StartedBy_Open' AND object_id = OBJECT_ID(N'[dbo].[RaidLogWorkSessions]'))
            CREATE UNIQUE INDEX [IX_RaidLogWorkSessions_StartedBy_Open] ON [dbo].[RaidLogWorkSessions] ([StartedBy]) WHERE [StoppedAt] IS NULL;
        """;
}
