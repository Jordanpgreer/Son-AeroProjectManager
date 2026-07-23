using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessControlGroupsAndPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Viewer",
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    IsSystemGroup = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupPermissions",
                columns: table => new
                {
                    AppGroupId = table.Column<int>(type: "int", nullable: false),
                    PermissionKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPermissions", x => new { x.AppGroupId, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_GroupPermissions_Groups_AppGroupId",
                        column: x => x.AppGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupMemberships",
                columns: table => new
                {
                    AppUserId = table.Column<int>(type: "int", nullable: false),
                    AppGroupId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupMemberships", x => new { x.AppUserId, x.AppGroupId });
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_Groups_AppGroupId",
                        column: x => x.AppGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_Users_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                table: "Groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMemberships_AppGroupId",
                table: "UserGroupMemberships",
                column: "AppGroupId");

            SeedGroup(
                migrationBuilder,
                "Administrators",
                "Full administrative access to project tracker.",
                [
                    "module.view",
                    "project.create",
                    "project.edit.programName",
                    "project.edit.programManager",
                    "project.edit.engineer",
                    "project.edit.customerName",
                    "project.edit.salesOrderNumber",
                    "project.edit.priority",
                    "project.complete",
                    "project.reopen",
                    "project.archive",
                    "task.create",
                    "task.delete",
                    "task.edit.title",
                    "task.edit.workStation",
                    "task.edit.dependency",
                    "task.edit.startDateLocked",
                    "task.edit.startDate",
                    "task.edit.endDate",
                    "task.edit.originalStartDate",
                    "task.edit.originalEndDate",
                    "task.edit.estimatedDuration",
                    "task.edit.actualDuration",
                    "task.edit.percentComplete",
                    "task.edit.notes",
                    "task.edit.overtimeDays",
                    "task.edit.sequence",
                    "settings.workCalendar.manage",
                    "settings.holidays.manage",
                    "settings.workCenters.manage",
                    "import.manage",
                    "archived.restore",
                    "access.manageUsers",
                    "access.manageGroups",
                    "project.activity.view",
                    "project.edit.jobNumber"
                ]);
            SeedGroup(
                migrationBuilder,
                "Managers",
                "Project management permissions across active programs.",
                [
                    "module.view",
                    "project.create",
                    "project.edit.programName",
                    "project.edit.programManager",
                    "project.edit.engineer",
                    "project.edit.customerName",
                    "project.edit.salesOrderNumber",
                    "project.edit.priority",
                    "project.complete",
                    "project.reopen",
                    "project.archive",
                    "task.create",
                    "task.delete",
                    "task.edit.title",
                    "task.edit.workStation",
                    "task.edit.dependency",
                    "task.edit.startDateLocked",
                    "task.edit.startDate",
                    "task.edit.endDate",
                    "task.edit.originalStartDate",
                    "task.edit.originalEndDate",
                    "task.edit.estimatedDuration",
                    "task.edit.actualDuration",
                    "task.edit.percentComplete",
                    "task.edit.notes",
                    "task.edit.overtimeDays",
                    "task.edit.sequence",
                    "archived.restore",
                    "project.activity.view",
                    "project.edit.jobNumber"
                ]);
            SeedGroup(
                migrationBuilder,
                "Engineering",
                "Operation and schedule maintenance for engineering users.",
                [
                    "module.view",
                    "task.create",
                    "task.edit.title",
                    "task.edit.workStation",
                    "task.edit.dependency",
                    "task.edit.startDateLocked",
                    "task.edit.startDate",
                    "task.edit.endDate",
                    "task.edit.originalStartDate",
                    "task.edit.originalEndDate",
                    "task.edit.estimatedDuration",
                    "task.edit.actualDuration",
                    "task.edit.percentComplete",
                    "task.edit.notes",
                    "task.edit.overtimeDays",
                    "task.edit.sequence",
                    "project.activity.view"
                ]);
            SeedGroup(
                migrationBuilder,
                "Sales",
                "Commercial updates for customer-facing users.",
                [
                    "module.view",
                    "project.edit.customerName",
                    "project.edit.salesOrderNumber",
                    "project.edit.programManager",
                    "task.edit.notes",
                    "project.activity.view",
                    "project.edit.jobNumber"
                ]);
            SeedGroup(
                migrationBuilder,
                "View Only",
                "Read-only access to Project Tracker.",
                ["module.view"]);

            migrationBuilder.Sql(
                """
                INSERT INTO [UserGroupMemberships] ([AppUserId], [AppGroupId], [CreatedAt])
                SELECT
                    [user].[Id],
                    [group].[Id],
                    SYSDATETIMEOFFSET()
                FROM [Users] AS [user]
                INNER JOIN [Groups] AS [group]
                    ON [group].[Name] = CASE
                        WHEN UPPER(LTRIM(RTRIM([user].[Role]))) IN (N'ADMIN', N'ADMINISTRATOR') THEN N'Administrators'
                        WHEN UPPER(LTRIM(RTRIM([user].[Role]))) IN (N'EDITOR', N'MANAGER') THEN N'Managers'
                        ELSE N'View Only'
                    END
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM [UserGroupMemberships] AS [membership]
                    WHERE [membership].[AppUserId] = [user].[Id]
                      AND [membership].[AppGroupId] = [group].[Id]
                );
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupPermissions");

            migrationBuilder.DropTable(
                name: "UserGroupMemberships");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Viewer");
        }

        private static void SeedGroup(
            MigrationBuilder migrationBuilder,
            string name,
            string description,
            IReadOnlyList<string> permissions)
        {
            migrationBuilder.Sql(
                $"""
                INSERT INTO [Groups] ([Name], [Description], [IsSystemGroup], [CreatedAt], [UpdatedAt])
                SELECT N'{SqlLiteral(name)}', N'{SqlLiteral(description)}', CAST(1 AS bit), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
                WHERE NOT EXISTS (SELECT 1 FROM [Groups] WHERE [Name] = N'{SqlLiteral(name)}');
                """);

            foreach (var permission in permissions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(
                    $"""
                    INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
                    SELECT [Id], N'{SqlLiteral(permission)}', SYSDATETIMEOFFSET()
                    FROM [Groups]
                    WHERE [Name] = N'{SqlLiteral(name)}'
                      AND NOT EXISTS (
                          SELECT 1
                          FROM [GroupPermissions]
                          WHERE [AppGroupId] = [Groups].[Id]
                            AND [PermissionKey] = N'{SqlLiteral(permission)}'
                      );
                    """);
            }
        }

        private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }
}
