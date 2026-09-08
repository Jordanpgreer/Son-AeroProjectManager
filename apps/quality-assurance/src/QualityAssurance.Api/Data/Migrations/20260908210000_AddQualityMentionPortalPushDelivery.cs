using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20260908210000_AddQualityMentionPortalPushDelivery")]
public sealed class AddQualityMentionPortalPushDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PortalPushPublishedAt",
            table: "QualityMentionNotifications",
            type: "datetimeoffset",
            nullable: true);

        // Do not replay historical mentions as new desktop notifications when
        // the Portal broker is enabled for the first time.
        migrationBuilder.Sql(
            "UPDATE \"QualityMentionNotifications\" SET \"PortalPushPublishedAt\" = \"CreatedAt\" WHERE \"PortalPushPublishedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_QualityMentionNotifications_PortalPushPublishedAt_Id",
            table: "QualityMentionNotifications",
            columns: new[] { "PortalPushPublishedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_QualityMentionNotifications_PortalPushPublishedAt_Id",
            table: "QualityMentionNotifications");

        migrationBuilder.DropColumn(
            name: "PortalPushPublishedAt",
            table: "QualityMentionNotifications");
    }
}
