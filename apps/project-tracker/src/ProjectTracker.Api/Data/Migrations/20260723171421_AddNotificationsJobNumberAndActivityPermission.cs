using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationsJobNumberAndActivityPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobNumber",
                table: "Projects",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ProjectTaskId = table.Column<int>(type: "int", nullable: true),
                    ProjectMessageId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorAccountName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ActorDisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    BodyPreview = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_ProjectMessages_ProjectMessageId",
                        column: x => x.ProjectMessageId,
                        principalTable: "ProjectMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserNotifications_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserNotifications_Tasks_ProjectTaskId",
                        column: x => x.ProjectTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UserNotifications_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_ProjectId",
                table: "UserNotifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_ProjectMessageId",
                table: "UserNotifications",
                column: "ProjectMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_ProjectTaskId",
                table: "UserNotifications",
                column: "ProjectTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_RecipientUserId_ReadAt_CreatedAt",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "ReadAt", "CreatedAt" });

            migrationBuilder.Sql(
                """
                INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
                SELECT source.[AppGroupId], 'project.activity.view', SYSUTCDATETIME()
                FROM [GroupPermissions] source
                WHERE source.[PermissionKey] = 'module.view'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM [GroupPermissions] existing
                      WHERE existing.[AppGroupId] = source.[AppGroupId]
                        AND existing.[PermissionKey] = 'project.activity.view');

                INSERT INTO [GroupPermissions] ([AppGroupId], [PermissionKey], [CreatedAt])
                SELECT source.[AppGroupId], 'project.edit.jobNumber', SYSUTCDATETIME()
                FROM [GroupPermissions] source
                WHERE source.[PermissionKey] = 'project.edit.salesOrderNumber'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM [GroupPermissions] existing
                      WHERE existing.[AppGroupId] = source.[AppGroupId]
                        AND existing.[PermissionKey] = 'project.edit.jobNumber');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM [GroupPermissions]
                WHERE [PermissionKey] IN ('project.activity.view', 'project.edit.jobNumber');
                """);

            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "JobNumber",
                table: "Projects");
        }
    }
}
