using EngineeringHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Engineering;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringAccessSchemaInitializer(EngineeringRoleDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSchema, cancellationToken);
            return;
        }

        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerSchema, cancellationToken);
        }
    }

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "EngineeringGroups" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EngineeringGroups" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Description" TEXT NULL,
            "IsSystemGroup" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_EngineeringGroups_Name" ON "EngineeringGroups" ("Name");
        CREATE TABLE IF NOT EXISTS "EngineeringUserGroupMemberships" (
            "AppUserId" INTEGER NOT NULL,
            "AppGroupId" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "PK_EngineeringUserGroupMemberships" PRIMARY KEY ("AppUserId", "AppGroupId"),
            CONSTRAINT "FK_EngineeringUserGroupMemberships_Users_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_EngineeringUserGroupMemberships_EngineeringGroups_AppGroupId" FOREIGN KEY ("AppGroupId") REFERENCES "EngineeringGroups" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_EngineeringUserGroupMemberships_AppGroupId" ON "EngineeringUserGroupMemberships" ("AppGroupId");
        CREATE TABLE IF NOT EXISTS "EngineeringGroupPermissions" (
            "AppGroupId" INTEGER NOT NULL,
            "PermissionKey" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "PK_EngineeringGroupPermissions" PRIMARY KEY ("AppGroupId", "PermissionKey"),
            CONSTRAINT "FK_EngineeringGroupPermissions_EngineeringGroups_AppGroupId" FOREIGN KEY ("AppGroupId") REFERENCES "EngineeringGroups" ("Id") ON DELETE CASCADE
        );
        INSERT OR IGNORE INTO "Groups" ("Name", "Description", "IsSystemGroup", "CreatedAt", "UpdatedAt")
        SELECT "Name", "Description", "IsSystemGroup", "CreatedAt", "UpdatedAt"
        FROM "EngineeringGroups";
        INSERT OR IGNORE INTO "UserGroupMemberships" ("AppUserId", "AppGroupId", "CreatedAt")
        SELECT membership."AppUserId", sharedGroup."Id", membership."CreatedAt"
        FROM "EngineeringUserGroupMemberships" membership
        INNER JOIN "EngineeringGroups" engineeringGroup ON engineeringGroup."Id" = membership."AppGroupId"
        INNER JOIN "Groups" sharedGroup ON UPPER(sharedGroup."Name") = UPPER(engineeringGroup."Name");
        INSERT OR IGNORE INTO "GroupPermissions" ("AppGroupId", "PermissionKey", "CreatedAt")
        SELECT sharedGroup."Id", permission."PermissionKey", permission."CreatedAt"
        FROM "EngineeringGroupPermissions" permission
        INNER JOIN "EngineeringGroups" engineeringGroup ON engineeringGroup."Id" = permission."AppGroupId"
        INNER JOIN "Groups" sharedGroup ON UPPER(sharedGroup."Name") = UPPER(engineeringGroup."Name");
        """ + EngineeringStorageSchema.Sqlite;

    private const string SqlServerSchema = """
        IF OBJECT_ID(N'[EngineeringGroups]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EngineeringGroups] (
                [Id] int NOT NULL IDENTITY,
                [Name] nvarchar(80) NOT NULL,
                [Description] nvarchar(240) NULL,
                [IsSystemGroup] bit NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                CONSTRAINT [PK_EngineeringGroups] PRIMARY KEY ([Id])
            );
            CREATE UNIQUE INDEX [IX_EngineeringGroups_Name] ON [EngineeringGroups] ([Name]);
        END;
        IF OBJECT_ID(N'[EngineeringUserGroupMemberships]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EngineeringUserGroupMemberships] (
                [AppUserId] int NOT NULL,
                [AppGroupId] int NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                CONSTRAINT [PK_EngineeringUserGroupMemberships] PRIMARY KEY ([AppUserId], [AppGroupId]),
                CONSTRAINT [FK_EngineeringUserGroupMemberships_Users_AppUserId] FOREIGN KEY ([AppUserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [FK_EngineeringUserGroupMemberships_EngineeringGroups_AppGroupId] FOREIGN KEY ([AppGroupId]) REFERENCES [EngineeringGroups] ([Id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_EngineeringUserGroupMemberships_AppGroupId] ON [EngineeringUserGroupMemberships] ([AppGroupId]);
        END;
        IF OBJECT_ID(N'[EngineeringGroupPermissions]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EngineeringGroupPermissions] (
                [AppGroupId] int NOT NULL,
                [PermissionKey] nvarchar(120) NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                CONSTRAINT [PK_EngineeringGroupPermissions] PRIMARY KEY ([AppGroupId], [PermissionKey]),
                CONSTRAINT [FK_EngineeringGroupPermissions_EngineeringGroups_AppGroupId] FOREIGN KEY ([AppGroupId]) REFERENCES [EngineeringGroups] ([Id]) ON DELETE CASCADE
            );
        END;
        INSERT INTO [Groups] ([Name], [Description], [IsSystemGroup], [CreatedAt], [UpdatedAt])
        SELECT source.[Name], source.[Description], source.[IsSystemGroup], source.[CreatedAt], source.[UpdatedAt]
        FROM [EngineeringGroups] source
        WHERE NOT EXISTS (SELECT 1 FROM [Groups] target WHERE UPPER(target.[Name]) = UPPER(source.[Name]));

        INSERT INTO [UserGroupMemberships] ([AppUserId], [AppGroupId], [CreatedAt])
        SELECT membership.[AppUserId], sharedGroup.[Id], membership.[CreatedAt]
        FROM [EngineeringUserGroupMemberships] membership
        INNER JOIN [EngineeringGroups] engineeringGroup ON engineeringGroup.[Id] = membership.[AppGroupId]
        INNER JOIN [Groups] sharedGroup ON UPPER(sharedGroup.[Name]) = UPPER(engineeringGroup.[Name])
        WHERE NOT EXISTS (
            SELECT 1 FROM [UserGroupMemberships] existing
            WHERE existing.[AppUserId] = membership.[AppUserId] AND existing.[AppGroupId] = sharedGroup.[Id]);

        INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
        SELECT sharedGroup.[Id], permission.[PermissionKey], permission.[CreatedAt]
        FROM [EngineeringGroupPermissions] permission
        INNER JOIN [EngineeringGroups] engineeringGroup ON engineeringGroup.[Id] = permission.[AppGroupId]
        INNER JOIN [Groups] sharedGroup ON UPPER(sharedGroup.[Name]) = UPPER(engineeringGroup.[Name])
        WHERE NOT EXISTS (
            SELECT 1 FROM [GroupPermissions] existing
            WHERE existing.[AppGroupId] = sharedGroup.[Id] AND existing.[PermissionKey] = permission.[PermissionKey]);
        """ + EngineeringStorageSchema.SqlServer;
}
