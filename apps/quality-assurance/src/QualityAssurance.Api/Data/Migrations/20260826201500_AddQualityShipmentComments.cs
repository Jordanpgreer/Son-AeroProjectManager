using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20260826201500_AddQualityShipmentComments")]
public sealed class AddQualityShipmentComments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "QualityShipmentComments",
            columns: table => new
            {
                Id = table.Column<long>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ShipmentId = table.Column<int>(nullable: false),
                Body = table.Column<string>(maxLength: 8000, nullable: false),
                AuthorUserId = table.Column<int>(nullable: false),
                AuthorAccountName = table.Column<string>(maxLength: 160, nullable: false),
                AuthorDisplayName = table.Column<string>(maxLength: 160, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                IsLegacyImport = table.Column<bool>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QualityShipmentComments", x => x.Id);
                table.ForeignKey(
                    name: "FK_QualityShipmentComments_QualityShipments_ShipmentId",
                    column: x => x.ShipmentId,
                    principalTable: "QualityShipments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "QualityMentionNotifications",
            columns: table => new
            {
                Id = table.Column<long>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true)
                    .Annotation("SqlServer:Identity", "1, 1"),
                RecipientUserId = table.Column<int>(nullable: false),
                RecipientAccountName = table.Column<string>(maxLength: 160, nullable: false),
                ShipmentId = table.Column<int>(nullable: false),
                CommentId = table.Column<long>(nullable: false),
                ActorAccountName = table.Column<string>(maxLength: 160, nullable: false),
                ActorDisplayName = table.Column<string>(maxLength: 160, nullable: false),
                BodyPreview = table.Column<string>(maxLength: 300, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                ReadAt = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QualityMentionNotifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_QualityMentionNotifications_QualityShipmentComments_CommentId",
                    column: x => x.CommentId,
                    principalTable: "QualityShipmentComments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_QualityMentionNotifications_CommentId",
            table: "QualityMentionNotifications",
            column: "CommentId");

        migrationBuilder.CreateIndex(
            name: "IX_QualityMentionNotifications_RecipientUserId_ReadAt_CreatedAt",
            table: "QualityMentionNotifications",
            columns: new[] { "RecipientUserId", "ReadAt", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_QualityMentionNotifications_ShipmentId_CommentId",
            table: "QualityMentionNotifications",
            columns: new[] { "ShipmentId", "CommentId" });

        migrationBuilder.CreateIndex(
            name: "IX_QualityShipmentComments_ShipmentId_Id",
            table: "QualityShipmentComments",
            columns: new[] { "ShipmentId", "Id" });

        // Convert the previous single Comments value into the first thread message.
        // The legacy column remains as the current-message preview for old clients and exports.
        if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql("""
                INSERT INTO QualityShipmentComments
                    (ShipmentId, Body, AuthorUserId, AuthorAccountName, AuthorDisplayName, CreatedAt, IsLegacyImport)
                SELECT
                    Id,
                    LEFT(CONVERT(nvarchar(max), Comments), 8000),
                    0,
                    LEFT(CONVERT(nvarchar(max), UpdatedByAccountName), 160),
                    CASE
                        WHEN DATALENGTH(UpdatedByDisplayName) = 0 THEN 'Imported shipping record'
                        ELSE LEFT(CONVERT(nvarchar(max), UpdatedByDisplayName), 160)
                    END,
                    UpdatedAt,
                    1
                FROM QualityShipments
                WHERE Comments IS NOT NULL AND DATALENGTH(Comments) > 0;
                """);
        }
        else
        {
            migrationBuilder.Sql("""
                INSERT INTO QualityShipmentComments
                    (ShipmentId, Body, AuthorUserId, AuthorAccountName, AuthorDisplayName, CreatedAt, IsLegacyImport)
                SELECT
                    Id,
                    substr(Comments, 1, 8000),
                    0,
                    substr(UpdatedByAccountName, 1, 160),
                    CASE WHEN UpdatedByDisplayName = '' THEN 'Imported shipping record' ELSE substr(UpdatedByDisplayName, 1, 160) END,
                    UpdatedAt,
                    1
                FROM QualityShipments
                WHERE Comments IS NOT NULL AND Comments <> '';
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "QualityMentionNotifications");
        migrationBuilder.DropTable(name: "QualityShipmentComments");
    }
}
