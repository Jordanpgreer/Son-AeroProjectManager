using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations;

[DbContext(typeof(ProjectTrackerDbContext))]
[Migration("20260908211000_AddPortalPushDeliveryTracking")]
public sealed class AddPortalPushDeliveryTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PortalPushPublishedAt",
            table: "UserNotifications",
            type: "datetimeoffset",
            nullable: true);

        // Broker cutover must not replay historical inbox entries as new alerts.
        migrationBuilder.Sql(
            "UPDATE \"UserNotifications\" SET \"PortalPushPublishedAt\" = \"CreatedAt\" WHERE \"PortalPushPublishedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_UserNotifications_PortalPushPublishedAt_Id",
            table: "UserNotifications",
            columns: new[] { "PortalPushPublishedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_UserNotifications_PortalPushPublishedAt_Id",
            table: "UserNotifications");

        migrationBuilder.DropColumn(
            name: "PortalPushPublishedAt",
            table: "UserNotifications");
    }
}
