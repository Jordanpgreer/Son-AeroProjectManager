using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations;

[DbContext(typeof(ProjectTrackerDbContext))]
[Migration("20260820150000_AddNotificationSnoozeAndScheduleResponses")]
public partial class AddNotificationSnoozeAndScheduleResponses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "SnoozedUntil",
            table: "UserNotifications",
            type: "date",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "DELETE FROM [UserNotifications] WHERE [Kind] IN (N'OperationStartResponse', N'OperationFinishResponse');");

        migrationBuilder.DropColumn(
            name: "SnoozedUntil",
            table: "UserNotifications");
    }
}
