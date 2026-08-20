using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ProjectTracker.Api.Data;

public static partial class SqliteCompatibility
{
    public static async Task RepairLegacySchemaAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var writableSchema = connection.CreateCommand();
            writableSchema.CommandText = "PRAGMA writable_schema=ON;";
            await writableSchema.ExecuteNonQueryAsync(cancellationToken);

            try
            {
                await using var tableCheck = connection.CreateCommand();
                tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'StatusHistory';";
                var statusHistoryExists = Convert.ToInt32(await tableCheck.ExecuteScalarAsync(cancellationToken)) > 0;
                if (statusHistoryExists) return;

                await using var removeOrphanedIndexes = connection.CreateCommand();
                removeOrphanedIndexes.CommandText = """
                    DELETE FROM sqlite_schema
                    WHERE type = 'index'
                      AND tbl_name = 'StatusHistory'
                      AND name IN ('IX_StatusHistory_ProjectId', 'IX_StatusHistory_ProjectTaskId');
                    """;
                var removed = await removeOrphanedIndexes.ExecuteNonQueryAsync(cancellationToken);
                if (removed == 0) return;

                await using var schemaVersion = connection.CreateCommand();
                schemaVersion.CommandText = "PRAGMA schema_version;";
                var nextVersion = Convert.ToInt32(await schemaVersion.ExecuteScalarAsync(cancellationToken)) + 1;

                await using var updateSchemaVersion = connection.CreateCommand();
                updateSchemaVersion.CommandText = $"PRAGMA schema_version={nextVersion};";
                await updateSchemaVersion.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await using var readOnlySchema = connection.CreateCommand();
                readOnlySchema.CommandText = "PRAGMA writable_schema=OFF;";
                await readOnlySchema.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (connection.State != ConnectionState.Closed) await connection.CloseAsync();
        }
    }

    public static Task EnsureTextColumnAsync(ProjectTrackerDbContext db, string table, string column, CancellationToken cancellationToken) =>
        EnsureColumnAsync(db, table, column, "TEXT NULL", cancellationToken);

    public static Task EnsureBooleanColumnAsync(ProjectTrackerDbContext db, string table, string column, CancellationToken cancellationToken) =>
        EnsureColumnAsync(db, table, column, "INTEGER NOT NULL DEFAULT 0", cancellationToken);

    public static Task EnsureNullableIntegerColumnAsync(ProjectTrackerDbContext db, string table, string column, CancellationToken cancellationToken) =>
        EnsureColumnAsync(db, table, column, "INTEGER NULL", cancellationToken);

    public static Task EnsureLongColumnAsync(ProjectTrackerDbContext db, string table, string column, CancellationToken cancellationToken) =>
        EnsureColumnAsync(db, table, column, "INTEGER NOT NULL DEFAULT 1", cancellationToken);

    public static async Task EnsureLegacyTablesAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        const string commandText = """
            CREATE TABLE IF NOT EXISTS "WorkCenters" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkCenters" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkCenters_Name" ON "WorkCenters" ("Name");
            CREATE TABLE IF NOT EXISTS "ScheduleSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ScheduleSettings" PRIMARY KEY,
                "WorkingDaysMask" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "TaskOvertimeDays" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TaskOvertimeDays" PRIMARY KEY AUTOINCREMENT,
                "ProjectTaskId" INTEGER NOT NULL,
                "Date" TEXT NOT NULL,
                "Note" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_TaskOvertimeDays_Tasks_ProjectTaskId" FOREIGN KEY ("ProjectTaskId") REFERENCES "Tasks" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TaskOvertimeDays_ProjectTaskId_Date" ON "TaskOvertimeDays" ("ProjectTaskId", "Date");
            CREATE TABLE IF NOT EXISTS "ProjectMessages" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProjectMessages" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "AuthorAccountName" TEXT NOT NULL,
                "AuthorDisplayName" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ProjectMessages_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ProjectMessages_ProjectId_CreatedAt" ON "ProjectMessages" ("ProjectId", "CreatedAt");
            CREATE TABLE IF NOT EXISTS "ProjectAuditEntries" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProjectAuditEntries" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "ProjectTaskId" INTEGER NULL,
                "Action" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "ChangesJson" TEXT NOT NULL,
                "ChangedByAccountName" TEXT NOT NULL,
                "ChangedByDisplayName" TEXT NOT NULL,
                "ChangedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ProjectAuditEntries_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ProjectAuditEntries_ProjectId_ChangedAt" ON "ProjectAuditEntries" ("ProjectId", "ChangedAt");
            CREATE TABLE IF NOT EXISTS "UserNotifications" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserNotifications" PRIMARY KEY AUTOINCREMENT,
                "RecipientUserId" INTEGER NOT NULL,
                "ProjectId" INTEGER NOT NULL,
                "ProjectTaskId" INTEGER NULL,
                "ProjectMessageId" INTEGER NULL,
                "Kind" TEXT NOT NULL,
                "ActorAccountName" TEXT NOT NULL,
                "ActorDisplayName" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "BodyPreview" TEXT NOT NULL,
                "ScheduledDate" TEXT NULL,
                "SnoozedUntil" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ReadAt" TEXT NULL,
                "RespondedAt" TEXT NULL,
                CONSTRAINT "FK_UserNotifications_Users_RecipientUserId" FOREIGN KEY ("RecipientUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_UserNotifications_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_UserNotifications_Tasks_ProjectTaskId" FOREIGN KEY ("ProjectTaskId") REFERENCES "Tasks" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_UserNotifications_ProjectMessages_ProjectMessageId" FOREIGN KEY ("ProjectMessageId") REFERENCES "ProjectMessages" ("Id") ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_UserNotifications_ProjectMessageId" ON "UserNotifications" ("ProjectMessageId");
            CREATE INDEX IF NOT EXISTS "IX_UserNotifications_ProjectTaskId" ON "UserNotifications" ("ProjectTaskId");
            CREATE INDEX IF NOT EXISTS "IX_UserNotifications_RecipientUserId_ReadAt_CreatedAt" ON "UserNotifications" ("RecipientUserId", "ReadAt", "CreatedAt");
            CREATE TABLE IF NOT EXISTS "StatusHistory" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_StatusHistory" PRIMARY KEY AUTOINCREMENT,
                "ProjectId" INTEGER NOT NULL,
                "ProjectTaskId" INTEGER NULL,
                "EntityName" TEXT NOT NULL,
                "OldStatus" TEXT NOT NULL,
                "NewStatus" TEXT NOT NULL,
                "ChangedBy" TEXT NOT NULL,
                "ChangedAt" TEXT NOT NULL,
                CONSTRAINT "FK_StatusHistory_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_StatusHistory_Tasks_ProjectTaskId" FOREIGN KEY ("ProjectTaskId") REFERENCES "Tasks" ("Id")
            );
            CREATE INDEX IF NOT EXISTS "IX_StatusHistory_ProjectId" ON "StatusHistory" ("ProjectId");
            CREATE INDEX IF NOT EXISTS "IX_StatusHistory_ProjectTaskId" ON "StatusHistory" ("ProjectTaskId");
            """;

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public static async Task EnsureAccessControlTablesAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        const string commandText = """
            CREATE TABLE IF NOT EXISTS "Groups" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Groups" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Description" TEXT NULL,
                "IsSystemGroup" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Groups_Name" ON "Groups" ("Name");
            CREATE TABLE IF NOT EXISTS "UserGroupMemberships" (
                "AppUserId" INTEGER NOT NULL,
                "AppGroupId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_UserGroupMemberships" PRIMARY KEY ("AppUserId", "AppGroupId"),
                CONSTRAINT "FK_UserGroupMemberships_Users_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_UserGroupMemberships_Groups_AppGroupId" FOREIGN KEY ("AppGroupId") REFERENCES "Groups" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_UserGroupMemberships_AppGroupId" ON "UserGroupMemberships" ("AppGroupId");
            CREATE TABLE IF NOT EXISTS "GroupPermissions" (
                "AppGroupId" INTEGER NOT NULL,
                "PermissionKey" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_GroupPermissions" PRIMARY KEY ("AppGroupId", "PermissionKey"),
                CONSTRAINT "FK_GroupPermissions_Groups_AppGroupId" FOREIGN KEY ("AppGroupId") REFERENCES "Groups" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "UserModuleAccess" (
                "AppUserId" INTEGER NOT NULL,
                "ModuleKey" TEXT NOT NULL,
                "Role" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_UserModuleAccess" PRIMARY KEY ("AppUserId", "ModuleKey"),
                CONSTRAINT "FK_UserModuleAccess_Users_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "AccessPreviewSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AccessPreviewSessions" PRIMARY KEY,
                "TokenHash" TEXT NOT NULL,
                "AdministratorAccountName" TEXT NOT NULL,
                "TargetKey" TEXT NOT NULL,
                "ApplicationId" TEXT NOT NULL,
                "IssuedAt" TEXT NOT NULL,
                "LaunchExpiresAt" TEXT NOT NULL,
                "SessionExpiresAt" TEXT NOT NULL,
                "RedeemedAt" TEXT NULL,
                "RevokedAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccessPreviewSessions_TokenHash" ON "AccessPreviewSessions" ("TokenHash");
            CREATE INDEX IF NOT EXISTS "IX_AccessPreviewSessions_ApplicationId_SessionExpiresAt" ON "AccessPreviewSessions" ("ApplicationId", "SessionExpiresAt");
            CREATE TABLE IF NOT EXISTS "PushSubscriptions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PushSubscriptions" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "Endpoint" TEXT NOT NULL,
                "P256dh" TEXT NOT NULL,
                "Auth" TEXT NOT NULL,
                "ExpirationTime" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_PushSubscriptions_Users_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PushSubscriptions_Endpoint" ON "PushSubscriptions" ("Endpoint");
            CREATE INDEX IF NOT EXISTS "IX_PushSubscriptions_AppUserId" ON "PushSubscriptions" ("AppUserId");
            """;

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public static async Task EnsureLocalPermissionSeedAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        const string commandText = """
            CREATE TABLE IF NOT EXISTS "ProjectTrackerSchemaState" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_ProjectTrackerSchemaState" PRIMARY KEY,
                "AppliedAt" TEXT NOT NULL
            );
            INSERT INTO "GroupPermissions" ("AppGroupId", "PermissionKey", "CreatedAt")
            SELECT source."AppGroupId", 'project.activity.view', CURRENT_TIMESTAMP
            FROM "GroupPermissions" source
            WHERE source."PermissionKey" = 'module.view'
              AND NOT EXISTS (
                  SELECT 1
                  FROM "GroupPermissions" existing
                  WHERE existing."AppGroupId" = source."AppGroupId"
                    AND existing."PermissionKey" = 'project.activity.view')
              AND NOT EXISTS (
                  SELECT 1 FROM "ProjectTrackerSchemaState" state
                  WHERE state."Key" = '20260723-local-permissions');
            INSERT INTO "GroupPermissions" ("AppGroupId", "PermissionKey", "CreatedAt")
            SELECT source."AppGroupId", 'project.edit.jobNumber', CURRENT_TIMESTAMP
            FROM "GroupPermissions" source
            WHERE source."PermissionKey" = 'project.edit.salesOrderNumber'
              AND NOT EXISTS (
                  SELECT 1
                  FROM "GroupPermissions" existing
                  WHERE existing."AppGroupId" = source."AppGroupId"
                    AND existing."PermissionKey" = 'project.edit.jobNumber')
              AND NOT EXISTS (
                  SELECT 1 FROM "ProjectTrackerSchemaState" state
                  WHERE state."Key" = '20260723-local-permissions');
            INSERT OR IGNORE INTO "ProjectTrackerSchemaState" ("Key", "AppliedAt")
            VALUES ('20260723-local-permissions', CURRENT_TIMESTAMP);
            """;

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public static async Task EnsureOperationScheduleReminderIndexAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserNotifications_RecipientUserId_ProjectTaskId_Kind_ScheduledDate"
                ON "UserNotifications" ("RecipientUserId", "ProjectTaskId", "Kind", "ScheduledDate")
                WHERE "ScheduledDate" IS NOT NULL;
            """;

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (connection.State != ConnectionState.Closed) await connection.CloseAsync();
        }
    }

    private static async Task EnsureColumnAsync(
        ProjectTrackerDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!SqlIdentifier().IsMatch(table) || !SqlIdentifier().IsMatch(column))
        {
            throw new ArgumentException("SQLite compatibility identifiers may contain only letters, numbers, and underscores.");
        }

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0) return;

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (connection.State != ConnectionState.Closed) await connection.CloseAsync();
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SqlIdentifier();
}
