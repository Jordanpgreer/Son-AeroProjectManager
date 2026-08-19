using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationScheduleConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RespondedAt",
                table: "UserNotifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduledDate",
                table: "UserNotifications",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_RecipientUserId_ProjectTaskId_Kind_ScheduledDate",
                table: "UserNotifications",
                columns: new[] { "RecipientUserId", "ProjectTaskId", "Kind", "ScheduledDate" },
                unique: true,
                filter: "[ScheduledDate] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep the database readable by the pre-feature binary when an
            // operator deliberately rolls this migration back.
            migrationBuilder.Sql(
                "UPDATE [Tasks] SET [Status] = N'Complete' WHERE [Status] = N'CompletedLate';");
            migrationBuilder.Sql(
                "DELETE FROM [UserNotifications] WHERE [Kind] IN (N'OperationStartConfirmation', N'OperationFinishConfirmation');");

            migrationBuilder.DropIndex(
                name: "IX_UserNotifications_RecipientUserId_ProjectTaskId_Kind_ScheduledDate",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "RespondedAt",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "ScheduledDate",
                table: "UserNotifications");
        }
    }
}
