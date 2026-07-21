using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ProjectTracker.Api.Data;

public static partial class SqliteCompatibility
{
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
