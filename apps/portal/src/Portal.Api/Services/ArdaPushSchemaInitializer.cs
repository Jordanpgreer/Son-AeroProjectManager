using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;

namespace Portal.Api.Services;

public sealed class ArdaPushSchemaInitializer(PortalRoleDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
        else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
    }

    internal const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "ArdaPushSubscriptions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ArdaPushSubscriptions" PRIMARY KEY AUTOINCREMENT,
            "AppUserId" INTEGER NOT NULL,
            "Endpoint" TEXT NOT NULL,
            "P256dh" TEXT NOT NULL,
            "Auth" TEXT NOT NULL,
            "ExpirationTime" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_ArdaPushSubscriptions_Users_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ArdaPushSubscriptions_Endpoint" ON "ArdaPushSubscriptions" ("Endpoint");
        CREATE INDEX IF NOT EXISTS "IX_ArdaPushSubscriptions_AppUserId" ON "ArdaPushSubscriptions" ("AppUserId");
        CREATE TABLE IF NOT EXISTS "ArdaPushNotifications" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ArdaPushNotifications" PRIMARY KEY AUTOINCREMENT,
            "RecipientAccountName" TEXT NOT NULL,
            "SourceModule" TEXT NOT NULL,
            "SourceNotificationKey" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "Body" TEXT NOT NULL,
            "TargetUrl" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "NextAttemptAt" TEXT NOT NULL,
            "DeliveredAt" TEXT NULL,
            "DeliveryMethod" TEXT NULL,
            "AttemptCount" INTEGER NOT NULL,
            "LastError" TEXT NULL,
            "ForegroundClaimedBy" TEXT NULL,
            "ForegroundClaimExpiresAt" TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ArdaPushNotifications_SourceModule_SourceNotificationKey" ON "ArdaPushNotifications" ("SourceModule", "SourceNotificationKey");
        CREATE INDEX IF NOT EXISTS "IX_ArdaPushNotifications_DeliveredAt_NextAttemptAt" ON "ArdaPushNotifications" ("DeliveredAt", "NextAttemptAt");
        """;

    internal const string SqlServerSchema = """
        IF OBJECT_ID(N'[dbo].[ArdaPushSubscriptions]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ArdaPushSubscriptions] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ArdaPushSubscriptions] PRIMARY KEY,
                [AppUserId] int NOT NULL,
                [Endpoint] nvarchar(2048) NOT NULL,
                [EndpointHash] AS CONVERT(binary(32), HASHBYTES('SHA2_256', [Endpoint])) PERSISTED,
                [P256dh] nvarchar(256) NOT NULL,
                [Auth] nvarchar(128) NOT NULL,
                [ExpirationTime] datetimeoffset NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                CONSTRAINT [FK_ArdaPushSubscriptions_Users_AppUserId] FOREIGN KEY ([AppUserId]) REFERENCES [dbo].[Users] ([Id]) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX [IX_ArdaPushSubscriptions_EndpointHash] ON [dbo].[ArdaPushSubscriptions] ([EndpointHash]);
            CREATE INDEX [IX_ArdaPushSubscriptions_AppUserId] ON [dbo].[ArdaPushSubscriptions] ([AppUserId]);
        END;
        IF OBJECT_ID(N'[dbo].[ArdaPushNotifications]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ArdaPushNotifications] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ArdaPushNotifications] PRIMARY KEY,
                [RecipientAccountName] nvarchar(160) NOT NULL,
                [SourceModule] nvarchar(64) NOT NULL,
                [SourceNotificationKey] nvarchar(160) NOT NULL,
                [Title] nvarchar(160) NOT NULL,
                [Body] nvarchar(500) NOT NULL,
                [TargetUrl] nvarchar(2048) NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [NextAttemptAt] datetimeoffset NOT NULL,
                [DeliveredAt] datetimeoffset NULL,
                [DeliveryMethod] nvarchar(24) NULL,
                [AttemptCount] int NOT NULL,
                [LastError] nvarchar(1000) NULL,
                [ForegroundClaimedBy] nvarchar(256) NULL,
                [ForegroundClaimExpiresAt] datetimeoffset NULL
            );
            CREATE UNIQUE INDEX [IX_ArdaPushNotifications_SourceModule_SourceNotificationKey]
                ON [dbo].[ArdaPushNotifications] ([SourceModule], [SourceNotificationKey]);
            CREATE INDEX [IX_ArdaPushNotifications_DeliveredAt_NextAttemptAt]
                ON [dbo].[ArdaPushNotifications] ([DeliveredAt], [NextAttemptAt]);
        END;
        """;
}
